using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Cadence;
using FrameMotionPlus.Core.Collections;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Platform.Threading;
using FrameMotionPlus.Platform.Time;
using FrameMotionPlus.Sources.File;
using FrameMotionPlus.Sources.V4l2.Interop;

namespace FrameMotionPlus.Sources.V4l2;

/// <summary>
/// A V4L2 capture device (a capture card) as a live <see cref="IFrameSource"/>: frames are captured into CUDA-pinned
/// buffers, uploaded, judged by the cadence detector (repeats of a picture are dropped) and converted on the GPU into RGB
/// frames of a <see cref="FrameHistory"/> (RGB10A2 for 10-bit captures, RGBA8 otherwise), with the same Ready/release
/// protocol as the file source.
/// <para>
/// <b>Dequeue thread</b> (<see cref="Start"/>; real-time priority asked for, degrading gracefully). It sleeps in
/// <c>poll</c> on the device (and on an eventfd <see cref="Stop"/> writes), dequeues, and when several buffers are ready
/// keeps the newest and requeues the rest unseen (newest wins; counted). Per frame, on the copy stream: a wait for the last
/// conversion that read the staging buffer, the host-to-device copy of the frame, the cadence metric against the last
/// unique frame's grid and a 16-byte read-back of it; the thread then waits for that work (polling an event: it is the only
/// thread that waits, and its wait is bounded and interruptible), requeues the capture buffer at once (the upload is
/// done), and feeds the metric to Core's <see cref="CadenceTracker"/>. A repeat goes no further. A new picture takes a
/// free history buffer (none free or the queue full: dropped, counted; nothing ever waits for the consumer) and is
/// converted on the compute stream, behind the buffer's last reader (GPU-side), and published with its content time: the
/// driver's CLOCK_MONOTONIC timestamp of the capture where the picture first appeared.
/// </para>
/// <para>
/// <b>Memory.</b> Everything is allocated by the constructor and freed by <see cref="Dispose"/> (drained points). Per frame
/// nothing is allocated, freed or synchronised context-wide; the consumer path (<see cref="TryRead"/>, <see cref="Release"/>)
/// is allocation-free too.
/// </para>
/// <para>
/// <b>Ending.</b> A device that goes away, a streaming error, a source change (the input's format changed) or GPU work that
/// never finishes ends the capture: <see cref="Failure"/> says why (a <see cref="CaptureEndedException"/>), nothing more is
/// published, and the thread exits. A signal that stops (no frames) is reported (<see cref="State"/>), not an end: frames
/// resume by themselves, the first one after the gap marked as a discontinuity.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class V4l2FrameSource : IFrameSource
{
    private const int StagingBuffers = 2;
    private const int MaxSwitchLog = 64;
    private const int PendingTimings = 64;
    private const int AllocationWarmupFrames = 16;
    private const long PollTimeoutMs = 50;
    private const long GpuPollSleepNs = 40 * Nanoseconds.PerMicrosecond;

    /// <summary>Buffers after each start left out of the interval statistics: the card's start-up (a first frame, then up to
    /// 0.8 s of nothing on the Elgato, then error-flagged frames) is not the signal's jitter.</summary>
    private const int IntervalWarmupBuffers = 30;

    private readonly CudaContext _context;
    private readonly CaptureSourceOptions _options;
    private readonly V4l2Device _device;
    private readonly CaptureBuffers _buffers;
    private readonly bool _ownsCopy;
    private readonly bool _ownsCompute;
    private readonly bool _ownsKernels;
    private readonly ImageKernels _kernels;
    private readonly CudaDeviceBuffer[] _staging = new CudaDeviceBuffer[StagingBuffers];
    private readonly CudaEvent[] _convertDone = new CudaEvent[StagingBuffers];
    private readonly CudaDeviceBuffer[] _grids = new CudaDeviceBuffer[2];
    private readonly CudaDeviceBuffer _metricDevice;
    private readonly CudaPinnedBuffer _metricHost;
    private readonly CudaEvent _metricReady;
    private readonly CudaSpanTimerRing _uploadSpans;
    private readonly CudaSpanTimerRing _cadenceSpans;
    private readonly CudaSpanTimerRing _convertSpans;
    private readonly SpscRing<SourceFrame> _queue;
    private readonly CadenceTracker _tracker;
    private readonly CadenceGrid _grid;
    private readonly int _eventFd = -1;
    private readonly bool _sourceChangeEvents;

    // The two colours the capture can be in and their conversions; which one is in force is the dequeue thread's.
    private readonly CaptureColor _hdrColor = null!;
    private readonly CaptureColor _sdrColor = null!;
    private readonly YuvConversion _hdrConversion;
    private readonly YuvConversion _sdrConversion;
    private readonly SampleDepthFollower _depth = null!;
    private readonly ColorSwitch[] _switches = new ColorSwitch[MaxSwitchLog];
    private YuvConversion _conversion;
    private bool _isPq;
    private bool _colorChangedNext;
    private int _colorRequest;
    private int _following;
    private int _hdrNow;
    private int _switchCount;
    private long _colorSwitches;
    private SampleDepthReading _lastDepth;
    private long _firstTimestampNs;
    private readonly long _blackCellSum;
    private readonly PendingTiming[] _pending = new PendingTiming[PendingTimings];
    private readonly CaptureFrameRecord[] _log;
    private int _logCount;
    private DedicatedThread? _thread;
    private Exception? _failure;
    private volatile bool _stop;
    private bool _streaming;
    private bool _disposed;
    private bool _abandoned;
    private int _refGrid;
    private int _nextStaging;
    private long _nextFrameId;
    private int _pendingHead;
    private int _pendingCount;
    private bool _timingLost;
    private int _state;

    // Dequeue thread's frame state.
    private uint _lastSequence;
    private bool _haveSequence;
    private long _lastTimestampNs = long.MinValue;
    private int _buffersSinceStart;
    private long _lastContentNs = long.MinValue;
    private long _lastUniqueNs = long.MinValue;
    private long _lastFrameAtNs = long.MinValue;
    private bool _discontinuityNext;

    // Counters (dequeue thread writes; read anywhere).
    private long _dequeued;
    private long _skippedStale;
    private long _driverDropped;
    private long _errorFrames;
    private long _shortFrames;
    private long _captured;
    private long _unique;
    private long _repeats;
    private long _droppedBehind;
    private long _published;
    private long _primed;
    private long _lastPublishedAtNs = long.MinValue;
    private long _blackFrames;
    private long _noSignalEpisodes;
    private long _holds;
    private long _timestampRepairs;
    private long _nonMonotonic;
    private long _allocated;
    private long _droppedTimings;
    private long _detectedIntervalNs;

    /// <summary>
    /// Opens <paramref name="options"/>' device, negotiates the format, and allocates every buffer (capture buffers,
    /// staging, grids, the history) and loads the kernels: a drained point. Throws <see cref="CaptureUnavailableException"/>
    /// (no such device, busy, not a capture device, no buffer mode accepted) or <see cref="CaptureFormatException"/> (the
    /// device does not offer what was asked, or set something else), with one-line messages.
    /// </summary>
    public V4l2FrameSource(CudaContext context, CaptureSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(context.IsDisposed, context);
        options.Validate();
        if (!CaptureDevices.IsAvailable(out var why))
        {
            throw new CaptureUnavailableException(why);
        }

        if (options.Kernels is { } k && k.Context.Handle != context.Handle)
        {
            throw new ArgumentException("The kernels are loaded in another CUDA context.", nameof(options));
        }

        _context = context;
        _options = options;
        _device = V4l2Device.Open(options.Device);
        try
        {
            DeviceInfo = _device.QueryCapabilities();
            if (!DeviceInfo.IsVideoCapture)
            {
                throw new CaptureUnavailableException(DeviceInfo.IsMultiplanarOnly
                    ? $"{options.Device} ({DeviceInfo.Card}) is a multi-planar capture device, which this source does not support"
                    : $"{options.Device} ({DeviceInfo.Card}, {DeviceInfo.Driver}) is not a video capture device (a metadata or output node?)");
            }

            var description = CaptureDevices.Describe(_device);
            if (description.Modes.Count == 0)
            {
                var offered = string.Join(", ", description.Formats.Select(f => f.Format.Compressed ? f.Format.Name + " (compressed)" : f.Format.Name));
                throw new CaptureFormatException(
                    $"{options.Device} offers no uncompressed NV12, P010 or YUYV mode (it offers {(offered.Length == 0 ? "nothing" : offered)}); " +
                    "compressed formats such as MJPEG need a decode step and add latency, so they are never used");
            }

            var mode = CaptureFormats.Choose(description.Modes, options.Request);
            Format = CaptureNegotiation.Apply(_device, mode);
            Color = CaptureColorMapping.Resolve(Format.Colorspace, Format.XferFunc, Format.YcbcrEnc, Format.Quantization, Format.Height, Format.BitDepth, options.Color);
            (_hdrColor, _sdrColor) = CaptureColorMapping.ResolveBoth(Format.Colorspace, Format.XferFunc, Format.YcbcrEnc, Format.Quantization, Format.Height, Format.BitDepth, options.Color);
            _isPq = Color.FrameColor.Transfer == TransferFunction.Pq;
            _hdrNow = _isPq ? 1 : 0;
            _following = options.Color.Hdr == HdrMode.Follow && Format.Format == CapturePixelFormat.P010 ? 1 : 0;
            _depth = new SampleDepthFollower(_isPq);
            InputDescription = description.Input;

            context.MakeCurrent();
            _ownsCopy = options.CopyStream is null;
            _ownsCompute = options.ComputeStream is null;
            _ownsKernels = options.Kernels is null;
            CopyStream = options.CopyStream ?? CudaStream.Create(context);
            ComputeStream = options.ComputeStream ?? CudaStream.Create(context);
            _kernels = options.Kernels ?? ImageKernels.Load(context);

            _buffers = CaptureBuffers.Create(_device, context, options.Buffers, Format.FrameBytes, options.BufferMode);
            for (var i = 0; i < StagingBuffers; i++)
            {
                _staging[i] = CudaDeviceBuffer.Allocate(context, Format.FrameBytes);
                _convertDone[i] = CudaEvent.Create(context);
            }

            _grid = new CadenceGrid(Format.Width, Format.Height, Format.Sampling);
            for (var i = 0; i < 2; i++)
            {
                _grids[i] = CudaDeviceBuffer.Allocate(context, _grid.Bytes);
                CopyStream.MemsetAsync(_grids[i].Address, 0, _grid.Bytes);
            }

            _metricDevice = CudaDeviceBuffer.Allocate(context, 16);
            _metricHost = CudaPinnedBuffer.Allocate(context, 16, CudaHostAllocOptions.Portable, alignment: 64);
            _metricReady = CudaEvent.Create(context);
            _uploadSpans = CudaSpanTimerRing.Create(context, PendingTimings);
            _cadenceSpans = CudaSpanTimerRing.Create(context, PendingTimings);
            _convertSpans = CudaSpanTimerRing.Create(context, PendingTimings);
            _conversion = YuvConversion.Create(Color.Matrix, Color.Range, Format.BitDepth, Color.Siting, Format.OutputFormat);
            _hdrConversion = YuvConversion.Create(_hdrColor.Matrix, _hdrColor.Range, Format.BitDepth, _hdrColor.Siting, Format.OutputFormat);
            _sdrConversion = YuvConversion.Create(_sdrColor.Matrix, _sdrColor.Range, Format.BitDepth, _sdrColor.Siting, Format.OutputFormat);
            History = new FrameHistory(context, Format.Width, Format.Height, Format.OutputFormat, options.QueueDepth + options.HistoryDepth);
            var ring = 2;
            while (ring < History.Capacity)
            {
                ring <<= 1;
            }

            _queue = new SpscRing<SourceFrame>(ring);
            _tracker = new CadenceTracker(ReferenceEquals(options.CadenceOptions, CaptureSourceOptions.CaptureCadence)
                ? CaptureSourceOptions.CadenceFor(Format)
                : options.CadenceOptions);

            // Black: the brightest 4x4 cell no brighter than the black level plus two 8-bit codes (limited range: 16 or 64).
            var scale = 1L << (Format.BitDepth - 8);
            var black = Color.Range == ColorRange.Limited ? 16 * scale : 0;
            _blackCellSum = (black + (2 * scale)) * CadenceGrid.CellSize * CadenceGrid.CellSize;

            _log = new CaptureFrameRecord[options.FrameLogCapacity];
            _sourceChangeEvents = _device.TrySubscribeSourceChange();
            _eventFd = LibC.EventFd(0, LibC.EfdCloExec | LibC.EfdNonBlock);
            if (_eventFd < 0)
            {
                throw new CaptureUnavailableException($"eventfd failed: {LibC.ErrorName(Marshal.GetLastPInvokeError())}");
            }

            CopyStream.Synchronize();
            Info = new FrameSourceInfo
            {
                Kind = FrameSourceKind.CaptureCard,
                Width = Format.Width,
                Height = Format.Height,
                Format = Format.OutputFormat,
                Color = Color.FrameColor,
                NominalRate = Format.Rate,
            };
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public FrameSourceInfo Info { get; }

    public V4l2DeviceInfo DeviceInfo { get; } = null!;

    public NegotiatedFormat Format { get; } = null!;

    /// <summary>The colour the capture started in (see <see cref="CurrentColor"/> for the one in force now).</summary>
    public CaptureColor Color { get; } = null!;

    /// <summary>The colour in force now: HDR10 or SDR, as followed or chosen (any thread; changes between frames).</summary>
    public CaptureColor CurrentColor => IsHdrNow ? _hdrColor : _sdrColor;

    /// <summary>Whether the frames are HDR10 (PQ) now.</summary>
    public bool IsHdrNow => Volatile.Read(ref _hdrNow) != 0;

    /// <summary>
    /// Whether the capture follows the signal's sample depth (<see cref="HdrMode.Follow"/> on a P010 capture, until a
    /// manual choice; <see cref="RequestColor"/> with <see cref="CaptureColorRequest.Follow"/> resumes it).
    /// </summary>
    public bool IsFollowing => Volatile.Read(ref _following) != 0;

    /// <summary>Changes between SDR and HDR10 while capturing (followed or by hand).</summary>
    public long ColorSwitches => Volatile.Read(ref _colorSwitches);

    /// <summary>The changes so far (the first 64), oldest first. Read after the capture has stopped, or accept a torn tail.</summary>
    public ReadOnlySpan<ColorSwitch> SwitchLog => _switches.AsSpan(0, Math.Min(Volatile.Read(ref _switchCount), MaxSwitchLog));

    /// <summary>Frames judged 10-bit, 8-bit and inconclusive by <see cref="SampleDepthProbe"/> (P010 captures).</summary>
    public (long TenBit, long EightBit, long Inconclusive) DepthCounts => (_depth.TenBitFrames, _depth.EightBitFrames, _depth.InconclusiveFrames);

    /// <summary>The driver's timestamp of the first frame processed (0 before), ns on CLOCK_MONOTONIC.</summary>
    public long FirstTimestampNs => Volatile.Read(ref _firstTimestampNs);

    /// <summary>The last frame's sample-depth reading (P010 captures; dequeue thread's, read anywhere for display).</summary>
    public SampleDepthReading LastDepthReading => _lastDepth;

    /// <summary>
    /// Asks for a colour change (any thread, allocation-free): applied by the dequeue thread before the next frame it
    /// converts, which is then marked a discontinuity (never interpolated with the frame before) and carries the new
    /// colour, so the presenter switches its output with it. A manual choice stops following.
    /// </summary>
    public void RequestColor(CaptureColorRequest request) => Volatile.Write(ref _colorRequest, (int)request);

    /// <summary>The device's current input and its status at open, when the driver reports them.</summary>
    public string? InputDescription { get; }

    public CaptureBufferMode BufferMode => _buffers.Mode;

    /// <summary>Why the buffer modes tried before the one in use were refused.</summary>
    public IReadOnlyList<string> RefusedBufferModes => _buffers.Refused;

    public int BufferCount => _buffers.Count;

    /// <summary>Whether the driver sends source-change events (a format change is then detected and ends the capture).</summary>
    public bool SourceChangeEvents => _sourceChangeEvents;

    public FrameHistory History { get; } = null!;

    /// <summary>Upload and cadence metric.</summary>
    public CudaStream CopyStream { get; } = null!;

    /// <summary>The conversion into the history; each frame's Ready event.</summary>
    public CudaStream ComputeStream { get; } = null!;

    public CudaContext Context => _context;

    /// <summary>The dequeue thread's scheduling (what it asked for and got), once started.</summary>
    public string? ThreadReport { get; private set; }

    /// <summary>Why the capture ended (a <see cref="CaptureEndedException"/>), if it did.</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    public CaptureSignalState State => (CaptureSignalState)Volatile.Read(ref _state);

    /// <summary>True when <see cref="Dispose"/> could not free the GPU side (the source was abandoned with GPU work stuck).</summary>
    public bool Leaked => _abandoned;

    public CaptureStatistics Statistics => new()
    {
        Dequeued = Volatile.Read(ref _dequeued),
        SkippedStale = Volatile.Read(ref _skippedStale),
        DriverDropped = Volatile.Read(ref _driverDropped),
        ErrorFrames = Volatile.Read(ref _errorFrames),
        ShortFrames = Volatile.Read(ref _shortFrames),
        Captured = Volatile.Read(ref _captured),
        Unique = Volatile.Read(ref _unique),
        Repeats = Volatile.Read(ref _repeats),
        DroppedBehind = Volatile.Read(ref _droppedBehind),
        Published = Volatile.Read(ref _published),
        Primed = Volatile.Read(ref _primed),
        BlackFrames = Volatile.Read(ref _blackFrames),
        NoSignalEpisodes = Volatile.Read(ref _noSignalEpisodes),
        Holds = Volatile.Read(ref _holds),
        TimestampRepairs = Volatile.Read(ref _timestampRepairs),
        NonMonotonicTimestamps = Volatile.Read(ref _nonMonotonic),
        DetectedFps = Volatile.Read(ref _detectedIntervalNs) is var i and > 0 ? Nanoseconds.PerSecond / (double)i : 0,
        DequeueThreadAllocatedBytes = Volatile.Read(ref _allocated),
        DroppedTimings = Volatile.Read(ref _droppedTimings),
    };

    /// <summary>Timestamp intervals between consecutive dequeued buffers after the first 30 of each start, ns (the signal's
    /// jitter).</summary>
    public CaptureDistribution Intervals { get; } = new(1_000, 200_000_000);

    /// <summary>Dequeue time minus the driver's timestamp, ns (USB delivery for a start-of-frame timestamp).</summary>
    public CaptureDistribution DequeueLag { get; } = new(10_000, 500_000_000);

    /// <summary>Capture timestamp to the frame published (upload done, conversion enqueued), ns.</summary>
    public CaptureDistribution CaptureToAvailable { get; } = new(10_000, 500_000_000);

    /// <summary>Dequeue to the upload and metric done (the dequeue thread's wait), ns.</summary>
    public CaptureDistribution UploadWait { get; } = new(1_000, 100_000_000);

    /// <summary>GPU time of the host-to-device copy, ns.</summary>
    public CaptureDistribution UploadGpu { get; } = new(1_000, 100_000_000);

    /// <summary>GPU time of the cadence metric, ns.</summary>
    public CaptureDistribution CadenceGpu { get; } = new(1_000, 100_000_000);

    /// <summary>GPU time of the conversion, ns.</summary>
    public CaptureDistribution ConvertGpu { get; } = new(1_000, 100_000_000);

    /// <summary>The cadence metric of frames judged new, in millionths of full scale.</summary>
    public CaptureDistribution UniqueMetric { get; } = new(10, 1_000_000);

    /// <summary>The cadence metric of frames judged repeats, in millionths of full scale.</summary>
    public CaptureDistribution RepeatMetric { get; } = new(10, 1_000_000);

    /// <summary>Intervals between unique frames, ns (the game's frame times).</summary>
    public CaptureDistribution UniqueIntervals { get; } = new(10_000, 2_000_000_000);

    /// <summary>The frame log (<see cref="CaptureSourceOptions.FrameLogCapacity"/>): every dequeued buffer, in order, until full.
    /// Read it once the source has stopped.</summary>
    public ReadOnlySpan<CaptureFrameRecord> FrameLog => _log.AsSpan(0, Volatile.Read(ref _logCount));

    /// <summary>The cadence tracker (read it once the source has stopped).</summary>
    public CadenceTracker Cadence => _tracker;

    /// <summary>Test hook, dequeue thread: once per processed frame, inside the allocation-measured part.</summary>
    internal Action? PerFrameHook { get; set; }

    /// <summary>Test hook, dequeue thread: before each poll (a slow dequeue thread lets buffers pile up).</summary>
    internal Action? BeforePollHook { get; set; }

    /// <summary>Test hook, dequeue thread: each uploaded frame's timestamp and host bytes (pointer, length) before its buffer
    /// goes back to the driver.</summary>
    internal Action<long, nint, long>? RawFrameHook { get; set; }

    /// <summary>Test hook, dequeue thread: the metric of each judged frame and whether it was judged new.</summary>
    internal Action<long, double, bool>? MetricHook { get; set; }

    /// <summary>One line: the device and what it captures.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{DeviceInfo.Card} ({_options.Device}, {DeviceInfo.Driver}): {Format.Describe()}; {CaptureBuffers.Name(BufferMode)} x{BufferCount}");

    /// <summary>Queues the buffers, starts streaming and the dequeue thread.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is { IsAlive: true })
        {
            return;
        }

        if (Failure is not null)
        {
            throw new InvalidOperationException("The capture has ended; it cannot be started again.", Failure);
        }

        _stop = false;
        DrainEventFd();
        _buffers.QueueAll();
        try
        {
            _device.StreamOn();
        }
        catch (V4l2Exception e) when (e.Errno is LibC.Ebusy or LibC.Enospc)
        {
            _ = _device.StreamOff();
            throw new CaptureUnavailableException(e.Errno == LibC.Enospc
                ? $"{_options.Device} cannot stream: not enough USB bandwidth for {Format.Describe()} (another device on the bus?)"
                : $"{_options.Device} is busy", e);
        }

        _streaming = true;
        _haveSequence = false;
        _lastTimestampNs = long.MinValue;
        _buffersSinceStart = 0;
        _lastFrameAtNs = Monotonic.NowNs;
        Volatile.Write(ref _state, (int)CaptureSignalState.Starting);
        _thread = DedicatedThread.Start("fmp-capture", _options.DequeueThread, Run);
        ThreadReport = _thread.Report.Summary;
    }

    /// <summary>
    /// Stops the dequeue thread (it never blocks for long: every wait polls the stop flag) and streaming; frames already
    /// delivered stay valid until released or disposed. Bounded; never waits for the GPU.
    /// </summary>
    public void Stop()
    {
        var thread = _thread;
        if (thread is not null)
        {
            _stop = true;
            Wake();
            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The capture thread did not stop within 5 s.");
            }

            _thread = null;
        }

        if (_streaming)
        {
            _streaming = false;
            _ = _device.StreamOff();
        }

        if (State != CaptureSignalState.Ended)
        {
            Volatile.Write(ref _state, (int)CaptureSignalState.Idle);
        }
    }

    /// <summary>
    /// Releases the device without touching the GPU: stops the thread (bounded), stops streaming, gives the buffers back to
    /// the driver and closes the device, so another program can use it at once. For a teardown where GPU work did not
    /// finish (nothing that waits for it may be called); the CUDA side stays allocated (<see cref="Leaked"/>) and is
    /// reclaimed when the process exits.
    /// </summary>
    public void Abandon()
    {
        _abandoned = true;
        _stop = true;
        Wake();
        if (_thread is { } thread && !thread.Join(TimeSpan.FromSeconds(2)))
        {
            // The thread may still be inside an ioctl on the descriptor: closing it could hand its number to another open
            // while that call returns. Leave the device open (the process exits after an abandoned teardown).
            return;
        }

        _thread = null;
        _streaming = false;
        if (_device.IsOpen)
        {
            _ = _device.StreamOff();
            _buffers?.Release(cudaToo: false);
            _device.Dispose();
        }
    }

    /// <summary>Whether the device is closed (after <see cref="Dispose"/> or an <see cref="Abandon"/> whose thread stopped).</summary>
    public bool DeviceReleased => !_device.IsOpen;

    /// <summary>The next unique frame, if one is ready. Consumer thread; never blocks, never allocates.</summary>
    public bool TryRead(out SourceFrame frame)
    {
        if (_queue.TryDequeue(out frame))
        {
            History.MarkRead(frame.FrameId);
            return true;
        }

        return false;
    }

    /// <summary>Gives a frame back with its last reader's GPU point (<see cref="IFrameSource.Release"/>). Consumer thread.</summary>
    public void Release(long frameId, in GpuSignal lastReader) => History.Release(frameId, lastReader);

    /// <summary>
    /// Stops capturing, closes the device and frees everything (synchronises the context: a drained point, never the pacing
    /// thread). After <see cref="Abandon"/> only the managed side is let go.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_abandoned)
        {
            return;
        }

        try
        {
            Stop();
        }
        catch (TimeoutException)
        {
            Abandon();
            return;
        }

        // The last upload from a capture buffer must be done before the buffer is given back and freed.
        if (!_context.IsDisposed)
        {
            _context.EnsureCurrent();
            if (CopyStream is { IsDisposed: false })
            {
                CopyStream.Synchronize();
            }

            if (ComputeStream is { IsDisposed: false })
            {
                ComputeStream.Synchronize();
            }
        }

        _buffers?.Release(cudaToo: !_context.IsDisposed);
        _device?.Dispose();
        if (_eventFd >= 0)
        {
            _ = LibC.Close(_eventFd);
        }

        History?.Dispose();
        foreach (var b in _staging)
        {
            b?.Dispose();
        }

        foreach (var e in _convertDone)
        {
            e?.Dispose();
        }

        foreach (var g in _grids)
        {
            g?.Dispose();
        }

        _metricDevice?.Dispose();
        _metricHost?.Dispose();
        _metricReady?.Dispose();
        _uploadSpans?.Dispose();
        _cadenceSpans?.Dispose();
        _convertSpans?.Dispose();
        if (_ownsKernels)
        {
            _kernels?.Dispose();
        }

        if (_ownsCopy)
        {
            CopyStream?.Dispose();
        }

        if (_ownsCompute)
        {
            ComputeStream?.Dispose();
        }
    }

    private void Wake()
    {
        if (_eventFd < 0)
        {
            return;
        }

        ulong one = 1;
        _ = LibC.Write(_eventFd, &one, sizeof(ulong));
    }

    private void DrainEventFd()
    {
        ulong value;
        _ = LibC.Read(_eventFd, &value, sizeof(ulong));
    }

    private void Run()
    {
        try
        {
            _context.MakeCurrent();
            PreRoll();
            var fds = stackalloc PollFd[2];
            var allocationBase = -1L;
            while (!_stop)
            {
                BeforePollHook?.Invoke();
                fds[0] = new PollFd { Fd = _device.Fd, Events = (short)(LibC.PollIn | LibC.PollPri) };
                fds[1] = new PollFd { Fd = _eventFd, Events = LibC.PollIn };
                var ready = LibC.Poll(fds, 2, (int)PollTimeoutMs);
                var now = Monotonic.NowNs;
                if (ready < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == LibC.Eintr)
                    {
                        continue;
                    }

                    End(CaptureEndReason.Failed, $"poll failed: {LibC.ErrorName(errno)}");
                    return;
                }

                CollectTimings();
                if (_stop || (fds[1].Revents & LibC.PollIn) != 0)
                {
                    break;
                }

                if (ready == 0)
                {
                    CheckSignal(now);
                    continue;
                }

                var revents = fds[0].Revents;
                if ((revents & LibC.PollPri) != 0 && !DrainEvents())
                {
                    return;
                }

                if ((revents & (LibC.PollHup | LibC.PollNval)) != 0)
                {
                    End(CaptureEndReason.DeviceLost, $"{_options.Device} went away (unplugged or reset)");
                    return;
                }

                if ((revents & LibC.PollIn) != 0)
                {
                    if (!DequeueNewest(now))
                    {
                        return;
                    }

                    // Managed allocations of this thread from one frame to the next, after its first frames.
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    if (allocationBase >= 0)
                    {
                        Volatile.Write(ref _allocated, _allocated + (allocated - allocationBase));
                    }

                    allocationBase = _captured >= AllocationWarmupFrames ? allocated : -1;
                    continue;
                }

                if ((revents & LibC.PollErr) != 0)
                {
                    // vb2 reports POLLERR when streaming stopped or the queue is in an error state: the next dequeue says which.
                    var probe = new V4l2Buffer { Type = V4l2Defs.BufTypeVideoCapture, Memory = _buffers.Memory };
                    var errno = _device.DequeueBuffer(ref probe);
                    if (errno == 0)
                    {
                        _ = _buffers.Queue((int)probe.Index);
                        continue;
                    }

                    End(errno == LibC.Enodev ? CaptureEndReason.DeviceLost : CaptureEndReason.StreamError,
                        $"{_options.Device} stopped streaming: {LibC.ErrorName(errno)}");
                    return;
                }

                CheckSignal(now);
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _failure, e is CaptureEndedException ? e : new CaptureEndedException(CaptureEndReason.Failed, $"the capture thread failed: {e.GetType().Name}: {e.Message}", e));
            Volatile.Write(ref _state, (int)CaptureSignalState.Ended);
        }
    }

    /// <summary>
    /// First uses on this thread before any frame: a static initialiser run here later (the sleep in <see cref="WaitFor"/>
    /// is first needed only when an upload outlasts the spin, frames into a run) would allocate inside the measured part.
    /// </summary>
    private void PreRoll()
    {
        _ = Monotonic.NowNs;
        _ = GC.GetAllocatedBytesForCurrentThread();
        DeadlineWaiter.Sleep.WaitUntil(Monotonic.NowNs + 1_000);
        Thread.SpinWait(1);
        _ = _metricReady.IsComplete;
        _ = _tracker.EstimatedFrameRateHz;
        _ = _tracker.EstimatedIntervalNs;
        _ = History.FreeSlots;
        _ = _queue.Count;
    }

    private void End(CaptureEndReason reason, string message)
    {
        Volatile.Write(ref _failure, new CaptureEndedException(reason, message));
        Volatile.Write(ref _state, (int)CaptureSignalState.Ended);
    }

    /// <summary>No frame for a while: the signal is reported gone; the next frame is a discontinuity.</summary>
    private void CheckSignal(long now)
    {
        if (now - _lastFrameAtNs > _options.NoSignalAfterNs && State is not (CaptureSignalState.NoSignal or CaptureSignalState.Ended))
        {
            Volatile.Write(ref _state, (int)CaptureSignalState.NoSignal);
            Volatile.Write(ref _noSignalEpisodes, _noSignalEpisodes + 1);
            _discontinuityNext = _published > 0;
        }
    }

    /// <summary>Source-change and other events: false when the capture must end.</summary>
    private bool DrainEvents()
    {
        var e = default(V4l2Event);
        while (_device.DequeueEvent(ref e) == 0)
        {
            if (e.Type == V4l2Defs.EventSourceChange)
            {
                End(CaptureEndReason.FormatChanged, (e.SourceChanges & V4l2Defs.EventSrcChResolution) != 0
                    ? $"the input's resolution changed on {_options.Device}: the capture stops (re-negotiating needs a restart)"
                    : $"the input changed on {_options.Device} (source change 0x{e.SourceChanges:x}): the capture stops");
                return false;
            }
        }

        return true;
    }

    /// <summary>Dequeues what is ready, keeps the newest, requeues the rest unseen, and processes the newest.</summary>
    private bool DequeueNewest(long now)
    {
        var b = new V4l2Buffer { Type = V4l2Defs.BufTypeVideoCapture, Memory = _buffers.Memory };
        var errno = _device.DequeueBuffer(ref b);
        if (errno == LibC.Eagain)
        {
            return true;
        }

        if (errno != 0)
        {
            End(errno == LibC.Enodev ? CaptureEndReason.DeviceLost : CaptureEndReason.StreamError, $"VIDIOC_DQBUF on {_options.Device} failed: {LibC.ErrorName(errno)}");
            return false;
        }

        Volatile.Write(ref _dequeued, _dequeued + 1);
        while (true)
        {
            var next = new V4l2Buffer { Type = V4l2Defs.BufTypeVideoCapture, Memory = _buffers.Memory };
            errno = _device.DequeueBuffer(ref next);
            if (errno == LibC.Eagain)
            {
                break;
            }

            if (errno != 0)
            {
                _ = _buffers.Queue((int)b.Index);
                End(errno == LibC.Enodev ? CaptureEndReason.DeviceLost : CaptureEndReason.StreamError, $"VIDIOC_DQBUF on {_options.Device} failed: {LibC.ErrorName(errno)}");
                return false;
            }

            // An older buffer: requeued unseen; only its sequence number and timestamp are accounted.
            Volatile.Write(ref _dequeued, _dequeued + 1);
            Volatile.Write(ref _skippedStale, _skippedStale + 1);
            Account(in b);
            Log(in b, now, 0, -1, 0, CaptureFrameEvents.Stale);
            if (!Requeue((int)b.Index))
            {
                return false;
            }

            b = next;
        }

        Account(in b);
        return Process(in b, now);
    }

    /// <summary>Sequence gaps and timestamp intervals of every dequeued buffer (error-flagged and stale ones included).</summary>
    private void Account(in V4l2Buffer b)
    {
        if ((b.Flags & V4l2Defs.BufFlagTimestampMask) == V4l2Defs.BufFlagTimestampMonotonic)
        {
            var timestamp = (b.TimestampSeconds * Nanoseconds.PerSecond) + (b.TimestampMicroseconds * Nanoseconds.PerMicrosecond);
            if (_lastTimestampNs != long.MinValue && ++_buffersSinceStart > IntervalWarmupBuffers)
            {
                Intervals.Add(timestamp - _lastTimestampNs);
            }

            _lastTimestampNs = timestamp;
        }

        if (_haveSequence && b.Sequence != _lastSequence + 1 && b.Sequence > _lastSequence)
        {
            Volatile.Write(ref _driverDropped, _driverDropped + (b.Sequence - _lastSequence - 1));
        }

        _lastSequence = b.Sequence;
        _haveSequence = true;
    }

    private bool Requeue(int index)
    {
        var errno = _buffers.Queue(index);
        if (errno == 0)
        {
            return true;
        }

        End(errno == LibC.Enodev ? CaptureEndReason.DeviceLost : CaptureEndReason.StreamError, $"VIDIOC_QBUF on {_options.Device} failed: {LibC.ErrorName(errno)}");
        return false;
    }

    private bool Process(in V4l2Buffer b, long dequeuedAt)
    {
        var index = (int)b.Index;
        if ((b.Flags & V4l2Defs.BufFlagError) != 0)
        {
            Volatile.Write(ref _errorFrames, _errorFrames + 1);
            Log(in b, dequeuedAt, 0, -1, 0, CaptureFrameEvents.Error);
            return Requeue(index);
        }

        if (b.BytesUsed != 0 && b.BytesUsed < Format.FrameBytes)
        {
            Volatile.Write(ref _shortFrames, _shortFrames + 1);
            Log(in b, dequeuedAt, 0, -1, 0, CaptureFrameEvents.Error);
            return Requeue(index);
        }

        long timestamp;
        if ((b.Flags & V4l2Defs.BufFlagTimestampMask) == V4l2Defs.BufFlagTimestampMonotonic)
        {
            timestamp = (b.TimestampSeconds * Nanoseconds.PerSecond) + (b.TimestampMicroseconds * Nanoseconds.PerMicrosecond);
        }
        else
        {
            timestamp = dequeuedAt;
            Volatile.Write(ref _nonMonotonic, _nonMonotonic + 1);
        }

        DequeueLag.Add(dequeuedAt - timestamp);
        _lastFrameAtNs = dequeuedAt;
        if (State is CaptureSignalState.Starting or CaptureSignalState.NoSignal)
        {
            Volatile.Write(ref _state, (int)CaptureSignalState.Streaming);
        }

        // Upload and judge, on the copy stream; the staging buffer's last conversion must be done first.
        var s = _nextStaging;
        _nextStaging = (s + 1) % StagingBuffers;
        var staging = _staging[s];
        var copy = CopyStream;
        copy.Wait(_convertDone[s]);
        _uploadSpans.Begin(copy);
        copy.CopyFromHostAsync(staging.Address, _buffers.Host(index), Format.FrameBytes);
        _uploadSpans.End(copy);
        var judged = _options.Cadence;
        if (judged)
        {
            _cadenceSpans.Begin(copy);
            _kernels.EnqueueCadenceMetric(copy, staging.Address, Format.BytesPerLine, _grid, _grids[_refGrid].Address, _grids[1 - _refGrid].Address, _metricDevice.Address);
            _cadenceSpans.End(copy);
            copy.CopyToHostAsync(_metricHost.HostPointer, _metricDevice.Address, 16);
        }

        _metricReady.Record(copy);
        AddPending(new PendingTiming(Convert: false, Cadence: judged));

        // While the GPU uploads: what the samples say about the signal's depth (P010), and any colour change asked for.
        if (Format.Format == CapturePixelFormat.P010)
        {
            _lastDepth = SampleDepthProbe.Judge((byte*)_buffers.Host(index), Format.BytesPerLine, Format.Width, Format.Height, _hdrColor.Range == ColorRange.Limited ? 64 : 0);
        }

        if (_firstTimestampNs == 0)
        {
            Volatile.Write(ref _firstTimestampNs, timestamp);
        }

        UpdateColor(timestamp);
        if (!WaitFor(_metricReady))
        {
            return false;
        }

        var judgedAt = Monotonic.NowNs;
        UploadWait.Add(judgedAt - dequeuedAt);

        RawFrameHook?.Invoke(timestamp, (nint)_buffers.Host(index), Format.FrameBytes);

        // The upload is done: the driver may write this buffer again.
        if (!Requeue(index))
        {
            return false;
        }

        Volatile.Write(ref _captured, _captured + 1);
        var content = timestamp;
        var metric = -1.0;
        var cellMean = 0.0;
        var flags = CaptureFrameEvents.None;
        if (judged)
        {
            var words = (ulong*)_metricHost.HostPointer;
            metric = _grid.Normalise(words[0]);
            cellMean = CadenceGrid.CellMean(words[1]);
            if ((long)words[1] <= _blackCellSum)
            {
                flags |= CaptureFrameEvents.Black;
                Volatile.Write(ref _blackFrames, _blackFrames + 1);
                Volatile.Write(ref _state, (int)CaptureSignalState.Black);
            }
            else if (State == CaptureSignalState.Black)
            {
                Volatile.Write(ref _state, (int)CaptureSignalState.Streaming);
            }

            var verdict = _tracker.Process(timestamp, metric, _isPq);
            MetricHook?.Invoke(timestamp, metric, verdict.IsUnique);
            Volatile.Write(ref _detectedIntervalNs, _tracker.EstimatedIntervalNs);
            flags |= verdict.Ambiguous ? CaptureFrameEvents.Ambiguous : CaptureFrameEvents.None;
            if (!verdict.IsUnique)
            {
                RepeatMetric.Add((long)Math.Round(metric * 1e6));
                Volatile.Write(ref _repeats, _repeats + 1);

                // A still picture at the start: publish it anyway (the reference stays the last unique frame).
                var prime = _published < _options.PrimeFrames
                    && (_lastPublishedAtNs == long.MinValue || dequeuedAt - _lastPublishedAtNs >= _options.PrimeIntervalNs);
                if (!prime)
                {
                    Log(in b, dequeuedAt, judgedAt, metric, cellMean, flags);
                    PerFrameHook?.Invoke();
                    return true;
                }

                flags |= CaptureFrameEvents.Primed;
                Volatile.Write(ref _primed, _primed + 1);
            }
            else
            {
                UniqueMetric.Add((long)Math.Round(metric * 1e6));
                content = verdict.ContentTimeNs;
            }
        }

        var unique = (flags & CaptureFrameEvents.Primed) == 0;
        if (unique)
        {
            Volatile.Write(ref _unique, _unique + 1);
            flags |= CaptureFrameEvents.Unique;
        }

        if (content <= _lastContentNs)
        {
            content = _lastContentNs + 1;
            Volatile.Write(ref _timestampRepairs, _timestampRepairs + 1);
        }

        // Newest wins: a frame the consumer has no room for is dropped here; nothing waits for the consumer.
        // The cadence reference stays the last *published* picture until this frame has room: a dropped new picture that
        // then stays still is judged new again at the next capture (and published then), instead of every later capture
        // repeating a picture the consumer never got (the review's finding: the display held an older picture for good).
        var slot = _queue.Count >= _options.QueueDepth ? -1 : History.TryBeginWrite(ComputeStream);
        if (slot < 0)
        {
            Volatile.Write(ref _droppedBehind, _droppedBehind + 1);
            Log(in b, dequeuedAt, judgedAt, metric, cellMean, flags | CaptureFrameEvents.DroppedBehind);
            PerFrameHook?.Invoke();
            return true;
        }

        if (unique)
        {
            if (judged)
            {
                _refGrid = 1 - _refGrid; // this frame's grid (written by its metric kernel, complete) is the new reference
            }

            if (_lastUniqueNs != long.MinValue)
            {
                var gap = content - _lastUniqueNs;
                UniqueIntervals.Add(gap);
                var expected = _tracker.EstimatedIntervalNs > 0 ? _tracker.EstimatedIntervalNs : (long)Format.Rate.PeriodNs;
                if (expected > 0 && gap > 2 * expected)
                {
                    Volatile.Write(ref _holds, _holds + 1);
                }
            }

            _lastUniqueNs = content;
        }

        var compute = ComputeStream;
        _convertSpans.Begin(compute);
        if (Format.Format == CapturePixelFormat.Yuyv)
        {
            _kernels.EnqueueYuyvToRgb(compute, staging.Address, Format.BytesPerLine, History.Target(slot), _conversion);
        }
        else
        {
            var planes = new YuvPlanes
            {
                Luma = staging.Address,
                LumaPitch = Format.BytesPerLine,
                Chroma = staging.Address + ((ulong)Format.BytesPerLine * (ulong)Format.Height),
                ChromaPitch = Format.BytesPerLine,
                Width = Format.Width,
                Height = Format.Height,
                Format = Format.Format == CapturePixelFormat.P010 ? YuvSampleFormat.P010 : YuvSampleFormat.Nv12,
            };
            _kernels.EnqueueYuvToRgb(compute, planes, History.Target(slot), _conversion);
        }

        _convertSpans.End(compute);
        var id = _nextFrameId++;
        var frame = History.CompleteWrite(slot, compute, id, content, _isPq ? _hdrColor.FrameColor : _sdrColor.FrameColor);
        _convertDone[s].Record(compute);
        AddPending(new PendingTiming(Convert: true, Cadence: false, Upload: false));
        var marks = (_discontinuityNext ? SourceFrameMarks.Discontinuity : SourceFrameMarks.None)
            | (_colorChangedNext ? SourceFrameMarks.FormatChanged : SourceFrameMarks.None);
        _discontinuityNext = false;
        _colorChangedNext = false;
        PerFrameHook?.Invoke();
        var arrival = Monotonic.NowNs;
        if (!_queue.TryEnqueue(new SourceFrame { FrameId = id, Frame = frame, ArrivalTimeNs = arrival, Marks = marks }))
        {
            // Cannot happen: the queue holds every history buffer.
            throw new InvalidOperationException("The capture queue is full.");
        }

        CaptureToAvailable.Add(arrival - content);
        Log(in b, dequeuedAt, judgedAt, metric, cellMean, flags | CaptureFrameEvents.Published);
        _lastContentNs = content;
        _lastPublishedAtNs = dequeuedAt;
        Volatile.Write(ref _published, _published + 1);
        return true;
    }

    /// <summary>
    /// Dequeue thread, before a frame is judged and converted: a manual request, else the sample-depth follower, may
    /// change the colour; the change applies to this frame (its conversion, its cadence thresholds) and marks the next
    /// published frame. Allocation-free.
    /// </summary>
    private void UpdateColor(long timestampNs)
    {
        var hdr = _isPq;
        var cause = ColorSwitchCause.Followed;
        var request = (CaptureColorRequest)Interlocked.Exchange(ref _colorRequest, (int)CaptureColorRequest.None);
        switch (request)
        {
            case CaptureColorRequest.Toggle:
                hdr = !hdr;
                cause = ColorSwitchCause.Manual;
                Volatile.Write(ref _following, 0);
                break;
            case CaptureColorRequest.Hdr:
            case CaptureColorRequest.Sdr:
                hdr = request == CaptureColorRequest.Hdr;
                cause = ColorSwitchCause.Manual;
                Volatile.Write(ref _following, 0);
                break;
            case CaptureColorRequest.Follow when Format.Format == CapturePixelFormat.P010:
                _depth.Reset(_isPq);
                Volatile.Write(ref _following, 1);
                break;
            default:
                break;
        }

        if (Format.Format == CapturePixelFormat.P010)
        {
            // Counted whether or not it is followed (the summary reports the signal's depth either way).
            if (_depth.Feed(_lastDepth.Depth) && Volatile.Read(ref _following) != 0 && request == CaptureColorRequest.None)
            {
                hdr = _depth.IsHdr;
            }
            else if (Volatile.Read(ref _following) == 0)
            {
                _depth.Reset(hdr);
            }
        }

        if (hdr == _isPq)
        {
            return;
        }

        _isPq = hdr;
        _conversion = hdr ? _hdrConversion : _sdrConversion;
        _depth.Reset(hdr);
        _discontinuityNext |= _published > 0;
        _colorChangedNext = true;
        Volatile.Write(ref _hdrNow, hdr ? 1 : 0);
        var n = _switchCount;
        if (n < MaxSwitchLog)
        {
            _switches[n] = new ColorSwitch(timestampNs, hdr, cause, _lastDepth);
        }

        Volatile.Write(ref _switchCount, n + 1);
        Volatile.Write(ref _colorSwitches, _colorSwitches + 1);
    }

    private void Log(in V4l2Buffer b, long dequeuedAt, long judgedAt, double metric, double cellMean, CaptureFrameEvents flags)
    {
        var n = _logCount;
        if (n >= _log.Length)
        {
            return;
        }

        _log[n] = new CaptureFrameRecord
        {
            Sequence = b.Sequence,
            TimestampNs = (b.TimestampSeconds * Nanoseconds.PerSecond) + (b.TimestampMicroseconds * Nanoseconds.PerMicrosecond),
            DequeuedNs = dequeuedAt,
            JudgedNs = judgedAt,
            Metric = metric,
            MaxCellMean = cellMean,
            EstimatedFps = _tracker.EstimatedFrameRateHz,
            Flags = flags,
        };
        Volatile.Write(ref _logCount, n + 1);
    }

    /// <summary>
    /// Waits for <paramref name="e"/> without a blocking CUDA call: polls it (allocation-free), sleeping a few tens of
    /// microseconds between looks, so <see cref="Stop"/> is honoured within that and GPU work that never finishes ends the
    /// capture after the stall timeout instead of holding the thread (and the device) for good.
    /// </summary>
    private bool WaitFor(CudaEvent e)
    {
        var start = Monotonic.NowNs;
        var spins = 0;
        while (!e.IsComplete)
        {
            if (_stop)
            {
                return false;
            }

            var now = Monotonic.NowNs;
            if (now - start > _options.StallTimeoutNs)
            {
                End(CaptureEndReason.GpuStalled, string.Create(CultureInfo.InvariantCulture,
                    $"an upload did not finish within {_options.StallTimeoutNs / 1e9:0.#} s (GPU work that never completes): the capture stops"));
                return false;
            }

            if (++spins < 8)
            {
                Thread.SpinWait(64);
                continue;
            }

            DeadlineWaiter.Sleep.WaitUntil(now + GpuPollSleepNs);
        }

        return true;
    }

    /// <summary>Queues a frame's GPU spans until they complete (read in the order recorded).</summary>
    private void AddPending(in PendingTiming entry)
    {
        CollectTimings();
        if (_timingLost || _pendingCount == PendingTimings || _uploadSpans.Dropped + _cadenceSpans.Dropped + _convertSpans.Dropped > 0)
        {
            _timingLost = true;
            Volatile.Write(ref _droppedTimings, _droppedTimings + 1);
            return;
        }

        _pending[(_pendingHead + _pendingCount) % PendingTimings] = entry;
        _pendingCount++;
    }

    private void CollectTimings()
    {
        while (_pendingCount > 0 && !_timingLost)
        {
            ref var head = ref _pending[_pendingHead];
            if (head.Upload)
            {
                if (!_uploadSpans.TryRead(out var ns))
                {
                    return;
                }

                UploadGpu.Add(ns);
                head = head with { Upload = false };
            }

            if (head.Cadence)
            {
                if (!_cadenceSpans.TryRead(out var ns))
                {
                    return;
                }

                CadenceGpu.Add(ns);
                head = head with { Cadence = false };
            }

            if (head.Convert)
            {
                if (!_convertSpans.TryRead(out var ns))
                {
                    return;
                }

                ConvertGpu.Add(ns);
                head = head with { Convert = false };
            }

            _pendingHead = (_pendingHead + 1) % PendingTimings;
            _pendingCount--;
        }
    }

    [SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Private bookkeeping.")]
    private readonly record struct PendingTiming(bool Convert, bool Cadence, bool Upload = true);
}
