using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Sources.File;

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
