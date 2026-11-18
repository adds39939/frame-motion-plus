using FrameMotionPlus.Core.Cadence;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Platform.Threading;

namespace FrameMotionPlus.Sources.V4l2;

/// <summary>How a <see cref="V4l2FrameSource"/> captures.</summary>
public sealed record CaptureSourceOptions
{
    /// <summary>
    /// The default: the thresholds are chosen per mode by <see cref="CadenceFor"/> (this instance is the marker; give
    /// another <see cref="CadenceOptions"/> to set them yourself).
    /// </summary>
    public static CadenceOptions CaptureCadence { get; } = new()
    {
        Sdr = new CadenceThresholds(0.0016, 0.0050),
        Pq = new CadenceThresholds(0.0004, 0.0050),
    };

    /// <summary>
    /// The cadence thresholds for a negotiated mode, from the Elgato 4K X with a PS5 (docs/benchmarks/phase5-capture.md,
    /// "Cadence thresholds"). The metric is the largest 32x32-pixel tile's mean absolute difference of 4x4-cell luma sums
    /// per sample, as a fraction of full scale. Low sits at about twice the largest true duplicate measured in that mode:
    /// P010 2160p30 duplicates 0.00011 to 0.00013 (Low 0.0003), P010 1440p60 at most 0.00021 (Low 0.0004), NV12 at most
    /// 0.00084 (Low 0.0016); High (0.0050) below every new picture's p1 measured (0.0085 to 0.0236). In between the cadence
    /// decides: new only when a new frame is due, which at a 30 Hz signal is every capture.
    /// </summary>
    public static CadenceOptions CadenceFor(NegotiatedFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var pqLow = format.Height >= 2160 ? 0.0003 : 0.0004;
        return new CadenceOptions
        {
            Sdr = new CadenceThresholds(0.0016, 0.0050),
            Pq = new CadenceThresholds(pqLow, 0.0050),
        };
    }

    /// <summary>The device node, e.g. /dev/video0.</summary>
    public required string Device { get; init; }

    public CaptureRequest Request { get; init; } = new();

    public CaptureColorOverrides Color { get; init; } = new();

    public CaptureBufferMode BufferMode { get; init; } = CaptureBufferMode.Auto;

    /// <summary>V4L2 buffers (the design doc's 3 to 4).</summary>
    public int Buffers { get; init; } = 4;

    /// <summary>Frames that may wait for the consumer; a newer one is dropped (counted) when it has not taken them.</summary>
    public int QueueDepth { get; init; } = 2;

    /// <summary>Frames the consumer may hold at once (the scheduler's start-up frames plus the pair in use).</summary>
    public int HistoryDepth { get; init; } = 10;

    /// <summary>Run the cadence detector (repeats of a picture are dropped). Off: every captured frame is unique.</summary>
    public bool Cadence { get; init; } = true;

    /// <summary>The cadence thresholds; the default (<see cref="CaptureCadence"/>) means "per mode" (<see cref="CadenceFor"/>).</summary>
    public CadenceOptions CadenceOptions { get; init; } = CaptureCadence;

    /// <summary>
    /// Start-up priming: until this many frames are published, a repeat is published as well when nothing has been for
    /// <see cref="PrimeIntervalNs"/>, so a consumer waiting for its start-up frames (the scheduler's, 8) starts on a still
    /// picture (a paused game, a menu) instead of waiting for motion. 0: off.
    /// </summary>
    public int PrimeFrames { get; init; } = 8;

    public long PrimeIntervalNs { get; init; } = 100 * Nanoseconds.PerMillisecond;

    /// <summary>No frame for this long: the signal is reported gone (the pipeline holds the last frame meanwhile).</summary>
    public long NoSignalAfterNs { get; init; } = Nanoseconds.PerSecond;

    /// <summary>How long the dequeue thread waits for an upload before the capture fails (GPU work that never finishes).</summary>
    public long StallTimeoutNs { get; init; } = 10 * Nanoseconds.PerSecond;

    /// <summary>The dequeue thread: real-time FIFO asked for (it degrades to a nice value, then to normal, and says so).</summary>
    public ThreadSchedulingRequest DequeueThread { get; init; } = new()
    {
        RealTime = RealTimePolicy.Fifo,
        RealTimePriority = 20,
        Nice = -10,
        TimerSlackNs = 1,
    };

    /// <summary>Dequeued buffers the frame log keeps (preallocated; 0: no log). Diagnostics: per-frame metric and timing.</summary>
    public int FrameLogCapacity { get; init; }

    /// <summary>The copy stream (upload, cadence metric); null: the source creates its own.</summary>
    public CudaStream? CopyStream { get; init; }

    /// <summary>The compute stream (the conversion into the history); null: the source creates its own.</summary>
    public CudaStream? ComputeStream { get; init; }

    /// <summary>Loaded kernels in the same context; null: the source loads its own.</summary>
    public ImageKernels? Kernels { get; init; }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrEmpty(Device);
        ArgumentNullException.ThrowIfNull(Request);
        ArgumentNullException.ThrowIfNull(Color);
        ArgumentOutOfRangeException.ThrowIfLessThan(Buffers, 2, nameof(Buffers));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Buffers, 16, nameof(Buffers));
        ArgumentOutOfRangeException.ThrowIfLessThan(QueueDepth, 1, nameof(QueueDepth));
        ArgumentOutOfRangeException.ThrowIfLessThan(HistoryDepth, 2, nameof(HistoryDepth));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(QueueDepth + HistoryDepth, 64, nameof(HistoryDepth));
        ArgumentOutOfRangeException.ThrowIfLessThan(NoSignalAfterNs, 100 * Nanoseconds.PerMillisecond, nameof(NoSignalAfterNs));
        ArgumentOutOfRangeException.ThrowIfLessThan(StallTimeoutNs, 100 * Nanoseconds.PerMillisecond, nameof(StallTimeoutNs));
        ArgumentOutOfRangeException.ThrowIfNegative(FrameLogCapacity, nameof(FrameLogCapacity));
        ArgumentOutOfRangeException.ThrowIfNegative(PrimeFrames, nameof(PrimeFrames));
        ArgumentOutOfRangeException.ThrowIfLessThan(PrimeIntervalNs, Nanoseconds.PerMillisecond, nameof(PrimeIntervalNs));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FrameLogCapacity, 10_000_000, nameof(FrameLogCapacity));
        ArgumentNullException.ThrowIfNull(CadenceOptions);
        CadenceOptions.Validate();
        DequeueThread.Validate();
    }
}
