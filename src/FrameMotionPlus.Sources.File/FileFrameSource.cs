using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Collections;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Platform.Threading;
using FrameMotionPlus.Platform.Time;
using FrameMotionPlus.Sources.File.Interop;

namespace FrameMotionPlus.Sources.File;

/// <summary>
/// A video file as an <see cref="IFrameSource"/>: FFmpeg demuxes it, NVDEC decodes it through FFmpeg's CUDA hwaccel
/// in the application's CUDA context, and each decoded NV12/P010 picture is converted on the GPU into an RGB frame of
/// the <see cref="History"/> (RGBA8 for 8-bit sources, RGB10A2 for 10-bit), never touching the CPU.
/// <para>
/// <b>Threads.</b> A dedicated decode thread (<see cref="Start"/>) decodes ahead into a bounded queue: at most
/// <see cref="FileSourceOptions.DecodeAhead"/> frames wait for the consumer, and when the consumer is slower the decode
/// thread waits for it (back-pressure; no source frame is ever dropped). It sleeps on an event that the consumer's reads
/// and releases, a seek and <see cref="Stop"/> set; it never polls. <see cref="TryRead"/>, <see cref="Release"/>,
/// <see cref="TryGetFrameInfo"/> and <see cref="TryReadTiming"/> belong to one consumer thread and never block or
/// allocate. <see cref="Seek"/> may come from any thread.
/// </para>
/// <para>
/// <b>GPU work per frame</b> (see docs/benchmarks/phase4a-decode.md for what FFmpeg and NVDEC do in the context):
/// on <see cref="CopyStream"/>, a wait for the previous frame's conversion (so FFmpeg's copy into a recycled pool
/// buffer cannot overtake it), then FFmpeg's map and copy of the decoded picture; on <see cref="ComputeStream"/>, a wait
/// for that copy and for the history buffer's last reader (the consumer's <see cref="Release"/> point), the
/// conversion, and the buffer's Ready event. The source's own code never waits for the GPU, but the decode thread does
/// inside FFmpeg: NVDEC's <c>cuvidMapVideoFrame</c> synchronises (<c>cuEventSynchronize</c>) once per frame on an event
/// behind the work already on the copy stream, which includes the previous frame's conversion and so, through it, the
/// release point of the buffer that conversion wrote. When the GPU or a consumer's release point is behind, the decode
/// thread blocks there until it catches up; a release point that never completes blocks it for good (<see cref="Stop"/>
/// then times out and <see cref="Dispose"/> leaves everything allocated, see <see cref="Leaked"/>). No other thread is
/// held up.
/// </para>
/// <para>
/// <b>Time.</b> Content times come from the presentation timestamps (<see cref="ContentClock"/>): distinct and strictly
/// increasing per delivered frame, in presentation order, across loops, seeks and jumps in the stream's own timestamps.
/// The first frame after a loop or a seek, and a frame whose timestamp jumps (concatenated recordings, a reset clock),
/// carries <see cref="SourceFrameMarks.Discontinuity"/>. After a loop, a seek or a jump back it continues one frame
/// interval after the last frame <i>decoded</i> (after a seek, frames queued before it are dropped unseen, so the content
/// time the consumer sees advances by (dropped frames + 1) intervals); a gap forward of more than 10 s keeps its time in a
/// container with a continuous timeline (MP4, Matroska) and is collapsed to one interval in MPEG-TS/PS. <see cref="TryGetFrameInfo"/> says which event it was. A frame whose colour
/// description differs from the previous one carries <see cref="SourceFrameMarks.FormatChanged"/>. At the end of the
/// file without looping the queue drains and <see cref="IsEndOfStream"/> becomes true.
/// </para>
/// <para>
/// <b>Colour.</b> Frames are RGB, and their <see cref="ColorMetadata"/> describes them as such: identity matrix, full
/// range, with the stream's primaries, transfer function and HDR metadata. The YCbCr description they were converted
/// from is the stream's (<see cref="FileStreamInfo.RawColor"/>, <see cref="FileStreamInfo.Conversion"/>) and, per frame,
/// <see cref="FileFrameInfo.Conversion"/>.
/// </para>
/// <para>
/// <b>Memory.</b> Everything is allocated by the constructor (which also decodes the first frame, so FFmpeg's decoder
/// and frame pool exist before playback), and freed by <see cref="Dispose"/>, a drained point. Steady playback neither
/// allocates nor frees CUDA memory (<see cref="FileSourceStatistics.PoolGrowth"/> counts any FFmpeg pool growth) nor
/// managed memory.
/// </para>
/// </summary>
public sealed unsafe class FileFrameSource : IFrameSource
{
    private const int TimingCapacity = 1024;
    private const int PendingTimings = 64;
    private const int AllocationWarmupFrames = 8;

    private readonly CudaContext _context;
    private readonly FileSourceOptions _options;
    private readonly MediaDecoder _decoder;
    private readonly ContentClock _clock;
    private readonly bool _ownsCopy;
    private readonly bool _ownsCompute;
    private readonly bool _ownsKernels;
    private readonly ImageKernels _kernels;
    private readonly CudaEvent _copyDone;
    private readonly CudaEvent _convertDone;
    private readonly CudaSpanTimerRing _copySpans;
    private readonly CudaSpanTimerRing _convertSpans;
    private readonly SpscRing<QueuedFrame> _queue;
    private readonly SpscRing<DecodeTiming> _timings;
    private readonly PendingTiming[] _pending = new PendingTiming[PendingTimings];
    // An AutoResetEvent, not a ManualResetEventSlim: under NativeAOT the slim event's Monitor-based slow path allocates
    // lazily created state the first time a thread contends for it, which can be at any point of a long run (seen in the
    // 10-minute gate: 120 B, once, on the decode thread). The wait subsystem's event allocates nothing after a thread's first
    // wait. A Set while nobody waits leaves it signalled, so a wake-up between the check and the wait is never lost.
    private readonly AutoResetEvent _wake = new(false);
    private readonly FileFrameInfo[] _frameInfo = null!;
    private readonly int _queueCapacity;
    private AVFrame* _frame;
    private YuvConversion _conversion;
    private RawColorDescription _conversionKey;
    private ColorMetadata _lastColor;
    private MasteringDisplay _lastMastering;
    private ContentLightLevel _lastLightLevel;
    private DedicatedThread? _thread;
    private Exception? _failure;
    private bool _firstFrameHeld;
    private FileFrameEvents _pendingEvents;
    private bool _ended;
    private int _stop;
    private int _requestedEpoch;
    private int _epoch;
    private long _seekTargetNs = long.MinValue;
    private long _skipBeforeMediaNs = long.MinValue;
    private long _nextFrameId;
    private int _pendingHead;
    private int _pendingCount;
    private bool _timingLost;
    private bool _firstPathThisFrame;
    private bool _startedOnce;
    private bool _disposed;
    private bool _freed;
    private volatile bool _leaked;

    // Counters: written by the decode thread (consumer-side ones by the consumer), read anywhere.
    private long _framesDecoded;
    private long _framesDelivered;
    private long _framesSkipped;
    private long _loops;
    private long _seeks;
    private long _backPressureWaits;
    private int _maxQueueDepth;
    private long _decodeAllocated;
    private long _droppedTimings;
    private long _seeksFromStart;
    private long _failedSeeks;
    private long _receivedSinceRestart;
    private string? _emptyLapError;
    private int _timingGate; // 1 while a thread collects or adds pending timings (the decode thread, or the consumer once it is stopped)

    /// <summary>
    /// Opens <paramref name="path"/>, checks NVDEC can decode it, decodes its first frame and allocates every buffer.
    /// Throws <see cref="FFmpegUnavailableException"/> without FFmpeg 9, <see cref="FileNotFoundException"/>, or
    /// <see cref="FileSourceException"/> for a file that cannot be played (with the reason).
    /// </summary>
    public FileFrameSource(CudaContext context, string path, FileSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(context.IsDisposed, context);
        options ??= new FileSourceOptions();
        options.Validate();
        _context = context;
        _options = options;
        if (options.Kernels is { } kernels && kernels.Context.Handle != context.Handle)
        {
            throw new ArgumentException("The kernels are loaded in another CUDA context.", nameof(options));
        }

        context.MakeCurrent();
        _ownsCopy = options.CopyStream is null;
        _ownsCompute = options.ComputeStream is null;
        _ownsKernels = options.Kernels is null;
        CopyStream = options.CopyStream ?? CudaStream.Create(context);
        ComputeStream = options.ComputeStream ?? CudaStream.Create(context);
        try
        {
            _kernels = options.Kernels ?? ImageKernels.Load(context);
            _copyDone = CudaEvent.Create(context);
            _convertDone = CudaEvent.Create(context);
            _copySpans = CudaSpanTimerRing.Create(context, PendingTimings);
            _convertSpans = CudaSpanTimerRing.Create(context, PendingTimings);
            _decoder = new MediaDecoder(path, context, CopyStream);
            StreamInfo = _decoder.Info;
            _conversion = StreamInfo.Conversion;
            _conversionKey = StreamInfo.RawColor;
            _lastMastering = StreamInfo.Color.Mastering;
            _lastLightLevel = StreamInfo.Color.LightLevel;
            var interval = StreamInfo.NominalRate.IsValid ? (long)StreamInfo.NominalRate.PeriodNs : 1_000_000_000L / 30;
            _clock = new ContentClock(
                StreamInfo.TimeBaseNum,
                StreamInfo.TimeBaseDen,
                StreamInfo.StartTicks == long.MinValue ? Av.NoPtsValue : StreamInfo.StartTicks,
                interval,
                options.ContentTimeOriginNs,
                collapseForwardJumps: StreamInfo.DiscontinuousTimestamps);
            History = new FrameHistory(
                context,
                StreamInfo.Width,
                StreamInfo.Height,
                StreamInfo.OutputFormat,
                options.DecodeAhead + options.HistoryDepth,
                options.KeepDecodedPlanes ? StreamInfo.SampleFormat : YuvSampleFormat.Unknown);
            _queueCapacity = options.DecodeAhead;
            var ringSize = 2;
            while (ringSize < History.Capacity)
            {
                ringSize <<= 1;
            }

            _frameInfo = new FileFrameInfo[History.Capacity];
            _queue = new SpscRing<QueuedFrame>(ringSize);
            _timings = new SpscRing<DecodeTiming>(TimingCapacity);
            _frame = AvUtil.av_frame_alloc();
            if (_frame == null)
            {
                throw new FileSourceException("av_frame_alloc failed." + " (FFmpeg is out of memory)");
            }

            // The first frame now: an undecodable stream fails here, and the decoder and its frame pool are built
            // before playback (start-up).
            if (!_decoder.Receive(_frame))
            {
                throw new FileSourceException($"'{path}' has a video stream but no frame could be decoded from it.");
            }

            _firstFrameHeld = true;

            // The frames' own colour description is authoritative (a container may leave it unset while the bitstream
            // carries it): the stream's info and the conversion follow the first frame.
            _lastColor = FrameColor();
            StreamInfo = StreamInfo with
            {
                ContainerRawColor = StreamInfo.RawColor,
                RawColor = _conversionKey,
                Color = _lastColor,
                Conversion = _conversion,
            };
            Info = new FrameSourceInfo
            {
                Kind = FrameSourceKind.File,
                Width = StreamInfo.Width,
                Height = StreamInfo.Height,
                Format = StreamInfo.OutputFormat,
                Color = StreamInfo.Color,
                NominalRate = StreamInfo.NominalRate,
            };

        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Whether file playback can work in this process (FFmpeg 9's libraries); false with the reason otherwise.</summary>
    public static bool IsAvailable([NotNullWhen(false)] out string? reason) => FFmpegLibraries.TryLoad(out reason);

    /// <summary>The FFmpeg libraries in use, once loaded ("libavcodec 63.1.102, ...").</summary>
    public static string? FFmpegVersions => FFmpegLibraries.LoadedVersions;

    public FrameSourceInfo Info { get; }

    /// <summary>The stream as opened: codec, size, bit depth, rate, colour and HDR metadata, the conversion.</summary>
    public FileStreamInfo StreamInfo { get; }

    /// <summary>The RGB frame buffers (queued and held).</summary>
    public FrameHistory History { get; } = null!;

    /// <summary>Where FFmpeg and NVDEC work (map and copy of each decoded picture).</summary>
    public CudaStream CopyStream { get; }

    /// <summary>Where the conversion runs and each frame's Ready event is recorded.</summary>
    public CudaStream ComputeStream { get; }

    public CudaContext Context => _context;

    /// <summary>
    /// True once the file has ended (without looping) and every decoded frame has been read, and no seek is pending (a
    /// seek asked for at the end is not the end).
    /// </summary>
    public bool IsEndOfStream =>
        Volatile.Read(ref _requestedEpoch) == Volatile.Read(ref _epoch) && Volatile.Read(ref _ended) && _queue.IsEmpty;

    /// <summary>The exception that stopped the decode thread, if any (it then delivers nothing more).</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    /// <summary>Frames waiting to be read (approximate from other threads).</summary>
    public int QueueDepth => _queue.Count;

    public FileSourceStatistics Statistics => new()
    {
        FramesDecoded = Volatile.Read(ref _framesDecoded),
        FramesDelivered = Volatile.Read(ref _framesDelivered),
        FramesSkippedBySeek = Volatile.Read(ref _framesSkipped),
        Loops = Volatile.Read(ref _loops),
        Seeks = Volatile.Read(ref _seeks),
        PtsRepairs = _clock.Repairs,
        PtsMissing = _clock.Missing,
        TimestampDiscontinuities = _clock.Discontinuities,
        StaleFramesDropped = _decoder.StaleFrames,
        SeeksFromStart = Volatile.Read(ref _seeksFromStart),
        FailedSeeks = Volatile.Read(ref _failedSeeks),
        PoolGrowth = _decoder.PoolGrowth,
        CorruptFrames = _decoder.CorruptFrames,
        CorruptPackets = _decoder.CorruptPackets,
        BackPressureWaits = Volatile.Read(ref _backPressureWaits),
        MaxQueueDepth = Volatile.Read(ref _maxQueueDepth),
        DecodeThreadAllocatedBytes = Volatile.Read(ref _decodeAllocated),
        DroppedTimings = Volatile.Read(ref _droppedTimings),
    };

    /// <summary>Why demuxing stopped before the end of the file (a truncated or damaged file), if it did.</summary>
    public string? DemuxError => _decoder.DemuxError;

    /// <summary>
    /// Why the last seek or loop that failed could not be made (the demuxer can seek neither by time nor to the start by
    /// bytes: a pipe, say). A failed loop ends the stream as if not looping; a failed seek is dropped (the next frame is
    /// marked, <see cref="FileFrameEvents.SeekFailed"/>) and decoding goes on.
    /// </summary>
    public string? SeekError => Volatile.Read(ref _emptyLapError) ?? _decoder.SeekError;

    /// <summary>
    /// True when <see cref="Dispose"/> could not free the source because the decode thread did not stop in time (see
    /// <see cref="Stop"/>): its CUDA memory, FFmpeg's decoder and the thread are still there. Call <see cref="Dispose"/>
    /// again once the GPU work it waits for has completed.
    /// </summary>
    public bool Leaked => _leaked;

    /// <summary>How long <see cref="Stop"/> waits for the decode thread (tests shorten it).</summary>
    internal TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The CUDA context and stream FFmpeg's device context holds (tests).</summary>
    internal (nint Context, nint Stream) FFmpegDevice => (_decoder.DeviceContext, _decoder.DeviceStream);

    /// <summary>Test hook, decode thread: runs once per delivered frame inside the allocation-measured part.</summary>
    internal Action? PerFrameHook { get; set; }

    /// <summary>Test hook, consumer thread: runs inside <see cref="TryRead"/> for each frame it returns.</summary>
    internal Action? ReadHook { get; set; }

    /// <summary>
    /// Sets the <c>CLOCK_MONOTONIC</c> time at which the file's first frame counts as having appeared, replacing
    /// <see cref="FileSourceOptions.ContentTimeOriginNs"/>: a pipeline builds the source first (opening a file, creating
    /// its decoder and decoding the first frame take tens of milliseconds) and chooses the origin when it starts playback.
    /// Only before the first <see cref="Start"/>, which is when content times start being assigned; throws afterwards.
    /// </summary>
    public void SetContentTimeOrigin(long originNs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null || Volatile.Read(ref _framesDecoded) > 0 || _startedOnce)
        {
            throw new InvalidOperationException("The content-time origin can only be set before the source is first started.");
        }

        _clock.SetOrigin(originNs);
    }

    /// <summary>Starts the decode thread. Allocation happens here, not afterwards.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is { IsAlive: true })
        {
            return;
        }

        Volatile.Write(ref _stop, 0);
        _startedOnce = true;
        _thread = DedicatedThread.Start("fmp-decode", _options.DecodeThread, Run);
    }

    /// <summary>
    /// Stops the decode thread and keeps the queue and buffers as they are. Bounded (10 s): a thread that does not stop
    /// in time is blocked inside NVDEC on GPU work that has not completed (see the class remarks), and
    /// <see cref="TimeoutException"/> says so; calling again waits again.
    /// </summary>
    public void Stop()
    {
        var thread = _thread;
        if (thread is null)
        {
            return;
        }

        Volatile.Write(ref _stop, 1);
        _wake.Set();
        if (!thread.Join(StopTimeout))
        {
            throw new TimeoutException(
                $"The decode thread did not stop within {StopTimeout.TotalSeconds:0.#} s: it is blocked inside FFmpeg (NVDEC's per-frame " +
                "synchronisation) on GPU work that has not completed, such as a frame's release point (the signal passed to Release) " +
                "that never signals.");
        }

        _thread = null;
    }

    /// <summary>
    /// The next frame in presentation order, if one is ready. Never blocks, never allocates. The frame is held until
    /// <see cref="Release(long, in GpuSignal)"/>; its <see cref="GpuFrame.Ready"/> is the end of its conversion.
    /// </summary>
    public bool TryRead(out SourceFrame frame)
    {
        while (_queue.TryDequeue(out var queued))
        {
            if (queued.Epoch != Volatile.Read(ref _requestedEpoch))
            {
                // Decoded before a seek that has been requested since: never shown.
                History.MarkRead(queued.Frame.FrameId);
                History.Release(queued.Frame.FrameId, GpuSignal.None);
                _wake.Set();
                continue;
            }

            History.MarkRead(queued.Frame.FrameId);
            Volatile.Write(ref _framesDelivered, _framesDelivered + 1);
            frame = queued.Frame;
            _wake.Set(); // a decode thread held back by a full queue may go on
            ReadHook?.Invoke();
            return true;
        }

        frame = default;
        return false;
    }

    /// <summary>The frame is no longer read by any GPU work, pending or future (<see cref="GpuSignal.None"/> as the last reader).</summary>
    public void Release(long frameId) => Release(frameId, GpuSignal.None);

    /// <summary>
    /// No work enqueued after this call reads <paramref name="frameId"/>, and the work enqueued before has finished
    /// reading it once <paramref name="lastReader"/> is reached (the generator's <c>done</c>, a ring write, an event
    /// recorded after the reads): its buffer is written again only after that point, GPU-side
    /// (<see cref="IFrameSource.Release"/>). Consumer thread; allocation-free.
    /// </summary>
    public void Release(long frameId, in GpuSignal lastReader)
    {
        History.Release(frameId, lastReader);
        _wake.Set();
    }

    /// <summary>The decoded NV12/P010 planes of a held frame (with <see cref="FileSourceOptions.KeepDecodedPlanes"/>).</summary>
    public bool TryGetPlanes(long frameId, out YuvPlanes planes) => History.TryGetPlanes(frameId, out planes);

    /// <summary>A held frame's media time, lap, events (why it is marked) and conversion. Consumer thread; allocation-free.</summary>
    public bool TryGetFrameInfo(long frameId, out FileFrameInfo info)
    {
        if (History.TryGetHeldSlot(frameId, out var slot))
        {
            info = _frameInfo[slot];
            return true;
        }

        info = default;
        return false;
    }

    /// <summary>
    /// The measurements of the next delivered frame whose GPU spans have completed. The consumer thread's, like
    /// <see cref="TryRead"/> (one thread reads timings); allocation-free. The decode thread collects them as it goes;
    /// while it is not running (before <see cref="Start"/>, after <see cref="Stop"/>) this collects what has completed
    /// since, on the calling thread. A gate keeps the two from ever collecting at once, so a <see cref="Start"/> from
    /// another thread racing this call is safe: whichever comes second skips (the consumer) or waits briefly (the decode
    /// thread).
    /// </summary>
    public bool TryReadTiming(out DecodeTiming timing)
    {
        if (_thread is null && !_freed && !_disposed && Interlocked.CompareExchange(ref _timingGate, 1, 0) == 0)
        {
            try
            {
                CollectTimingsHeld();
            }
            finally
            {
                Volatile.Write(ref _timingGate, 0);
            }
        }

        return _timings.TryDequeue(out timing);
    }

    /// <summary>
    /// Continues from <paramref name="mediaTimeNs"/> (ns after the stream's start): frames already queued are dropped,
    /// the decoder restarts at the keyframe before it, frames before it are decoded and dropped, and the first one
    /// delivered carries <see cref="SourceFrameMarks.Discontinuity"/>. Any thread; takes effect on the decode thread.
    /// </summary>
    public void Seek(long mediaTimeNs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(mediaTimeNs);
        Volatile.Write(ref _seekTargetNs, mediaTimeNs);
        Interlocked.Increment(ref _requestedEpoch);
        _wake.Set();
    }

    /// <summary>
    /// Stops the decode thread, waits for the source's GPU work, and frees everything (FFmpeg's frame pool and decoder,
    /// the history buffers): a drained point, never the pacing thread. Frames still held must not be used afterwards.
    /// If the decode thread does not stop in time (<see cref="Stop"/>), nothing is freed (freeing under it would crash
    /// it): <see cref="Leaked"/> becomes true, and a later call tries again.
    /// </summary>
    public void Dispose()
    {
        if (_freed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Stop();
        }
        catch (TimeoutException)
        {
            _leaked = true;
            return;
        }

        _leaked = false;
        _freed = true;

        if (!_context.IsDisposed)
        {
            _context.EnsureCurrent();
            if (!CopyStream.IsDisposed)
            {
                CopyStream.Synchronize();
            }

            if (!ComputeStream.IsDisposed)
            {
                ComputeStream.Synchronize();
            }
        }

        if (_frame != null)
        {
            var frame = _frame;
            AvUtil.av_frame_free(&frame);
            _frame = null;
        }

        _decoder?.Dispose();
        History?.Dispose();
        _copySpans?.Dispose();
        _convertSpans?.Dispose();
        _copyDone?.Dispose();
        _convertDone?.Dispose();
        if (_ownsKernels)
        {
            _kernels?.Dispose();
        }

        if (_ownsCopy)
        {
            CopyStream.Dispose();
        }

        if (_ownsCompute)
        {
            ComputeStream.Dispose();
        }

        _wake.Dispose();
    }

    private void Run()
    {
        try
        {
            _context.MakeCurrent();

            // First uses on this thread, before anything is measured: a blocking wait (the thread's first wait allocates
            // its wait state once).
            _ = _wake.WaitOne(1);
            var allocationBase = -1L;
            while (Volatile.Read(ref _stop) == 0)
            {
                CollectTimings();
                if (Volatile.Read(ref _requestedEpoch) != _epoch)
                {
                    BeginSeek();
                }

                if (_ended)
                {
                    Idle();
                    continue;
                }

                // Bounded decode-ahead: at most DecodeAhead frames wait for the consumer, and a history buffer must be
                // free (the consumer holds the rest). Otherwise wait for the consumer: no frame is ever dropped.
                if (_queue.Count >= _queueCapacity)
                {
                    _backPressureWaits++;
                    Idle();
                    continue;
                }

                var slot = History.TryBeginWrite(ComputeStream);
                if (slot < 0)
                {
                    _backPressureWaits++;
                    Idle();
                    continue;
                }

                if (!Decode(out var hostNs))
                {
                    History.Abandon(slot);
                    EndOfFile();
                    continue;
                }

                _receivedSinceRestart++;

                if (_skipBeforeMediaNs != long.MinValue && _clock.Peek(FrameTicks(), _frame->Duration) < _skipBeforeMediaNs)
                {
                    // Decoded on the way to a seek target: not shown (its copy span is read and dropped in order). The
                    // decoder holds nothing from before the seek (MediaDecoder.Seek), so every frame here is from the
                    // new position, and the first one at or after the target is the first shown.
                    _clock.Skip(FrameTicks(), _frame->Duration);
                    History.Abandon(slot);
                    AvUtil.av_frame_unref(_frame);
                    AddPending(new PendingTiming(default, NeedsCopy: hostNs != 0, NeedsConvert: false, Emit: false));
                    Volatile.Write(ref _framesSkipped, _framesSkipped + 1);
                    continue;
                }

                _skipBeforeMediaNs = long.MinValue;
                Publish(slot, hostNs);

                // Managed allocations of the decode thread from one delivered frame to the next (waits and skipped frames
                // included), after its first frames, and not across the first loop or seek: those run paths for the first
                // time (under the JIT, binding a P/Invoke on its first call allocates; NativeAOT binds nothing at run time).
                var now = GC.GetAllocatedBytesForCurrentThread();
                if (allocationBase >= 0 && !_firstPathThisFrame)
                {
                    Volatile.Write(ref _decodeAllocated, _decodeAllocated + (now - allocationBase));
                }

                allocationBase = _framesDecoded >= AllocationWarmupFrames ? now : -1;
                _firstPathThisFrame = false;
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _failure, e);
            Volatile.Write(ref _ended, true);
        }
    }

    /// <summary>
    /// Sleeps until the consumer reads or releases a frame, a seek is asked for or <see cref="Stop"/> is called. Every
    /// caller re-checks its condition afterwards, and those all change before the event is set; a set while the thread is
    /// not waiting leaves the event signalled, so no wake-up is lost. Allocation-free.
    /// </summary>
    private void Idle() => _ = _wake.WaitOne();

    private long FrameTicks() => _frame->BestEffortTimestamp == Av.NoPtsValue ? _frame->Pts : _frame->BestEffortTimestamp;

    /// <summary>The next decoded frame into <see cref="_frame"/>, with the copy stream ordered behind the last conversion.</summary>
    private bool Decode(out long hostNs)
    {
        hostNs = 0;
        if (_firstFrameHeld)
        {
            _firstFrameHeld = false;
            return true;
        }

        // FFmpeg's copy may land in a pool buffer the previous conversion still reads.
        CopyStream.Wait(_convertDone);
        _copySpans.Begin(CopyStream);
        var started = Stopwatch.GetTimestamp();
        bool received;
        try
        {
            received = _decoder.Receive(_frame);
        }
        catch
        {
            _copySpans.Abort();
            throw;
        }

        hostNs = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        if (!received)
        {
            _copySpans.Abort();
            return false;
        }

        _copySpans.End(CopyStream);
        return true;
    }

    private void Publish(int slot, long hostNs)
    {
        var copy = CopyStream;
        var compute = ComputeStream;
        _copyDone.Record(copy);
        compute.Wait(_copyDone);
        var planes = _decoder.Planes(_frame);
        var color = FrameColor();
        if (History.PlaneFormat != YuvSampleFormat.Unknown)
        {
            var keep = History.PlaneTarget(slot);
            var rowBytes = (long)planes.Width * planes.BytesPerSample;
            compute.Copy2DAsync(keep.Luma, keep.LumaPitch, planes.Luma, planes.LumaPitch, rowBytes, planes.Height);
            compute.Copy2DAsync(keep.Chroma, keep.ChromaPitch, planes.Chroma, planes.ChromaPitch, (long)((planes.Width + 1) / 2) * 2 * planes.BytesPerSample, (planes.Height + 1) / 2);
        }

        _convertSpans.Begin(compute);
        _kernels.EnqueueYuvToRgb(compute, planes, History.Target(slot), _conversion);
        _convertSpans.End(compute);
        var content = _clock.Next(FrameTicks(), _frame->Duration, out var media, out var jumped);
        var events = _pendingEvents;
        _pendingEvents = FileFrameEvents.None;
        events |= jumped ? FileFrameEvents.TimestampJump : FileFrameEvents.None;
        events |= _clock.LastWasRepaired ? FileFrameEvents.TimestampRepaired : FileFrameEvents.None;
        var id = _nextFrameId++;
        _frameInfo[slot] = new FileFrameInfo { MediaTimeNs = media, Lap = _loops, Events = events, Conversion = _conversion };
        var frame = History.CompleteWrite(slot, compute, id, content, color);
        _convertDone.Record(compute);
        AvUtil.av_frame_unref(_frame); // the pool buffer goes back; its next copy waits for _convertDone

        var marks = SourceFrameMarks.None;
        if ((events & (FileFrameEvents.Loop | FileFrameEvents.Seek | FileFrameEvents.TimestampJump | FileFrameEvents.SeekFailed)) != 0)
        {
            marks |= SourceFrameMarks.Discontinuity;
        }

        if (id > 0 && color != _lastColor)
        {
            marks |= SourceFrameMarks.FormatChanged;
        }

        _lastColor = color;
        PerFrameHook?.Invoke();
        var queued = new QueuedFrame(
            new SourceFrame { FrameId = id, Frame = frame, ArrivalTimeNs = Monotonic.NowNs, Marks = marks },
            _epoch);
        if (!_queue.TryEnqueue(queued))
        {
            // Cannot happen: the queue holds at least as many entries as there are history buffers.
            throw new InvalidOperationException("The source's frame queue is full.");
        }

        var depth = _queue.Count;
        if (depth > _maxQueueDepth)
        {
            Volatile.Write(ref _maxQueueDepth, depth);
        }

        Volatile.Write(ref _framesDecoded, _framesDecoded + 1);
        var timing = new DecodeTiming
        {
            FrameId = id,
            ContentTimeNs = content,
            MediaTimeNs = media,
            HostDecodeNs = hostNs,
            DecodeCopyGpuNs = -1, // the first frame was decoded before the thread started, without a span
            QueueDepth = depth,
            Marks = marks,
            Events = events,
        };
        AddPending(new PendingTiming(timing, NeedsCopy: hostNs != 0, NeedsConvert: true, Emit: true));
    }

    /// <summary>
    /// The frame's colour description: its own fields (the conversion follows them; a change of matrix, range or siting
    /// mid-stream builds a new conversion, rarely), its HDR side data, else the last seen, else the stream's.
    /// </summary>
    private ColorMetadata FrameColor()
    {
        var f = _frame;
        var raw = new RawColorDescription(f->ColorPrimaries, f->ColorTrc, f->ColorSpace, f->ColorRange, f->ChromaLocation);
        if (raw != _conversionKey)
        {
            var matrix = ColorMapping.Matrix(raw, StreamInfo.Height);
            if (matrix is { } m)
            {
                var range = ColorMapping.Range(raw);
                var siting = ColorMapping.Siting(raw);
                if (m != _conversion.Matrix || range != _conversion.Range || siting != _conversion.Siting)
                {
                    _conversion = YuvConversion.Create(m, range, StreamInfo.BitDepth, siting, StreamInfo.OutputFormat);
                }
            }

            _conversionKey = raw;
        }

        var masteringData = AvUtil.av_frame_get_side_data(f, Av.FrameDataMasteringDisplayMetadata);
        if (masteringData != null)
        {
            _lastMastering = ColorMapping.Mastering((AVMasteringDisplayMetadata*)masteringData->Data);
        }

        var lightData = AvUtil.av_frame_get_side_data(f, Av.FrameDataContentLightLevel);
        if (lightData != null)
        {
            _lastLightLevel = ColorMapping.LightLevel((AVContentLightMetadata*)lightData->Data);
        }

        return ColorMapping.ToColorMetadata(raw, _conversion.Matrix, _lastMastering, _lastLightLevel);
    }

    /// <summary>
    /// Restarts the decoder (a seek or a loop). The decoder may still return frames it held from before (MediaDecoder.Seek
    /// receives and drops them), and FFmpeg copies each into a frame-pool buffer on the copy stream, which may be the buffer
    /// the last conversion still reads (its history buffer can be waiting, GPU-side, for a consumer's release point): the
    /// copy stream waits for that conversion first, exactly as before every receive in <see cref="Decode"/>.
    /// <para>
    /// Every path on which the decoder can produce a frame is behind this wait: <see cref="Decode"/> (normal decoding,
    /// the end-of-stream drain and the frames <c>MediaDecoder.Receive</c> drops before the first packet from a new
    /// position all happen inside its receive) and this method (the seek's and the loop's drain). The constructor's first
    /// frame comes before any conversion; <see cref="Stop"/> and <see cref="Dispose"/> receive nothing.
    /// </para>
    /// </summary>
    private SeekOutcome SeekDecoder(long ticks)
    {
        CopyStream.Wait(_convertDone);
        return _decoder.Seek(ticks);
    }

    private void EndOfFile()
    {
        // A seek target past the end skips no further: the next lap (or nothing) is shown.
        _skipBeforeMediaNs = long.MinValue;
        if (!_options.Loop)
        {
            Volatile.Write(ref _ended, true);
            return;
        }

        if (_loops > 0 && _receivedSinceRestart == 0)
        {
            // The last restart at the beginning gave no frame at all: looping again would spin for ever.
            Volatile.Write(ref _emptyLapError, $"'{StreamInfo.Path}' gave no frame after restarting at its beginning; looping stopped");
            Volatile.Write(ref _failedSeeks, _failedSeeks + 1);
            Volatile.Write(ref _ended, true);
            return;
        }

        var outcome = SeekDecoder(StreamInfo.StartTicks == long.MinValue ? 0 : StreamInfo.StartTicks);
        _firstPathThisFrame |= _loops == 0;
        if (outcome == SeekOutcome.Failed)
        {
            // The file cannot be restarted (SeekError says why): it ends as if played once.
            Volatile.Write(ref _failedSeeks, _failedSeeks + 1);
            Volatile.Write(ref _ended, true);
            return;
        }

        _receivedSinceRestart = 0;

        if (outcome == SeekOutcome.Start)
        {
            Volatile.Write(ref _seeksFromStart, _seeksFromStart + 1);
        }

        _clock.Rebase(fromStart: true);
        _pendingEvents |= FileFrameEvents.Loop;
        Volatile.Write(ref _loops, _loops + 1);
    }

    private void BeginSeek()
    {
        // Not ended before the seek is taken up, so IsEndOfStream never sees the new epoch with the old end.
        Volatile.Write(ref _ended, false);
        Volatile.Write(ref _epoch, Volatile.Read(ref _requestedEpoch));
        var target = Volatile.Read(ref _seekTargetNs);
        if (_firstFrameHeld)
        {
            AvUtil.av_frame_unref(_frame);
            _firstFrameHeld = false;
        }

        _firstPathThisFrame |= _seeks == 0;
        Volatile.Write(ref _seeks, _seeks + 1);
        var outcome = SeekDecoder(_clock.TicksAt(target));
        if (outcome == SeekOutcome.Failed)
        {
            // Nothing moved: decoding goes on from where it was. The frames queued before the request are dropped
            // (their epoch is gone), so the next frame is marked.
            Volatile.Write(ref _failedSeeks, _failedSeeks + 1);
            _pendingEvents |= FileFrameEvents.SeekFailed;
            return;
        }

        if (outcome == SeekOutcome.Start)
        {
            Volatile.Write(ref _seeksFromStart, _seeksFromStart + 1);
        }

        _clock.Rebase(fromStart: outcome == SeekOutcome.Start);
        _skipBeforeMediaNs = target;
        _pendingEvents = (_pendingEvents & ~FileFrameEvents.SeekFailed) | FileFrameEvents.Seek;
    }

    /// <summary>
    /// Queues a frame's measurements until its GPU spans complete. Spans are read in the order they were recorded, so
    /// every frame that recorded one has an entry, shown or not. The span rings hold <see cref="PendingTimings"/>, as
    /// many as there are entries, so an entry is dropped only when the GPU is a full ring behind (which also drops spans).
    /// </summary>
    private void AddPending(in PendingTiming entry)
    {
        EnterTimingGate();
        try
        {
            AddPendingHeld(entry);
        }
        finally
        {
            Volatile.Write(ref _timingGate, 0);
        }
    }

    private void AddPendingHeld(in PendingTiming entry)
    {
        CollectTimingsHeld();
        if (_timingLost || _pendingCount == PendingTimings || _copySpans.Dropped + _convertSpans.Dropped > 0)
        {
            // The GPU fell a whole ring of spans behind (not seen: NVDEC's map waits keep the decode thread close to the
            // GPU). Spans can no longer be matched to frames: stop reporting rather than misattribute.
            _timingLost = true;
            Volatile.Write(ref _droppedTimings, _droppedTimings + 1);
            return;
        }

        _pending[(_pendingHead + _pendingCount) % PendingTimings] = entry;
        _pendingCount++;
    }

    /// <summary>Decode thread: <see cref="CollectTimingsHeld"/> under the timing gate.</summary>
    private void CollectTimings()
    {
        EnterTimingGate();
        try
        {
            CollectTimingsHeld();
        }
        finally
        {
            Volatile.Write(ref _timingGate, 0);
        }
    }

    /// <summary>Decode thread: takes the timing gate, waiting (spinning, allocation-free) while a stopped-source consumer holds it.</summary>
    private void EnterTimingGate()
    {
        var spin = default(SpinWait);
        while (Interlocked.CompareExchange(ref _timingGate, 1, 0) != 0)
        {
            spin.SpinOnce();
        }
    }

    /// <summary>
    /// Completes pending measurements whose GPU spans have finished (in order) and hands them to the consumer. Only with
    /// the timing gate held.
    /// </summary>
    private void CollectTimingsHeld()
    {
        while (_pendingCount > 0 && !_timingLost)
        {
            ref var head = ref _pending[_pendingHead];
            if (head.NeedsCopy)
            {
                if (!_copySpans.TryRead(out var copyNs))
                {
                    return;
                }

                head = head with { Timing = head.Timing with { DecodeCopyGpuNs = copyNs }, NeedsCopy = false };
            }

            if (head.NeedsConvert)
            {
                if (!_convertSpans.TryRead(out var convertNs))
                {
                    return;
                }

                head = head with { Timing = head.Timing with { ConvertGpuNs = convertNs }, NeedsConvert = false };
            }

            var done = head;
            _pendingHead = (_pendingHead + 1) % PendingTimings;
            _pendingCount--;
            if (done.Emit && !_timings.TryEnqueue(done.Timing))
            {
                Volatile.Write(ref _droppedTimings, _droppedTimings + 1);
            }
        }
    }

    private readonly record struct QueuedFrame(SourceFrame Frame, int Epoch);

    private readonly record struct PendingTiming(DecodeTiming Timing, bool NeedsCopy, bool NeedsConvert, bool Emit);
}
