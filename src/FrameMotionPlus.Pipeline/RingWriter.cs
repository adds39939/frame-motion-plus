using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Collections;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>Reads the GPU time of a generator's calls, oldest first, without blocking.</summary>
public delegate bool GpuTimeReader(out long nanoseconds);

/// <summary>A generator and how to read the GPU time of each of its <see cref="IFrameGenerator.Enqueue"/> calls.</summary>
public sealed class GeneratorHandle
{
    public GeneratorHandle(IFrameGenerator generator, GpuTimeReader? readGpuTime, string name)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrEmpty(name);
        Generator = generator;
        ReadGpuTime = readGpuTime;
        Name = name;
    }

    public IFrameGenerator Generator { get; }

    /// <summary>The generator's own GPU span of each call that did not throw (its d), oldest first; null if it has none.</summary>
    public GpuTimeReader? ReadGpuTime { get; }

    public string Name { get; }
}

/// <summary>
/// The generator thread: every write into the presentation ring, real frames and generated frames alike, in the order the
/// pacing thread handed the slots out, on one CUDA stream (the generator's, high priority). So <em>written</em> is
/// signalled in write order by construction (Core's ordering rule). A real frame is copied in behind the slot's release
/// and the frame's conversion; a generated one is the generator's <c>Enqueue</c> straight into the slot (VFG blocks this
/// thread for the generation: that is why it is a thread of its own, never the pacing or present thread, and nothing it
/// does releases a slot or writes a source frame). When the generator throws, or reports itself faulted, the slot gets
/// the nearest real frame on the same stream behind whatever the failed call enqueued, and is signalled in order
/// (Core's failure contract), and generation stays off for the rest of the session: playback carries on with real frames.
/// Allocation-free per write (an exception on the fault path aside).
/// </summary>
internal sealed class RingWriter : IDisposable
{
    private const int PendingCapacity = 128;

    private readonly CudaContext _context;
    private readonly CudaStream _stream;
    private readonly GeneratorHandle? _generator;
    private readonly CudaSpanTimerRing _copySpans;
    private readonly GpuClock _clock;
    private readonly SpscRing<WriteJob> _jobs;
    private readonly SpscRing<long> _generationTimes;
    private readonly SpscRing<WriteRecord> _records;
    // An AutoResetEvent, not a ManualResetEventSlim: under NativeAOT the slim event's Monitor-based slow path allocates
    // lazily created state the first time a thread contends for it, which can be at any point of a long run (seen in the
    // 10-minute gate: 120 B, once, on this thread). The wait subsystem's event allocates nothing after a thread's first
    // wait. A Set while nobody waits leaves it signalled, so a wake-up between the check and the wait is never lost.
    private readonly AutoResetEvent _wake = new(false);
    private readonly Pending[] _pending = new Pending[PendingCapacity];
    private readonly uint _fillPixel;
    private int _pendingHead;
    private int _pendingCount;
    private volatile bool _stop;
    private bool _faulted;

    // Counters: written by this thread, read by others after it is joined (or as a snapshot).
    private long _enqueuedWrite;
    private long _copies;
    private long _generated;
    private long _fills;
    private long _fallbacks;
    private long _faults;
    private long _recordsDropped;
    private long _samplesDropped;
    private Exception? _firstFault;

    public RingWriter(
        CudaContext context,
        CudaStream stream,
        GeneratorHandle? generator,
        PixelFormat format,
        int queueCapacity,
        int recordCapacity)
    {
        _context = context;
        _stream = stream;
        _generator = generator;
        _fillPixel = format == PixelFormat.Rgb10A2 ? 0xC0000000u : 0xFF000000u;
        _jobs = new SpscRing<WriteJob>(queueCapacity);
        _generationTimes = new SpscRing<long>(1024);
        _records = new SpscRing<WriteRecord>(recordCapacity);
        _copySpans = CudaSpanTimerRing.Create(context, 64);
        try
        {
            _clock = new GpuClock(context);
        }
        catch
        {
            _copySpans.Dispose();
            throw;
        }
    }

    /// <summary>Jobs, pacing thread → this thread (single producer at a time: the pre-roll, then the pacing thread).</summary>
    public SpscRing<WriteJob> Jobs => _jobs;

    /// <summary>Generation GPU times (d), this thread → pacing thread.</summary>
    public SpscRing<long> GenerationTimes => _generationTimes;

    /// <summary>Write records, this thread → collector.</summary>
    public SpscRing<WriteRecord> Records => _records;

    public CudaStream Stream => _stream;

    public GpuClock Clock => _clock;

    /// <summary>Set by the session at the start of the measured window; this thread snapshots its own counters then.</summary>
    public ThreadMeter Meter { get; } = new();

    /// <summary>
    /// The newest write whose GPU work (its waits on the frames' <c>Ready</c> included) has been enqueued. A source frame
    /// may only be released once the last write that reads it is enqueued: a frame's Ready event is re-recorded when its
    /// buffer is reused, and a wait enqueued after that would wait for the new frame (Core's Release contract).
    /// </summary>
    public ulong EnqueuedWrite => (ulong)Volatile.Read(ref _enqueuedWrite);

    public long Copies => Volatile.Read(ref _copies);

    public long Generated => Volatile.Read(ref _generated);

    public long Fills => Volatile.Read(ref _fills);

    public long Fallbacks => Volatile.Read(ref _fallbacks);

    public long Faults => Volatile.Read(ref _faults);

    public bool GeneratorFaulted => Volatile.Read(ref _faulted);

    public Exception? FirstFault => Volatile.Read(ref _firstFault);

    public long RecordsDropped => Volatile.Read(ref _recordsDropped);

    public long SamplesDropped => Volatile.Read(ref _samplesDropped);

    /// <summary>Test hook: runs before each job (stalls, allocation injection). Generator thread.</summary>
    public Action<WriteJob>? BeforeJob { get; set; }

    /// <summary>Wakes the thread for a job just queued. Any thread; allocation-free.</summary>
    public void Wake() => _wake.Set();

    /// <summary>Finish every queued job, then return. Any thread.</summary>
    public void RequestStop()
    {
        _stop = true;
        _wake.Set();
    }

    /// <summary>The thread body.</summary>
    public void Run()
    {
        _context.MakeCurrent();
        PreRoll();
        while (true)
        {
            Meter.Tick();
            if (_jobs.TryDequeue(out var job))
            {
                Execute(job);
                Collect();
                continue;
            }

            Collect();
            if (_stop)
            {
                break;
            }

            if (_jobs.IsEmpty && !_stop)
            {
                // Pending GPU spans are collected at the next wake-up at the latest (2 ms).
                _ = _wake.WaitOne(_pendingCount > 0 ? 1 : 2);
            }
        }

        // Every job is enqueued; collect what remains of its timings (bounded).
        var giveUp = Monotonic.NowNs + (2 * Nanoseconds.PerSecond);
        while (_pendingCount > 0 && Monotonic.NowNs < giveUp)
        {
            Collect();
            if (_pendingCount > 0)
            {
                Thread.Sleep(1);
            }
        }

        Meter.Stop();
    }

    /// <summary>First uses on this thread of everything the loop calls (no first-use allocation can land in a measured window).</summary>
    private void PreRoll()
    {
        // The GPU clock's anchor, at a drained point (the stream is idle before the first write).
        _ = _clock.Start(_stream);
        _ = _wake.WaitOne(1); // the thread's first wait sets up its wait state (allocates once)
        _ = Monotonic.NowNs;
        _ = ThreadCpuClock.CurrentThreadNs;
        _ = _copySpans.TryRead(out _);
        _ = _stream.IsIdle;
        if (_generator?.ReadGpuTime is { } read)
        {
            _ = read(out _);
        }
    }

    private void Execute(in WriteJob job)
    {
        BeforeJob?.Invoke(job);
        var start = Monotonic.NowNs;
        var stream = _stream;
        var slot = job.Slot;
        WriteOutcome outcome;
        var source = SpanSource.Copy;
        switch (job.Kind)
        {
            case WriteKind.Fill:
                stream.Wait(slot.ProducerWait);
                stream.FillFrameAsync(slot.ProducerView, _fillPixel);
                stream.Signal(slot.ProducerSignal);
                outcome = WriteOutcome.Fill;
                source = SpanSource.None;
                _fills++;
                break;
            case WriteKind.Real:
                CopyReal(job.Prev, slot);
                outcome = WriteOutcome.Copied;
                Volatile.Write(ref _copies, _copies + 1);
                break;
            default:
                if (!_faulted && _generator is { } generator && !IsFaulted(generator.Generator))
                {
                    try
                    {
                        generator.Generator.Enqueue(job.Prev, job.Curr, job.Timestep, slot.ProducerView, slot.ProducerSignal);
                        outcome = WriteOutcome.Generated;
                        source = generator.ReadGpuTime is null ? SpanSource.None : SpanSource.Generator;
                        Volatile.Write(ref _generated, _generated + 1);
                        break;
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        // Core's failure contract: nothing signalled, GPU-side waits possibly queued and the output partly
                        // written. The slot gets the real frame it would otherwise show, behind all of that, on this stream.
                        Volatile.Write(ref _faults, _faults + 1);
                        _firstFault ??= e;
                        Volatile.Write(ref _faulted, true);
                    }
                }

                CopyReal(job.Timestep >= 0.5f ? job.Curr : job.Prev, slot);
                outcome = WriteOutcome.Fallback;
                Volatile.Write(ref _fallbacks, _fallbacks + 1);
                break;
        }

        var end = Monotonic.NowNs;
        Volatile.Write(ref _enqueuedWrite, (long)slot.Write);
        _clock.Mark(stream, (long)slot.Write, end);
        if (_pendingCount == PendingCapacity)
        {
            // The GPU is a whole queue of records behind (cannot happen within the bridge's lead): give up the oldest.
            _pendingHead = (_pendingHead + 1) % PendingCapacity;
            _pendingCount--;
            Volatile.Write(ref _recordsDropped, _recordsDropped + 1);
        }

        _pending[(_pendingHead + _pendingCount) % PendingCapacity] = new Pending(job.SlotIndex, slot.Write, outcome, source, start, end, -1);
        _pendingCount++;
    }

    private static bool IsFaulted(IFrameGenerator generator) => generator is Vfx.VfgGenerator { IsFaulted: true };

    private void CopyReal(in GpuFrame frame, in BridgeSlot slot)
    {
        var stream = _stream;
        stream.Wait(slot.ProducerWait);
        stream.Wait(frame.Ready);
        _copySpans.Begin(stream);
        stream.CopyFrameAsync(frame, slot.ProducerView);
        _copySpans.End(stream);
        stream.Signal(slot.ProducerSignal);
    }

    /// <summary>Emits the records of writes whose GPU work has finished, in write order. Allocation-free.</summary>
    private void Collect()
    {
        while (_pendingCount > 0)
        {
            ref var head = ref _pending[_pendingHead];
            if (head.GpuNs < 0)
            {
                long ns = 0;
                var read = head.Source switch
                {
                    SpanSource.Copy => _copySpans.TryRead(out ns),
                    SpanSource.Generator => _generator!.ReadGpuTime!(out ns),
                    _ => true,
                };
                if (!read)
                {
                    return;
                }

                head = head with { GpuNs = ns };
                if (head.Source == SpanSource.Generator && !_generationTimes.TryEnqueue(ns))
                {
                    Volatile.Write(ref _samplesDropped, _samplesDropped + 1);
                }
            }

            long endNs = 0;
            if (_clock.IsStarted && !_clock.TryRead((long)head.Write, out endNs))
            {
                return; // its mark has not completed yet (it follows the span by a few operations)
            }

            var record = new WriteRecord
            {
                SlotIndex = head.SlotIndex,
                Write = head.Write,
                Outcome = head.Outcome,
                HostStartNs = head.HostStartNs,
                HostEndNs = head.HostEndNs,
                GpuNs = head.GpuNs,
                GpuEndNs = endNs,
            };
            if (!_records.TryEnqueue(record))
            {
                Volatile.Write(ref _recordsDropped, _recordsDropped + 1);
            }

            _pendingHead = (_pendingHead + 1) % PendingCapacity;
            _pendingCount--;
        }
    }

    public void Dispose()
    {
        _copySpans.Dispose();
        _clock.Dispose();
        _wake.Dispose();
    }

    private enum SpanSource : byte
    {
        None = 0,
        Copy = 1,
        Generator = 2,
    }

    private readonly record struct Pending(long SlotIndex, ulong Write, WriteOutcome Outcome, SpanSource Source, long HostStartNs, long HostEndNs, long GpuNs);
}
