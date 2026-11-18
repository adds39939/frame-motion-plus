using System.Globalization;
using System.Runtime.Versioning;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Sources.File;
using FrameMotionPlus.Sources.V4l2;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// What the pipeline needs from a source beyond Core's <see cref="IFrameSource"/>: when its content times start, whether
/// it is live, whether it has ended or failed. A capture source (Phase 5) is live: its content times are capture
/// timestamps and its frames arrive no earlier than they happen. A file is not: it is decoded ahead, and its content
/// times are media time plus an origin the pipeline picks when playback starts.
/// </summary>
public interface IPlaybackSource : IDisposable
{
    IFrameSource Frames { get; }

    /// <summary>False for a source decoded ahead of time (a file): see the class remarks.</summary>
    bool IsLive { get; }

    /// <summary>The source's nominal frame rate (default when unknown).</summary>
    FrameRate NominalRate { get; }

    /// <summary>Starts producing. A source that is not live puts its first frame at <paramref name="contentOriginNs"/>.</summary>
    void Start(long contentOriginNs);

    /// <summary>Stops producing (bounded); frames already delivered stay valid until released or disposed.</summary>
    void Stop();

    /// <summary>The stream has ended and every frame has been delivered (never for a live or looping source).</summary>
    bool IsEndOfStream { get; }

    /// <summary>Why the source stopped producing, if it failed.</summary>
    Exception? Failure { get; }

    /// <summary>One line: what is playing.</summary>
    string Describe();

    /// <summary>
    /// Teardown when GPU work did not finish (nothing that waits for the GPU may be called): let go of whatever outside the
    /// GPU the source holds (a capture device, so another program can use it at once), without stopping or freeing anything
    /// that waits for GPU work. A file holds nothing of the kind.
    /// </summary>
    void Abandon()
    {
    }
}

/// <summary>A video file (<see cref="FileFrameSource"/>) as a playback source.</summary>
public sealed class FilePlaybackSource : IPlaybackSource
{
    private bool _started;

    /// <summary>Opens the file (decoder built, first frame decoded, every buffer allocated: a drained point).</summary>
    public FilePlaybackSource(CudaContext context, string path, FileSourceOptions options)
    {
        Source = new FileFrameSource(context, path, options);
    }

    public FileFrameSource Source { get; }

    public IFrameSource Frames => Source;

    public FileStreamInfo StreamInfo => Source.StreamInfo;

    public bool IsLive => false;

    public FrameRate NominalRate => Source.StreamInfo.NominalRate;

    public bool IsEndOfStream => Source.IsEndOfStream;

    public Exception? Failure => Source.Failure;

    public CudaStream CopyStream => Source.CopyStream;

    public CudaStream ComputeStream => Source.ComputeStream;

    public void Start(long contentOriginNs)
    {
        if (!_started)
        {
            Source.SetContentTimeOrigin(contentOriginNs);
            _started = true;
        }

        Source.Start();
    }

    public void Stop() => Source.Stop();

    /// <summary>Continues from <paramref name="mediaTimeNs"/>; the first frame after it is a discontinuity. Any thread.</summary>
    public void Seek(long mediaTimeNs) => Source.Seek(mediaTimeNs);

    public string Describe() => Source.StreamInfo.Describe();

    public void Dispose() => Source.Dispose();

    /// <summary>The source options the pipeline uses: its streams and kernels, decode-ahead and history depth.</summary>
    public static FileSourceOptions OptionsFor(PlaybackOptions options, CudaStream copy, CudaStream compute, ImageKernels kernels) => new()
    {
        DecodeAhead = options.DecodeAhead,
        HistoryDepth = options.HistoryDepth,
        Loop = options.Loop,
        CopyStream = copy,
        ComputeStream = compute,
        Kernels = kernels,
        DecodeThread = options.Threads,
    };
}

/// <summary>
/// A V4L2 capture device (<see cref="V4l2FrameSource"/>) as a playback source: live. Its content times are the driver's
/// capture timestamps (CLOCK_MONOTONIC), its frames arrive when uploaded and converted, it is never decoded ahead, and
/// when the pipeline does not take its frames it drops them itself (counted): nothing slows it down.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class CapturePlaybackSource : IPlaybackSource
{
    /// <summary>Opens the device and negotiates (a drained point): see <see cref="V4l2FrameSource"/>.</summary>
    public CapturePlaybackSource(CudaContext context, CaptureSourceOptions options)
    {
        Source = new V4l2FrameSource(context, options);
    }

    public V4l2FrameSource Source { get; }

    public IFrameSource Frames => Source;

    public bool IsLive => true;

    /// <summary>The signal's rate as negotiated (the game's own rate is what the cadence detector finds).</summary>
    public FrameRate NominalRate => Source.Format.Rate;

    public bool IsEndOfStream => false;

    public Exception? Failure => Source.Failure;

    /// <summary>Starts streaming. A live source has no origin to apply: its content times are capture times.</summary>
    public void Start(long contentOriginNs) => Source.Start();

    public void Stop() => Source.Stop();

    public string Describe() => Source.Describe();

    public void Abandon() => Source.Abandon();

    public void Dispose() => Source.Dispose();

    /// <summary>Everything the summary prints about the capture (read it once the source has stopped).</summary>
    public CaptureSummary Summarise() => CaptureSummary.From(Source);
}

/// <summary>A distribution reduced to its numbers (ns, or the metric in millionths).</summary>
public readonly record struct DistributionSummary(long Count, double Mean, double StdDev, long Min, long P50, long P99, long Max)
{
    public static DistributionSummary From(CaptureDistribution d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new DistributionSummary(d.Count, d.Mean, d.StdDev, d.Min, d.Percentile(0.5), d.Percentile(0.99), d.Max);
    }

    public string Ms(string label) => Count == 0
        ? $"{label} none"
        : string.Create(CultureInfo.InvariantCulture, $"{label} p50 {P50 / 1e6:0.000} ms, p99 {P99 / 1e6:0.000} ms, max {Max / 1e6:0.000} ms");
}

/// <summary>A capture's counters and distributions at the end of a run.</summary>
public sealed record CaptureSummary
{
    public required CaptureStatistics Counters { get; init; }

    public required CaptureSignalState State { get; init; }

    public string? ThreadReport { get; init; }

    public DistributionSummary Intervals { get; init; }

    public DistributionSummary DequeueLag { get; init; }

    public DistributionSummary CaptureToAvailable { get; init; }

    public DistributionSummary UploadWait { get; init; }

    public DistributionSummary UploadGpu { get; init; }

    public DistributionSummary CadenceGpu { get; init; }

    public DistributionSummary ConvertGpu { get; init; }

    public DistributionSummary UniqueMetric { get; init; }

    public DistributionSummary RepeatMetric { get; init; }

    public DistributionSummary UniqueIntervals { get; init; }

    public long SignalIntervalNs { get; init; }

    [SupportedOSPlatform("linux")]
    public static CaptureSummary From(V4l2FrameSource s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new CaptureSummary
        {
            Counters = s.Statistics,
            State = s.State,
            ThreadReport = s.ThreadReport,
            Intervals = DistributionSummary.From(s.Intervals),
            DequeueLag = DistributionSummary.From(s.DequeueLag),
            CaptureToAvailable = DistributionSummary.From(s.CaptureToAvailable),
            UploadWait = DistributionSummary.From(s.UploadWait),
            UploadGpu = DistributionSummary.From(s.UploadGpu),
            CadenceGpu = DistributionSummary.From(s.CadenceGpu),
            ConvertGpu = DistributionSummary.From(s.ConvertGpu),
            UniqueMetric = DistributionSummary.From(s.UniqueMetric),
            RepeatMetric = DistributionSummary.From(s.RepeatMetric),
            UniqueIntervals = DistributionSummary.From(s.UniqueIntervals),
            SignalIntervalNs = s.Cadence.SignalIntervalNs,
        };
    }
}
