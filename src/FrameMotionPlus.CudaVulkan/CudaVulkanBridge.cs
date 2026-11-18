using System.Diagnostics.CodeAnalysis;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vulkan;

namespace FrameMotionPlus.CudaVulkan;

public sealed record CudaVulkanBridgeOptions
{
    /// <summary>
    /// CUDA operations (kernel launches, copies, event records, semaphore waits and signals) a stream may have queued
    /// before the bridge's limit stops it: 960. Measured on this driver, a stream holds about 1,024 queued operations,
    /// and past that the call that enqueues the next one blocks the CPU until the GPU drains the queue (writes of
    /// wait + kernel + signal blocked at the 341st; of wait + two kernels + signal at the 259th). 960 leaves a margin.
    /// </summary>
    public const int StreamQueueOperations = 960;

    /// <summary>The default <see cref="OperationsPerWrite"/>: 16, more than any producer in this repository enqueues.</summary>
    public const int DefaultOperationsPerWrite = 16;

    /// <summary>
    /// The most <see cref="MaxLead"/> may be: 250, half the presenter's <see cref="RingProtocol.MaxWaitingReleases"/>.
    /// Each write handed to the presenter adds at most about one release that waits for the producer, so within this
    /// lead the presenter's waiting bound (and its write-lead bound, 500 × N) can never be reached and it never refuses
    /// a request from the bridge.
    /// </summary>
    public const int MaxLeadCeiling = RingProtocol.MaxWaitingReleases / 2;

    /// <summary>
    /// The output colour the bridge asks the presenter for when it configures it. The default (unspecified) lets the
    /// ring format decide: 8-bit is SDR, 10-bit asks for HDR10 where the target offers it.
    /// </summary>
    public ColorMetadata OutputColor { get; init; }

    /// <summary>Bound on each CPU wait in <see cref="CudaVulkanBridge.Configure"/> and Dispose (in-flight writes).</summary>
    public long DrainTimeoutNs { get; init; } = 2_000_000_000;

    /// <summary>
    /// The hard limit on writes handed out whose work the GPU has not finished (<see cref="IGpuBridge.MaxOutstanding"/>):
    /// 0 for 2 × N (one extra ring of slack over the design doc's "about N"), else 1 to <see cref="MaxLeadCeiling"/>, and
    /// at most <see cref="StreamQueueOperations"/> / <see cref="OperationsPerWrite"/> so that, within it, the producer's
    /// stream never fills, so enqueueing a write's work never blocks on a full stream (see the bridge's remarks, "Frees",
    /// for the one other way an enqueue can block on this driver).
    /// </summary>
    public int MaxLead { get; init; }

    /// <summary>
    /// The most CUDA operations the producer enqueues for one write, on whichever stream holds the write's work, its
    /// waits and its signal (0 for <see cref="DefaultOperationsPerWrite"/>). The "never blocks on a full stream" guarantee
    /// holds for producers that stay within it.
    /// </summary>
    public int OperationsPerWrite { get; init; }
}

/// <summary>
/// The Linux <see cref="IGpuBridge"/>: the presenter's <see cref="PresentationRing"/> (Vulkan buffers and two timeline
/// semaphores, exported as opaque FDs) imported into a CUDA context, so CUDA writes ring slots and Vulkan presents
/// them with GPU-side synchronisation only.
/// <para>
/// <b>Value protocol</b> (<see cref="RingProtocol"/>). Each acquire hands out the next write n of the ring, in slot
/// (n − 1) mod N. Numbering never restarts on a ring: a fresh ring starts at 1, and a ring imported again (by a new
/// bridge on the same presenter, or after a failed Configure) continues after the highest write it has seen.
/// <list type="bullet">
/// <item><see cref="BridgeSlot.ProducerWait"/> = <em>released</em> ≥ n − N as a CUDA timeline point (none while
/// n ≤ N), also the <see cref="GpuFrame.Ready"/> of <see cref="BridgeSlot.ProducerView"/>, a CUDA-domain frame on the
/// imported slot memory. A CUDA generator given that view as its output waits on it by itself.</item>
/// <item><see cref="BridgeSlot.ProducerSignal"/> = <em>written</em> := n (CUDA). The producer signals it once the
/// slot is written, in write order (see <see cref="IGpuBridge"/>, "Order").</item>
/// <item><see cref="BridgeSlot.ConsumerView"/>, <see cref="BridgeSlot.ConsumerWait"/> = <em>written</em> ≥ n and
/// <see cref="BridgeSlot.ConsumerSignal"/> = <em>released</em> := n in the Vulkan domain: what
/// <see cref="VulkanPresenter.TryPresent"/> takes (<see cref="CreatePresentRequest"/>). The presenter signals the
/// release itself: for a shown write once it has its own copy; a refused write stays presentable (retained) until a
/// newer write is handed to the presenter or a release covers it (<see cref="VulkanPresenter.Release"/>).</item>
/// </list>
/// </para>
/// <para>
/// <b>Back-pressure.</b> <see cref="Outstanding"/> is the writes handed out whose work the GPU has not finished:
/// handed out minus the ring's <em>written</em> value. It is not the presenter's released value, which only says
/// releases were submitted (a refused present still submits one). <see cref="TryAcquireSlot"/> refuses, and
/// <see cref="AcquireSlot"/> throws, when the next write would make it exceed <see cref="MaxOutstanding"/>. A slow
/// producer, a stalled GPU and a stalled presenter (whose missing releases stop the producer's writes) all stop
/// <em>written</em>, so all of them are held back at the limit instead of building a backlog; when the stall ends the
/// queued work runs, <em>written</em> catches up and acquiring resumes with no intervention. The limit keeps two
/// promises: within it the producer's stream never fills (<see cref="CudaVulkanBridgeOptions.OperationsPerWrite"/>),
/// so enqueueing a write's work never blocks on a full stream; and the presenter's own bounds are never reached, so it
/// never refuses a request from the bridge (<see cref="CudaVulkanBridgeOptions.MaxLeadCeiling"/>). An enqueue can still
/// block while a context-synchronising call waits on another thread ("Frees", below). Reading <em>written</em> is a host query of the
/// ring's semaphore (allocation-free, any thread); the bridge skips it while a value it has already seen proves there
/// is room.
/// </para>
/// <para>
/// <b>Ring replacement.</b> <see cref="Configure"/> owns the presenter's ring shape. It validates the new shape before
/// touching anything; on a change it releases every outstanding write through the presenter, waits (bounded) until all
/// of them have finished, frees the old memory imports, has the presenter replace its ring and imports the new one;
/// the generation grows. If the drain does not finish in time it throws <see cref="TimeoutException"/> and leaves the
/// current ring imported and usable. The old ring's semaphores stay alive until the bridge is disposed, so the driver
/// cannot hand their handles to the new ring while a stale slot may still name them. That is the bound: two CUDA
/// semaphore imports per ring replacement, kept until the bridge is disposed (300 replacements on one bridge showed a
/// flat Configure cost and unchanged GPU memory); a bridge reconfigured without end should be replaced now and then.
/// Slots carry their generation;
/// <see cref="CreatePresentRequest"/> refuses a stale one and <see cref="IsCurrent"/> tells a producer.
/// </para>
/// <para>
/// <b>Threads and lifetime.</b> <see cref="TryAcquireSlot"/>/<see cref="AcquireSlot"/> and <see cref="Outstanding"/>
/// belong to the producer thread and touch no Vulkan object but the ring's counter. <see cref="Configure"/> and Dispose
/// call into the presenter (which is single-threaded), so call them on its thread while nothing is presenting or
/// producing; they block, so keep them off the pacing thread during playback. Dispose the bridge before the presenter.
/// Nothing here ever signals a timeline to a lower value: the bridge releases from the CUDA side only once the presenter
/// no longer owns the ring, and never forces <em>written</em>. While the presenter owns the ring a drain polls
/// <em>written</em> from the host, so a drain that times out leaves no GPU-side wait behind; once the presenter has let
/// go, a drain releases from CUDA and waits for nothing. Dispose never throws or hangs: an import whose work has not
/// finished in time (or whose ring the presenter let go of) is handed, with the retired semaphores, to a background
/// reaper (<see cref="PendingReaps"/>), which frees it once its work is done, no other abandoned import on the context
/// is still waiting, and no bridge is live on its CUDA context, so a new bridge on the same ring holds it until that
/// bridge is disposed too (and the last bridge's Dispose frees what is done at once). Neither the reaper nor a drain
/// ever queues a GPU-side wait in the CUDA context (one that cannot complete would make every CUDA call that
/// synchronises the context block), so after an abandoning Dispose any teardown order (bridge, CUDA buffers,
/// presenter, or the presenter first) still completes. Dispose every bridge: one that never is keeps the reaper from
/// freeing anything on its context. Dispose the presenter before the process ends, too: if one of its releases waits
/// for a write that is never signalled, returning from <c>Main</c> without disposing it (which forces its own waits)
/// hangs in the driver's exit path.
/// </para>
/// <para>
/// <b>Frees.</b> On this driver a call that synchronises the context (<c>cuMemFree</c>, <c>cuMemFreeHost</c>, destroying
/// an external semaphore) waits for all queued work in it, and while it waits, enqueues on every other thread (kernel
/// launches, event records, semaphore waits and signals) block as well; a producer whose queued writes wait for a
/// release that only its own next present gives would deadlock behind it. The bridge makes such calls only inside its
/// own <see cref="Configure"/> (replacing the ring) and Dispose, on the caller's thread after its own writes have
/// drained, and through the reaper only while no bridge is live on the context. So within the lead limit the producer's
/// enqueues never block, provided nothing else frees in that context during playback: not the application (keep its
/// frees off the playback period, as off the present and pacing threads), and not a second pipeline on the same
/// context, whose bridge's Configure or Dispose would free while this one runs.
/// </para>
/// <para>
/// <b>Caller errors that remain.</b> A slot whose <see cref="BridgeSlot.ProducerSignal"/> is never enqueued: nothing
/// can tell it from a slow producer, so drains time out and the import goes to the reaper, which frees it once
/// <em>written</em> has passed that write (a later bridge's first write does) or the presenter, disposing, has forced
/// its own waits, and no bridge is live. Until then the import's memory stays allocated and the presenter's release of
/// that write waits GPU-side, on its Vulkan queue, for <em>written</em> to reach it; nothing waits for it in the CUDA
/// context.
/// N or more consecutive writes on one ring that are never signalled wedge any later bridge on that ring until the
/// presenter is disposed (its first writes wait for releases of those). A <see cref="BridgeSlot.ProducerView"/> kept
/// and written after a Configure: the old memory import is freed,
/// so the pointer is dangling (check <see cref="IsCurrent"/> before enqueueing). Writes signalled out of order: noticed
/// on a best-effort basis only (<see cref="OutOfOrderSignals"/>), never prevented. The bridge owns neither the
/// presenter nor the CUDA context.
/// </para>
/// </summary>
public sealed class CudaVulkanBridge : IGpuBridge
{
    private readonly CudaContext _context;
    private readonly VulkanPresenter _presenter;
    private readonly CudaVulkanBridgeOptions _options;
    private readonly BridgeTestHooks? _hooks;
    private readonly List<CudaExternalSemaphore> _retired = [];
    private readonly nint _contextHandle; // the native context this bridge is live on (the reaper frees nothing there meanwhile)
    private RingImport? _import;
    private ulong _next = RingProtocol.FirstWrite;
    private ulong _firstWrite = RingProtocol.FirstWrite;
    private ulong _knownWritten;
    private ulong _maxLead;
    private int _operationsPerWrite;
    private int _ringGeneration = -1;
    private int _configuration;
    private long _outOfOrder;
    private bool _disposed;

    public CudaVulkanBridge(CudaContext context, VulkanPresenter presenter, CudaVulkanBridgeOptions? options = null)
        : this(context, presenter, options, hooks: null)
    {
    }

    internal CudaVulkanBridge(CudaContext context, VulkanPresenter presenter, CudaVulkanBridgeOptions? options, BridgeTestHooks? hooks)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(presenter);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The CUDA/Vulkan bridge imports opaque POSIX file descriptors and needs Linux (Windows NT-handle interop is Phase 10).");
        }

        _options = options ?? new CudaVulkanBridgeOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.DrainTimeoutNs, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxLead, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(_options.OperationsPerWrite, nameof(options));
        _context = context;
        _presenter = presenter;
        _hooks = hooks;
        DeviceUuid = hooks?.CudaUuid ?? GpuIdentity.UuidOf(context.Device);
        if (presenter.Ring is { } existing)
        {
            GpuIdentity.EnsureSameDevice(DeviceUuid, existing.DeviceUuid, "The presenter");
        }

        // Last, so a constructor that throws leaves nothing registered. Waits if the reaper is freeing an import on this
        // context right now (it only does that once the import's work is done and no bridge is live).
        _contextHandle = context.Handle;
        ImportReaper.BridgeCreated(_contextHandle);
    }

    /// <summary>Imports that disposed bridges handed to the reaper and that are not freed yet (process-wide).</summary>
    public static int PendingReaps => ImportReaper.Pending;

    /// <summary>
    /// Imports the reaper dropped because their CUDA context was gone (process-wide; nothing could be freed): destroyed,
    /// deinitialised or in a sticky error state, or the <see cref="CudaContext"/> object disposed before the reaper's first
    /// look at an import it then had to release from CUDA (disposing the context object after <see cref="WaitForReaper"/>
    /// avoids that one).
    /// </summary>
    public static int DroppedReaps => ImportReaper.Dropped;

    public MemoryDomain ProducerDomain => MemoryDomain.CudaDevice;

    public MemoryDomain ConsumerDomain => MemoryDomain.Vulkan;

    /// <summary>Slots in the current ring; 0 before <see cref="Configure"/>.</summary>
    public int SlotCount => _import?.SlotCount ?? 0;

    public bool IsConfigured => _import is not null;

    /// <summary>The CUDA device's UUID, which the Vulkan ring must share.</summary>
    public DeviceUuid DeviceUuid { get; }

    /// <summary>The ring currently imported (owned by the presenter), or null.</summary>
    public PresentationRing? Ring => _import?.Ring;

    /// <summary>The presenter's <see cref="VulkanPresenter.RingGeneration"/> of the imported ring; -1 before Configure.</summary>
    public int RingGeneration => _ringGeneration;

    /// <summary>The current configuration's number, stamped on every slot (<see cref="BridgeSlot.Generation"/>); 0 before Configure.</summary>
    public int Generation => _configuration;

    /// <summary>The number of the last write handed out (on this ring, by any bridge; 0 = none).</summary>
    public ulong LastAcquiredWrite => _next - 1;

    /// <summary>The first write this configuration handed out (or will): 1 on a fresh ring, more on a ring with history.</summary>
    public ulong FirstWrite => _firstWrite;

    /// <summary>The effective <see cref="CudaVulkanBridgeOptions.OperationsPerWrite"/>; 0 before Configure.</summary>
    public int OperationsPerWrite => _import is null ? 0 : _operationsPerWrite;

    /// <summary>
    /// Writes handed out whose work the GPU has not finished (handed out minus the ring's <em>written</em> value). Reads
    /// the ring's counter; allocation-free; producer thread.
    /// </summary>
    public long Outstanding
    {
        get
        {
            var import = _import;
            if (import is null)
            {
                return 0;
            }

            RefreshWritten(import);
            var last = _next - 1;
            return last <= _knownWritten ? 0 : (long)(last - _knownWritten);
        }
    }

    /// <summary>The hard limit on <see cref="Outstanding"/> (<see cref="CudaVulkanBridgeOptions.MaxLead"/>); 0 before Configure.</summary>
    public long MaxOutstanding => _import is null ? 0 : (long)_maxLead;

    /// <summary>
    /// Times the bridge saw <em>written</em> lower than it had seen before: a producer signalling writes out of order
    /// (see <see cref="IGpuBridge"/>, "Order"). Best effort: it is only checked when the bridge reads the counter, so a
    /// regression that is undone before the next read (n, then n − 2, then n again in between two reads) goes unseen.
    /// 0 does not prove the order was kept; non-zero proves it was not.
    /// </summary>
    public long OutOfOrderSignals => _outOfOrder;

    /// <summary>CUDA objects the bridge holds for the current ring (2 per slot plus 2 semaphores), for leak checks.</summary>
    public int LiveImportObjects => _import?.LiveObjects ?? 0;

    /// <summary>
    /// Imports this bridge handed to the reaper: at Dispose, when their work had not finished in time; and at Dispose or
    /// a <see cref="Configure"/> that found the presenter no longer owning the ring (the bridge then releases every write
    /// from CUDA but cannot tell when the work has finished without a GPU-side wait, so the reaper frees the import once
    /// no bridge is live).
    /// </summary>
    public int AbandonedImports { get; private set; }

    /// <summary>Waits (bounded) until the reaper has freed every import handed to it. False on timeout.</summary>
    public static bool WaitForReaper(TimeSpan timeout) => ImportReaper.WaitIdle(timeout);

    /// <summary>
    /// Sets the ring shape. Validates it first; then a first call, or a change of size or format, drains and frees the
    /// current imports, lets the presenter create the ring (<see cref="VulkanPresenter.Configure"/>) and imports it; the
    /// same shape again does nothing. <see cref="BridgeConfig.SlotCount"/> is 0 (the presenter's) or must equal the
    /// presenter's. Throws <see cref="TimeoutException"/> (leaving the current ring imported and usable) if the
    /// outstanding writes do not finish within the drain timeout. If the presenter replaced its ring behind the bridge,
    /// the old ring's writes are released from CUDA and its import goes to the reaper (see <see cref="AbandonedImports"/>).
    /// Not for the hot path.
    /// </summary>
    public void Configure(in BridgeConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObjectDisposedException.ThrowIf(_context.IsDisposed, _context);
        ArgumentOutOfRangeException.ThrowIfNegative(config.SlotCount, nameof(config));
        if (config.Format is not (PixelFormat.Rgba8 or PixelFormat.Rgb10A2))
        {
            throw new ArgumentException($"Ring format {config.Format} is not supported (Rgba8 and Rgb10A2 are).", nameof(config));
        }

        // Everything the presenter would refuse is refused here, before anything is torn down.
        _ = PresentationRing.Normalize(new PresentationRingConfig { Width = config.Width, Height = config.Height, Format = config.Format });

        var current = _import;
        if (current is not null)
        {
            CheckSlotCount(config, current.SlotCount);
            if (_presenter.RingGeneration == _ringGeneration && current.Ring.Width == config.Width
                && current.Ring.Height == config.Height && current.Ring.Format == config.Format)
            {
                return;
            }

            var drained = Drain(current);
            if (drained == DrainResult.NotFinished)
            {
                throw new TimeoutException(
                    $"Writes up to {_next - 1} were handed out but not all of them finished within {_options.DrainTimeoutNs / 1_000_000} ms " +
                    "(every acquired slot's ProducerSignal must be enqueued, and a slow producer must catch up). The current ring stays imported.");
            }

            if (drained == DrainResult.Finished)
            {
                var (written, released) = current.Retire();
                Retain(written);
                Retain(released);
            }
            else
            {
                // The presenter replaced its ring behind the bridge, and the drain released every write from CUDA: their
                // work can finish now, but only a GPU-side wait could tell when, and one for a write that is never
                // signalled would wedge the context. The reaper frees the import once no bridge is live (this one
                // included), so its semaphores also stay alive while a stale slot may name them.
                ImportReaper.Adopt(current, _presenter, _next - 1, releasedFromCuda: true, [], _hooks);
                AbandonedImports++;
            }

            _import = null;
            _ringGeneration = -1;
        }

        _presenter.Configure(new PresenterConfig
        {
            ClockMode = ClockMode.FixedRefresh,
            Width = config.Width,
            Height = config.Height,
            Format = config.Format,
            OutputColor = _options.OutputColor,
        });
        var ring = _presenter.Ring ?? throw new InvalidOperationException("The presenter created no ring.");
        CheckSlotCount(config, ring.SlotCount);
        var (maxLead, operations) = Limits(_options, ring.SlotCount);
        var import = RingImport.Import(_context, ring, DeviceUuid, _hooks);

        // Continue after everything this ring has seen: a fresh ring starts at 1; one imported again (a new bridge on
        // the same presenter, a Configure that failed after the old import was freed) never reuses a write number.
        var writtenNow = ring.ReadWrittenValue();
        var history = Math.Max(
            Math.Max(writtenNow, ring.ReadReleasedValue()),
            Math.Max(Math.Max(_presenter.LastReleasedWrite, _presenter.LastPresentedWrite), Math.Max(_presenter.RetainedWrite, import.HighestHandedOut.Value)));
        _import = import;
        _maxLead = (ulong)maxLead;
        _operationsPerWrite = operations;
        _ringGeneration = _presenter.RingGeneration;
        _configuration++;
        _next = history + 1;
        _firstWrite = _next;
        _knownWritten = writtenNow;
    }

    /// <summary>
    /// The next write in ring order and its slot (see the class remarks), unless it would put more than
    /// <see cref="MaxOutstanding"/> writes the GPU has not finished in flight: then false, nothing is handed out, and the
    /// producer skips this frame. Never blocks, allocation-free. Throws when not configured, or when the presenter's
    /// ring was replaced (or the presenter disposed) behind the bridge.
    /// </summary>
    public bool TryAcquireSlot(out BridgeSlot slot)
    {
        var import = _import;
        if (import is null || _disposed)
        {
            ThrowNotConfigured();
        }

        if (_presenter.RingGeneration != _ringGeneration || !ReferenceEquals(_presenter.Ring, import.Ring))
        {
            ThrowRingReplaced();
        }

        var next = _next;
        if (next - _knownWritten > _maxLead)
        {
            RefreshWritten(import);
            if (next - _knownWritten > _maxLead)
            {
                slot = default;
                return false;
            }
        }

        _next = next + 1;
        import.HighestHandedOut.Value = next;
        slot = import.Slot(next, _configuration);
        return true;
    }

    /// <summary>
    /// As <see cref="TryAcquireSlot"/>, but at the limit it throws <see cref="InvalidOperationException"/> (use
    /// <see cref="TryAcquireSlot"/> and skip the frame instead).
    /// </summary>
    public BridgeSlot AcquireSlot()
    {
        if (!TryAcquireSlot(out var slot))
        {
            ThrowTooFarAhead();
        }

        return slot;
    }

    /// <summary>
    /// A present request for <paramref name="slot"/>'s write (its consumer view and <em>written</em> point), for
    /// <see cref="VulkanPresenter.TryPresent"/> or <see cref="VulkanPresenter.Release"/>. Allocation-free. Throws for a
    /// slot not handed out by the current configuration: from an earlier <see cref="Generation"/> (its ring is gone; with
    /// coinciding handles the presenter could mistake it for the new ring's write of that number) or never handed out.
    /// </summary>
    public PresentRequest CreatePresentRequest(in BridgeSlot slot, long slotIndex, long targetTimeNs)
    {
        if (!IsCurrent(slot))
        {
            ThrowStaleSlot(slot);
        }

        return new PresentRequest
        {
            SlotIndex = slotIndex,
            Frame = slot.ConsumerView, // its Ready is ConsumerWait, the written point the present pass waits on
            TargetTimeNs = targetTimeNs,
        };
    }

    /// <summary>True when <paramref name="slot"/> was handed out by the current configuration. Cheap; producer thread.</summary>
    public bool IsCurrent(in BridgeSlot slot) =>
        _import is not null && slot.Generation == _configuration && slot.Write >= _firstWrite && slot.Write < _next;

    /// <summary>
    /// Releases every outstanding write and waits (bounded) until all of them have finished, then frees the CUDA
    /// imports; if they have not finished in time, hands them to the reaper instead (see the class remarks). A bridge
    /// disposed after its presenter (the wrong order) releases every write from CUDA and hands the import to the reaper
    /// without waiting for anything GPU-side. The presenter and the ring are left alone. Dispose the bridge before the
    /// presenter. Never throws or hangs.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_import is { } import)
            {
                _import = null;
                var drained = Drain(import);
                if (drained == DrainResult.Finished)
                {
                    import.Dispose();
                }
                else
                {
                    // Freeing now would block inside CUDA (cuMemFree waits for the work), possibly for good. The retired
                    // semaphores go too: destroying one synchronises the context, which waits for that same work.
                    ImportReaper.Adopt(import, _presenter, _next - 1, releasedFromCuda: drained == DrainResult.ReleasedFromCuda, [.. _retired], _hooks);
                    _retired.Clear();
                    AbandonedImports++;
                }
            }
        }
        finally
        {
            foreach (var semaphore in _retired)
            {
                semaphore.Dispose();
            }

            _retired.Clear();

            // From here the reaper may free on this context again (once no other bridge is live on it).
            ImportReaper.BridgeDisposed(_contextHandle);
        }
    }

    private static void CheckSlotCount(in BridgeConfig config, int actual)
    {
        if (config.SlotCount != 0 && config.SlotCount != actual)
        {
            throw new ArgumentException(
                $"The presenter's ring has {actual} slots and the bridge cannot change that (VulkanPresenterOptions.SlotCount); " +
                $"pass SlotCount 0 or {actual}, not {config.SlotCount}.",
                nameof(config));
        }
    }

    private static (int MaxLead, int Operations) Limits(CudaVulkanBridgeOptions options, int slots)
    {
        var maxLead = options.MaxLead == 0 ? 2 * slots : options.MaxLead;
        var operations = options.OperationsPerWrite == 0 ? CudaVulkanBridgeOptions.DefaultOperationsPerWrite : options.OperationsPerWrite;
        if (maxLead > CudaVulkanBridgeOptions.MaxLeadCeiling || (long)maxLead * operations > CudaVulkanBridgeOptions.StreamQueueOperations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                maxLead,
                $"MaxLead {maxLead} with {operations} CUDA operations per write could fill the producer's stream ({maxLead * operations} of the " +
                $"{CudaVulkanBridgeOptions.StreamQueueOperations} operations it may queue) or reach the presenter's bounds (at most " +
                $"{CudaVulkanBridgeOptions.MaxLeadCeiling}); lower it or declare fewer operations per write.");
        }

        return (maxLead, operations);
    }

    /// <summary>Reads the ring's <em>written</em> value; notes a value lower than one already seen (out-of-order signals).</summary>
    private void RefreshWritten(RingImport import)
    {
        var written = import.Ring.ReadWrittenValue();
        if (written < _knownWritten)
        {
            _outOfOrder++;
            return;
        }

        _knownWritten = written;
    }

    private void Retain(CudaExternalSemaphore? semaphore)
    {
        if (semaphore is not null)
        {
            _retired.Add(semaphore);
        }
    }

    private enum DrainResult
    {
        /// <summary>Every write handed out has finished: the import can be freed now.</summary>
        Finished,

        /// <summary>Not finished in time (or the release through the presenter failed); nothing was queued GPU-side.</summary>
        NotFinished,

        /// <summary>
        /// The presenter no longer owns the ring; every write was released from CUDA, so the work can finish by itself,
        /// but nothing tells when without a GPU-side wait: the import goes to the reaper.
        /// </summary>
        ReleasedFromCuda,
    }

    /// <summary>
    /// Has every write handed out finish (bounded by <see cref="CudaVulkanBridgeOptions.DrainTimeoutNs"/>). While the
    /// presenter owns the ring (and so its Vulkan objects are alive on this thread) it releases through the presenter and
    /// polls <em>written</em> from the host: no GPU-side wait is queued, so a write that never finishes leaves nothing
    /// behind. Once the presenter has let go (replaced the ring outside the bridge, or been disposed) nothing else will
    /// release, and <em>written</em> can no longer be read from the host, so it releases from the CUDA side and queues no
    /// wait at all (one for a write whose signal is never enqueued would never complete, and every later free in the
    /// context would block behind it).
    /// </summary>
    private DrainResult Drain(RingImport import)
    {
        var last = _next - 1;
        if (_next == _firstWrite)
        {
            return DrainResult.Finished;
        }

        var deadline = Environment.TickCount64 + Math.Max(1, _options.DrainTimeoutNs / 1_000_000);
        try
        {
            if (ReferenceEquals(_presenter.Ring, import.Ring))
            {
                if (!ReleaseThroughPresenter(import, last, deadline))
                {
                    return DrainResult.NotFinished;
                }

                while (import.Ring.ReadWrittenValue() < last)
                {
                    if (Environment.TickCount64 > deadline)
                    {
                        return DrainResult.NotFinished;
                    }

                    Thread.Sleep(1);
                }

                return DrainResult.Finished;
            }

            // The presenter let go of the ring: nothing else will release these writes, nor signal 'released' again
            // (released never exceeds the last write handed out, so 'last' is never lower than it).
            var known = _presenter.Ring is null ? _presenter.LastReleasedWrite : 0;
            if (last > known)
            {
                using var stream = CudaStream.Create(_context);
                import.Released.Signal(stream, last);
                stream.Synchronize();
            }

            return DrainResult.ReleasedFromCuda;
        }
        catch (Exception e) when (e is CudaException or VulkanException or ObjectDisposedException)
        {
            return DrainResult.NotFinished;
        }
    }

    /// <summary>
    /// Releases through <paramref name="last"/> via the presenter, in calls it accepts: at most
    /// <see cref="RingProtocol.MaxWriteLead"/> past its last release, and retried while its waiting bound refuses (the
    /// releases already queued wait for writes the producer has not finished; they will). False if it failed otherwise
    /// or the deadline passed.
    /// </summary>
    private bool ReleaseThroughPresenter(RingImport import, ulong last, long deadline)
    {
        try
        {
            _hooks?.BeforeRelease?.Invoke(last);
            var step = (ulong)RingProtocol.MaxWriteLead(import.SlotCount);
            while (true)
            {
                var released = _presenter.LastReleasedWrite;
                if (released >= last)
                {
                    return true;
                }

                try
                {
                    _presenter.ReleaseThrough(Math.Min(last, released + step));
                }
                catch (ArgumentOutOfRangeException)
                {
                    if (Environment.TickCount64 > deadline)
                    {
                        return false;
                    }

                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    [DoesNotReturn]
    private void ThrowNotConfigured()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new InvalidOperationException("Configure the bridge before acquiring slots.");
    }

    [DoesNotReturn]
    private static void ThrowRingReplaced() =>
        throw new InvalidOperationException(
            "The presenter's ring was replaced (or the presenter disposed) outside the bridge; reconfigure through CudaVulkanBridge.Configure, " +
            "which drains and re-imports the ring.");

    [DoesNotReturn]
    private void ThrowTooFarAhead() =>
        throw new InvalidOperationException(
            $"{_next - 1 - _knownWritten} writes handed out have not finished on the GPU (limit {_maxLead}, CudaVulkanBridgeOptions.MaxLead): " +
            "the next one could block the producer or be refused. Use TryAcquireSlot and skip producing the frame.");

    [DoesNotReturn]
    private void ThrowStaleSlot(in BridgeSlot slot) =>
        throw new InvalidOperationException(
            $"Slot of write {slot.Write} from bridge generation {slot.Generation} is not a slot of the current ring (generation {_configuration}, " +
            $"writes {_firstWrite} to {_next - 1} handed out): its ring was replaced or it was never handed out by this bridge.");
}
