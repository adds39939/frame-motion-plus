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

    /// <summary>Tells the source that no pending GPU work references <paramref name="frameId"/> any more.</summary>
    void Release(long frameId);
}
