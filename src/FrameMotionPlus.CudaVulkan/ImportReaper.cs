using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vulkan;

namespace FrameMotionPlus.CudaVulkan;

/// <summary>
/// Frees ring imports that a bridge could not free when it was disposed, because producer work queued against them had
/// not finished within the drain timeout (a slow producer, a write whose signal was never enqueued, or writes waiting
/// for releases still to come). Freeing them then would block (<c>cuMemFree</c> waits for all queued work in the
/// context), possibly for good, so the bridge hands them here instead, with the retired semaphores (destroying one
/// synchronises the context in the same way). A background thread looks at each one every few milliseconds.
/// <para>
/// <b>It frees nothing while any bridge is live on the import's CUDA context.</b> On this driver, while a free waits
/// for queued work, kernel launches, event records and semaphore enqueues on every other thread block too. A live
/// pipeline whose queued writes wait for releases that only its own next present gives (a producer that enqueues
/// ahead of its presents) would then deadlock behind the reaper's free. So an import, even one whose work is done, is
/// held until the last bridge on its context (counted per native context handle, from construction to the end of
/// Dispose) has been disposed, and freed then. Checking that count and freeing happen under one lock, so a bridge
/// created meanwhile waits for that free (which by then has nothing left to wait for). Holding costs one ring's imports
/// per abandoned bridge, on an error path.
/// </para>
/// <list type="bullet">
/// <item>While the presenter owns the ring it queues nothing on the GPU (a GPU-side wait that cannot complete makes
/// every free in the context block): it polls <em>written</em> from the host
/// (<see cref="PresentationRing.TryReadWrittenValue"/>, safe against the presenter destroying the ring meanwhile). Once
/// it has reached the last write handed out, the work is done and the import can be freed (when no bridge is
/// live).</item>
/// <item>Once the presenter has let go of the ring (replaced it or been disposed) nothing else will release these
/// writes, so the reaper releases them from the CUDA side if the presenter had not (never to a lower value: not when a
/// later bridge has handed out writes on that ring, and what the presenter released is read generation-checked).
/// After that, every write's queued work can finish: a write whose signal was never enqueued has no work queued at
/// all. So it queues no GPU-side wait (one for a write that never comes would wedge the context for good) and frees
/// the import, when no bridge is live; the free's own wait covers whatever producer work is still running. Only a
/// probe the bridge's own drain queued (once the presenter had let go before the bridge was disposed) is waited for,
/// by polling, before freeing.</item>
/// <item>An entry whose CUDA context is gone is dropped, not polled for ever: the driver reports the context destroyed,
/// deinitialised or invalid, or it hit a sticky error (an illegal address, a failed launch, …: every later call fails
/// the same way), or the <see cref="CudaContext"/> object was disposed before the reaper could create the stream it
/// needs for a CUDA-side release (it creates one at its first look, so disposing the context object right after the
/// presenter does not stop it). Nothing can be freed then; <see cref="Dropped"/> counts them.</item>
/// </list>
/// Off the present and pacing threads; the thread is a background thread, so it never keeps the process alive. It sleeps
/// between looks; the only call in which it can wait is the free itself, made once nothing is live and the work is done
/// (or, after the presenter let go, while the abandoned producer's own remaining GPU work finishes).
/// </summary>
internal static class ImportReaper
{
    private const int PollMs = 5;

    private static readonly object Gate = new(); // the entries; Monitor.Wait/Pulse wake the thread when an import arrives
    private static readonly Lock LiveGate = new(); // the live-bridge counts, and every free the reaper makes
    private static readonly Dictionary<nint, int> LiveBridges = [];
    private static readonly List<Entry> Entries = [];
    private static Thread? _thread;
    private static int _pending;
    private static int _dropped;

    /// <summary>Imports handed over and not yet freed (or dropped), including those held while a bridge is live.</summary>
    public static int Pending => Volatile.Read(ref _pending);

    /// <summary>Entries dropped because their CUDA context was gone (nothing could be freed).</summary>
    public static int Dropped => Volatile.Read(ref _dropped);

    /// <summary>A bridge on the native context <paramref name="context"/> is live from now on (its constructor).</summary>
    public static void BridgeCreated(nint context)
    {
        lock (LiveGate)
        {
            LiveBridges[context] = LiveBridges.GetValueOrDefault(context) + 1;
        }
    }

    /// <summary>That bridge is no longer live (the end of its Dispose); the reaper may free on the context once none is.</summary>
    public static void BridgeDisposed(nint context)
    {
        lock (LiveGate)
        {
            var count = LiveBridges.GetValueOrDefault(context) - 1;
            if (count > 0)
            {
                LiveBridges[context] = count;
            }
            else
            {
                LiveBridges.Remove(context);
            }
        }
    }

    /// <summary>
    /// Hands <paramref name="import"/> over, with semaphores to free after it. <paramref name="probe"/>, if any, already
    /// waits GPU-side for written ≥ <paramref name="last"/>, after the bridge's drain released from CUDA; the bridge
    /// passes one only when the presenter no longer owns the ring. Call on the presenter's thread (it reads the
    /// presenter's state). Makes no CUDA call.
    /// </summary>
    public static void Adopt(RingImport import, VulkanPresenter presenter, ulong last, CudaStream? probe, CudaExternalSemaphore[] retired, BridgeTestHooks? hooks = null)
    {
        var owned = ReferenceEquals(presenter.Ring, import.Ring);
        var entry = new Entry(import, presenter, last, probe, retired)
        {
            Hook = hooks?.BeforeReapStep,
            ReleaseHook = hooks?.BeforeReapRelease,
            Context = import.Written.Context.Handle,
            Generation = owned ? presenter.RingGeneration : -1,
            KnownReleased = owned ? presenter.LastReleasedWrite : 0,
            LetGo = !owned,

            // A probe comes from the bridge's drain once the presenter had let go: that drain has already released
            // from CUDA what needed it.
            ReleasedFromCuda = probe is not null,
        };
        lock (Gate)
        {
            Entries.Add(entry);
            Interlocked.Increment(ref _pending);
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "fmp-import-reaper" };
                _thread.Start();
            }

            Monitor.PulseAll(Gate);
        }
    }

    /// <summary>Waits (bounded) until every import handed over has been freed or dropped. False on timeout.</summary>
    public static bool WaitIdle(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Pending > 0)
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            Thread.Sleep(PollMs);
        }

        return true;
    }

    private static void Run()
    {
        while (true)
        {
            Entry[] snapshot;
            lock (Gate)
            {
                while (Entries.Count == 0)
                {
                    Monitor.Wait(Gate);
                }

                snapshot = [.. Entries];
            }

            foreach (var entry in snapshot)
            {
                if (Step(entry))
                {
                    lock (Gate)
                    {
                        Entries.Remove(entry);
                    }

                    Interlocked.Decrement(ref _pending);
                }
            }

            Thread.Sleep(PollMs);
        }
    }

    /// <summary>One look at an entry; true once it is done with (freed, or dropped).</summary>
    private static bool Step(Entry e)
    {
        var context = e.Import.Written.Context;
        try
        {
            e.Hook?.Invoke();
            if (e.Stream is null && !context.IsDisposed)
            {
                // Created now and idle (nothing is ever queued on it but a CUDA-side release once the presenter let
                // go), so that release needs no new CUDA object even if the context object is disposed by then.
                e.Stream = TryCreateStream(context);
            }

            if (!e.WorkDone && !e.LetGo)
            {
                if (Owned(e, out var released) && e.Import.Ring.TryReadWrittenValue(out var written))
                {
                    e.KnownReleased = Math.Max(e.KnownReleased, released);
                    if (written < e.Last)
                    {
                        return false; // still working (or never signalled): nothing queued, look again later
                    }

                    e.WorkDone = true; // every write handed out has finished
                }
                else
                {
                    e.LetGo = true; // the presenter replaced the ring or is disposing it
                }
            }

            if (!e.WorkDone)
            {
                if (e.Probe is { } probe)
                {
                    if (!probe.IsIdle)
                    {
                        return false;
                    }
                }
                else if (!e.ReleasedFromCuda)
                {
                    // Nothing will release these writes now. Released never exceeds the last write handed out on the
                    // ring; if a later bridge handed out more, the presenter or that bridge's own teardown released past
                    // ours, and signalling ours could be lower than what it reached.
                    if (e.Last > Math.Max(e.KnownReleased, FinalRelease(e)) && e.Import.HighestHandedOut.Value <= e.Last)
                    {
                        // Without a stream of its own (the context object was disposed before the reaper's first look)
                        // this throws ObjectDisposedException, and the entry is dropped (below).
                        var stream = e.Stream ??= CudaStream.Create(context);
                        e.ReleaseHook?.Invoke(e.Last);
                        e.Import.Released.Signal(stream, e.Last);
                    }

                    e.ReleasedFromCuda = true;
                }

                e.WorkDone = true; // what is queued can finish now; the free waits for it
            }

            return TryFree(e);
        }
        catch (CudaException ex) when (IsGone(ex.Result))
        {
            Interlocked.Increment(ref _dropped);
            return true;
        }
        catch (ObjectDisposedException) when (context.IsDisposed)
        {
            Interlocked.Increment(ref _dropped);
            return true;
        }
        catch (Exception ex) when (ex is CudaException or VulkanException or ObjectDisposedException or InvalidOperationException)
        {
            // A driver going away or a transient failure: look again later.
            return false;
        }
    }

    /// <summary>
    /// Frees the entry unless a bridge is live on its context (then false: held, look again later). Under the lock that
    /// bridges register with, so none can become live while the free runs.
    /// </summary>
    private static bool TryFree(Entry e)
    {
        lock (LiveGate)
        {
            if (LiveBridges.ContainsKey(e.Context))
            {
                return false;
            }

            e.Import.Dispose();
            e.Stream?.Dispose();
            e.Probe?.Dispose();
            foreach (var semaphore in e.Retired)
            {
                semaphore.Dispose();
            }

            return true;
        }
    }

    /// <summary>
    /// Whether the presenter still owns the entry's ring, with what it has released of it, read consistently: the
    /// generation must be the entry's before and after the read, so a release count of a ring that replaced it (which
    /// can only be written after the generation changed) is never mixed in.
    /// </summary>
    private static bool Owned(Entry e, out ulong released)
    {
        released = 0;
        var p = e.Presenter;
        if (p.RingGeneration != e.Generation || !ReferenceEquals(p.Ring, e.Import.Ring))
        {
            return false;
        }

        var value = p.LastReleasedWrite;
        if (p.RingGeneration != e.Generation || !ReferenceEquals(p.Ring, e.Import.Ring))
        {
            return false;
        }

        released = value;
        return true;
    }

    /// <summary>
    /// What a disposed presenter released of the entry's ring last (its count survives Dispose), read generation-checked;
    /// 0 when the presenter went on to another ring (whose count says nothing about this one).
    /// </summary>
    private static ulong FinalRelease(Entry e)
    {
        var p = e.Presenter;
        if (p.RingGeneration != e.Generation || p.Ring is not null)
        {
            return 0;
        }

        var value = p.LastReleasedWrite;
        return p.RingGeneration == e.Generation && p.Ring is null ? value : 0;
    }

    /// <summary>
    /// The context cannot be used any more: destroyed, deinitialised or invalid, or a sticky error (the driver documents
    /// that every later call then fails the same way and the process must restart to use CUDA again).
    /// </summary>
    private static bool IsGone(CudaResult result) => result is
        CudaResult.ErrorContextIsDestroyed or CudaResult.ErrorDeinitialized or CudaResult.ErrorInvalidContext or
        CudaResult.ErrorContained or CudaResult.ErrorIllegalAddress or CudaResult.ErrorLaunchTimeout or CudaResult.ErrorAssert or
        CudaResult.ErrorHardwareStackError or CudaResult.ErrorIllegalInstruction or CudaResult.ErrorMisalignedAddress or
        CudaResult.ErrorInvalidAddressSpace or CudaResult.ErrorInvalidPc or CudaResult.ErrorLaunchFailed;

    /// <summary>
    /// A stream on <paramref name="context"/>, or null if the context object was disposed meanwhile or the driver failed
    /// transiently (the next look tries again). Throws only when the context is gone.
    /// </summary>
    private static CudaStream? TryCreateStream(CudaContext context)
    {
        try
        {
            return CudaStream.Create(context);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (CudaException ex) when (!IsGone(ex.Result))
        {
            return null;
        }
    }

    private sealed class Entry(RingImport import, VulkanPresenter presenter, ulong last, CudaStream? probe, CudaExternalSemaphore[] retired)
    {
        public RingImport Import { get; } = import;

        public VulkanPresenter Presenter { get; } = presenter;

        public ulong Last { get; } = last;

        public CudaExternalSemaphore[] Retired { get; } = retired;

        /// <summary>A GPU-side wait for written ≥ <see cref="Last"/> that the bridge's own drain queued, or null.</summary>
        public CudaStream? Probe { get; } = probe;

        public Action? Hook { get; init; }

        public Action<ulong>? ReleaseHook { get; init; }

        /// <summary>The native handle of the import's CUDA context: no free while a bridge on it is live.</summary>
        public nint Context { get; init; }

        /// <summary>The presenter's ring generation when the import was handed over; -1 if it no longer owned the ring.</summary>
        public int Generation { get; init; }

        public ulong KnownReleased { get; set; }

        public bool LetGo { get; set; }

        /// <summary>The reaper's own stream, for a CUDA-side release only; idle otherwise.</summary>
        public CudaStream? Stream { get; set; }

        public bool ReleasedFromCuda { get; set; }

        /// <summary>The import's queued work is done, or can finish without anyone else (so a free will not wait for good).</summary>
        public bool WorkDone { get; set; }
    }
}
