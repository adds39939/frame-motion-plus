namespace FrameMotionPlus.Core.Frames;

[Flags]
public enum SourceFrameMarks
{
    None = 0,

    /// <summary>Source switch, seek or detected cut: never interpolate across this frame's left edge.</summary>
    Discontinuity = 1 << 0,

    /// <summary>Size, pixel format or colour metadata differ from the previous frame.</summary>
    FormatChanged = 1 << 1,
}

/// <summary>
/// A unique frame delivered by an <see cref="Abstractions.IFrameSource"/>. Repeats of the same picture are
/// removed by the source's cadence stage, so every <see cref="SourceFrame"/> is new content.
/// </summary>
public readonly record struct SourceFrame
{
    /// <summary>Monotonically increasing identifier assigned by the source.</summary>
    public long FrameId { get; init; }

    public GpuFrame Frame { get; init; }

    /// <summary>When the frame became usable on the GPU (after upload and convert are enqueued).</summary>
    public long ArrivalTimeNs { get; init; }

    /// <summary>
    /// GPU-side signal for upload/convert completion. Consumers wait on it GPU-side. Sources set the same point as
    /// <see cref="Frame"/>.<see cref="GpuFrame.Ready"/>, which is what a generator sees.
    /// </summary>
    public GpuSignal Ready { get; init; }

    public SourceFrameMarks Marks { get; init; }

    public long ContentTimeNs => Frame.ContentTimeNs;
}
