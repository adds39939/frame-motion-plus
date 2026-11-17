using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda.Kernels;

namespace FrameMotionPlus.Sources.File;

/// <summary>What the file's video stream is, as opened (stream-level values; frames may refine the colour).</summary>
public sealed record FileStreamInfo
{
    public required string Path { get; init; }

    /// <summary>The demuxer (container) name, e.g. "mov,mp4,m4a,3gp,3g2,mj2" or "matroska,webm".</summary>
    public required string Container { get; init; }

    /// <summary>FFmpeg's codec name: h264, hevc, av1, vp9.</summary>
    public required string Codec { get; init; }

    /// <summary>The codec profile's name (Main, Main 10, High, ...), or empty.</summary>
    public required string Profile { get; init; }

    public int StreamIndex { get; init; }

    /// <summary>Visible size.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Bits per sample (8 or 10).</summary>
    public int BitDepth { get; init; }

    /// <summary>The decoded sample layout in CUDA memory (NVDEC's output): NV12 for 8-bit, P010 for 10-bit.</summary>
    public YuvSampleFormat SampleFormat { get; init; }

    /// <summary>The RGB format frames are converted to: RGBA8 for 8-bit sources, RGB10A2 for 10-bit.</summary>
    public PixelFormat OutputFormat { get; init; }

    /// <summary>Nominal rate from the stream (average frame rate, else the container's base rate); default when unknown.</summary>
    public FrameRate NominalRate { get; init; }

    public int TimeBaseNum { get; init; }

    public int TimeBaseDen { get; init; }

    /// <summary>The stream's first timestamp in stream ticks, or <see cref="long.MinValue"/> when unknown.</summary>
    public long StartTicks { get; init; }

    /// <summary>
    /// The container's timestamps may legitimately jump (libavformat flags the format <c>AVFMT_TS_DISCONT</c>: MPEG-TS,
    /// MPEG-PS): a forward jump of more than 10 s is then collapsed in content time. In a container with a continuous
    /// timeline (MP4, Matroska, ...) such a gap is content (a still held for that long) and is kept.
    /// </summary>
    public bool DiscontinuousTimestamps { get; init; }

    /// <summary>Duration in ns, 0 when unknown.</summary>
    public long DurationNs { get; init; }

    /// <summary>Frames the container declares, 0 when unknown.</summary>
    public long DeclaredFrames { get; init; }

    /// <summary>The colour description of the first frame (before defaults): what the conversion follows.</summary>
    public RawColorDescription RawColor { get; init; }

    /// <summary>The colour description the container states (before defaults); frames override it.</summary>
    public RawColorDescription ContainerRawColor { get; init; }

    /// <summary>Colour metadata of the converted frames, with defaults applied and stream-level HDR metadata.</summary>
    public ColorMetadata Color { get; init; }

    /// <summary>The conversion the frames go through (matrix, range, bit depth, siting, output).</summary>
    public YuvConversion Conversion { get; init; }

    public string Describe() =>
        $"{Codec}{(Profile.Length > 0 ? $" ({Profile})" : string.Empty)} {Width}x{Height} {BitDepth}-bit {SampleFormat}, " +
        $"{(NominalRate.IsValid ? $"{NominalRate.Hz:0.###} fps" : "rate unknown")}, {Container}";
}
