using FrameMotionPlus.Core.Statistics;

namespace FrameMotionPlus.Sources.V4l2;

/// <summary>
/// A distribution of one per-frame quantity (ns, or metric micro-units): count, mean, standard deviation, extremes and
/// percentiles from a fixed-bin histogram. Preallocated; <see cref="Add"/> is allocation-free. One writer thread; read it
/// once the writer has stopped (values read while it runs are approximate).
/// </summary>
public sealed class CaptureDistribution
{
    private readonly Histogram _histogram;
    private double _sum;
    private double _sumSquares;

    public CaptureDistribution(long binWidth, long maxValue)
    {
        _histogram = new Histogram(binWidth, maxValue);
    }

    public long Count => _histogram.Count;

    public double Mean => Count == 0 ? 0 : _sum / Count;

    public double StdDev
    {
        get
        {
            if (Count < 2)
            {
                return 0;
            }

            var mean = Mean;
            return Math.Sqrt(Math.Max(0, (_sumSquares / Count) - (mean * mean)));
        }
    }

    public long Min => _histogram.Min;

    public long Max => _histogram.Max;

    public void Add(long value)
    {
        _histogram.Add(Math.Max(0, value));
        _sum += value;
        _sumSquares += (double)value * value;
    }

    public long Percentile(double fraction) => Count == 0 ? 0 : Math.Min(_histogram.Percentile(fraction), Max);

    public void Clear()
    {
        _histogram.Clear();
        _sum = 0;
        _sumSquares = 0;
    }
}

/// <summary>Why a capture stopped producing (its <see cref="CaptureEndedException.Reason"/>).</summary>
public enum CaptureEndReason
{
    None = 0,

    /// <summary>The device went away (unplugged; ENODEV, POLLHUP).</summary>
    DeviceLost = 1,

    /// <summary>The driver reported a streaming error (EIO, EPIPE, POLLERR).</summary>
    StreamError = 2,

    /// <summary>The input's resolution or format changed (a source-change event): re-negotiation needs a drained point.</summary>
    FormatChanged = 3,

    /// <summary>GPU work the capture waits for did not finish (stall timeout).</summary>
    GpuStalled = 4,

    /// <summary>Anything else (a CUDA error, a bug).</summary>
    Failed = 5,
}

/// <summary>The capture stopped producing frames; <see cref="Reason"/> says why. The message is one line.</summary>
public sealed class CaptureEndedException : Exception
{
    public CaptureEndedException()
    {
    }

    public CaptureEndedException(string message)
        : base(message)
    {
    }

    public CaptureEndedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CaptureEndedException(CaptureEndReason reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public CaptureEndReason Reason { get; }
}

/// <summary>The capture's signal as last seen by the dequeue thread.</summary>
public enum CaptureSignalState
{
    /// <summary>Not streaming yet.</summary>
    Idle = 0,

    /// <summary>Streaming, waiting for the first frame.</summary>
    Starting = 1,

    /// <summary>Frames arriving.</summary>
    Streaming = 2,

    /// <summary>Frames arriving, but black (no picture: a console asleep, an input with nothing on it).</summary>
    Black = 3,

    /// <summary>No frame for <see cref="CaptureSourceOptions.NoSignalAfterNs"/>: the signal is gone (or the device stalled).</summary>
    NoSignal = 4,

    /// <summary>The capture ended (<see cref="V4l2FrameSource.Failure"/> says why).</summary>
    Ended = 5,
}

/// <summary>Counters of a capture, a snapshot. Distributions are on <see cref="V4l2FrameSource"/>.</summary>
public readonly record struct CaptureStatistics
{
    /// <summary>Buffers dequeued from the driver (every one, stale ones included).</summary>
    public long Dequeued { get; init; }

    /// <summary>Buffers ready at once behind a newer one: requeued unseen (newest wins).</summary>
    public long SkippedStale { get; init; }

    /// <summary>Frames the driver itself dropped (gaps in its sequence numbers).</summary>
    public long DriverDropped { get; init; }

    /// <summary>Buffers flagged V4L2_BUF_FLAG_ERROR (incomplete or corrupt), skipped.</summary>
    public long ErrorFrames { get; init; }

    /// <summary>Buffers with fewer bytes than a frame, skipped.</summary>
    public long ShortFrames { get; init; }

    /// <summary>Frames uploaded and judged (the cadence detector saw them).</summary>
    public long Captured { get; init; }

    /// <summary>Captured frames that were new pictures.</summary>
    public long Unique { get; init; }

    /// <summary>Captured frames that repeated the last picture: dropped.</summary>
    public long Repeats { get; init; }

    /// <summary>Unique frames dropped because the pipeline had not taken the earlier ones (no history slot, queue full).</summary>
    public long DroppedBehind { get; init; }

    /// <summary>Frames handed to the consumer.</summary>
    public long Published { get; init; }

    /// <summary>Repeats published at the start so the pipeline starts on a still picture.</summary>
    public long Primed { get; init; }

    /// <summary>Frames whose brightest 4x4 cell was at the black level.</summary>
    public long BlackFrames { get; init; }

    /// <summary>Times no frame arrived for <see cref="CaptureSourceOptions.NoSignalAfterNs"/>.</summary>
    public long NoSignalEpisodes { get; init; }

    /// <summary>Gaps between unique frames longer than twice the detected interval (pauses, menus, loading: holds).</summary>
    public long Holds { get; init; }

    /// <summary>Timestamps that did not advance, moved 1 ns past the previous frame.</summary>
    public long TimestampRepairs { get; init; }

    /// <summary>Buffers whose timestamp was not CLOCK_MONOTONIC (the dequeue time is used instead).</summary>
    public long NonMonotonicTimestamps { get; init; }

    /// <summary>The game's frame rate the cadence detector estimates (0 until known).</summary>
    public double DetectedFps { get; init; }

    /// <summary>Managed bytes the dequeue thread allocated per frame, after its warm-up (must be 0).</summary>
    public long DequeueThreadAllocatedBytes { get; init; }

    /// <summary>Timing samples not taken because the GPU was a whole ring of spans behind.</summary>
    public long DroppedTimings { get; init; }
}

/// <summary>What happened to one dequeued buffer (the frame log: <see cref="CaptureSourceOptions.FrameLogCapacity"/>).</summary>
[Flags]
public enum CaptureFrameEvents
{
    None = 0,

    /// <summary>Requeued unseen: a newer buffer was ready.</summary>
    Stale = 1 << 0,

    /// <summary>Flagged V4L2_BUF_FLAG_ERROR, or short: skipped.</summary>
    Error = 1 << 1,

    /// <summary>Judged a new picture.</summary>
    Unique = 1 << 2,

    /// <summary>Its metric fell between the thresholds (the cadence decided).</summary>
    Ambiguous = 1 << 3,

    /// <summary>Black (no picture).</summary>
    Black = 1 << 4,

    /// <summary>Handed to the consumer.</summary>
    Published = 1 << 5,

    /// <summary>Unique but dropped: the pipeline had not taken the earlier frames.</summary>
    DroppedBehind = 1 << 6,

    /// <summary>A repeat published to start the pipeline on a still picture (<see cref="CaptureSourceOptions.PrimeFrames"/>).</summary>
    Primed = 1 << 7,
}

/// <summary>One dequeued buffer, as the frame log records it (allocation-free: a preallocated array).</summary>
public readonly record struct CaptureFrameRecord
{
    public uint Sequence { get; init; }

    /// <summary>The driver's timestamp (CLOCK_MONOTONIC), ns.</summary>
    public long TimestampNs { get; init; }

    /// <summary>When it was dequeued, ns.</summary>
    public long DequeuedNs { get; init; }

    /// <summary>When its upload and metric were done (0: not judged), ns.</summary>
    public long JudgedNs { get; init; }

    /// <summary>The cadence metric (fraction of full scale; -1: not judged).</summary>
    public double Metric { get; init; }

    /// <summary>The brightest 4x4 cell's mean code value.</summary>
    public double MaxCellMean { get; init; }

    /// <summary>The tracker's game-rate estimate after this frame, fps.</summary>
    public double EstimatedFps { get; init; }

    public CaptureFrameEvents Flags { get; init; }
}
