using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Abstractions;

public enum FrameSourceKind
{
    Unknown = 0,
    CaptureCard = 1,
    File = 2,
    WindowCapture = 3,
    RemotePlay = 4,
    Synthetic = 5,
}

public readonly record struct FrameSourceInfo
{
    public FrameSourceKind Kind { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public PixelFormat Format { get; init; }

    public ColorMetadata Color { get; init; }

    /// <summary>Nominal signal rate (e.g. the HDMI refresh for a capture card); default when unknown.</summary>
    public FrameRate NominalRate { get; init; }
}

/// <summary>
/// Produces GPU-resident unique frames with content timestamps. Implementations run their own capture or
/// decode thread and publish through a single-producer ring; the pipeline drains it with
/// <see cref="TryRead"/> from one consumer thread.
/// </summary>
public interface IFrameSource : IDisposable
{
    FrameSourceInfo Info { get; }

    void Start();

    void Stop();

    /// <summary>Non-blocking. Returns the next unique frame in content-time order, if one is ready.</summary>
    bool TryRead(out SourceFrame frame);

    /// <summary>
    /// Gives a frame back: no work enqueued after this call reads <paramref name="frameId"/>, and the work enqueued before
    /// has finished reading it once <paramref name="lastReader"/> is reached, GPU-side (a generator's <c>done</c>, the
    /// presentation-ring write that copied it, an event recorded after the reads; <see cref="GpuSignal.None"/> when no
    /// work read it). The source writes the frame's buffer again only after that point, without the CPU waiting for it.
    /// The object behind <paramref name="lastReader"/> must stay alive until the source is stopped or disposed (the wait
    /// is enqueued when the buffer is next written), and the point must be reached, or the source's writes stop there.
    /// Consumer thread; allocation-free.
    /// </summary>
    void Release(long frameId, in GpuSignal lastReader);
}
