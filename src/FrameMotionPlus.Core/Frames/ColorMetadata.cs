namespace FrameMotionPlus.Core.Frames;

public enum ColorPrimaries
{
    Unspecified = 0,
    Bt709 = 1,
    Bt2020 = 2,
    DciP3 = 3,
    DisplayP3 = 4,

    /// <summary>BT.601 525-line (SMPTE 170M; SMPTE 240M has the same primaries): standard-definition "NTSC" video.</summary>
    Bt601Line525 = 5,

    /// <summary>BT.601 625-line (BT.470 System B/G, EBU Tech 3213): standard-definition "PAL" video.</summary>
    Bt601Line625 = 6,
}

public enum TransferFunction
{
    Unspecified = 0,

    /// <summary>BT.709 / BT.1886 SDR gamma.</summary>
    Bt709 = 1,
    Srgb = 2,

    /// <summary>SMPTE ST 2084 perceptual quantiser (HDR10).</summary>
    Pq = 3,

    /// <summary>ARIB STD-B67 hybrid log-gamma; converted to PQ at ingest.</summary>
    Hlg = 4,
    Linear = 5,
}

public enum MatrixCoefficients
{
    Unspecified = 0,

    /// <summary>RGB, no YCbCr matrix.</summary>
    Identity = 1,
    Bt601 = 2,
    Bt709 = 3,
    Bt2020NonConstant = 4,
}

public enum ColorRange
{
    Unspecified = 0,

    /// <summary>Limited / video range (16-235 for 8-bit, 64-940 for 10-bit).</summary>
    Limited = 1,
    Full = 2,
}

/// <summary>CIE 1931 xy chromaticity coordinate.</summary>
public readonly record struct Chromaticity(float X, float Y);

/// <summary>SMPTE ST 2086 mastering display colour volume.</summary>
public readonly record struct MasteringDisplay
{
    public Chromaticity Red { get; init; }

    public Chromaticity Green { get; init; }

    public Chromaticity Blue { get; init; }

    public Chromaticity WhitePoint { get; init; }

    public float MaxLuminanceNits { get; init; }

    public float MinLuminanceNits { get; init; }

    public bool IsSpecified => MaxLuminanceNits > 0;

    /// <summary>A common HDR10 default for mastered video that carries none: P3-D65 primaries, 1000 nits.</summary>
    public static MasteringDisplay P3D65Default => new()
    {
        Red = new Chromaticity(0.680f, 0.320f),
        Green = new Chromaticity(0.265f, 0.690f),
        Blue = new Chromaticity(0.150f, 0.060f),
        WhitePoint = new Chromaticity(0.3127f, 0.3290f),
        MaxLuminanceNits = 1000f,
        MinLuminanceNits = 0.0001f,
    };

    /// <summary>
    /// The default for a source that sends no mastering metadata, such as a games console over a capture card: BT.2020
    /// primaries (the HDR10 container, which a game renders into), D65 white, <paramref name="maxNits"/> peak (a console's
    /// HDR calibration targets the display it sees, so this is best set to that display's peak) and 0.0001 nits black.
    /// </summary>
    public static MasteringDisplay Bt2020D65(float maxNits) => new()
    {
        Red = new Chromaticity(0.708f, 0.292f),
        Green = new Chromaticity(0.170f, 0.797f),
        Blue = new Chromaticity(0.131f, 0.046f),
        WhitePoint = new Chromaticity(0.3127f, 0.3290f),
        MaxLuminanceNits = maxNits,
        MinLuminanceNits = 0.0001f,
    };
}

/// <summary>CTA-861.3 content light level: MaxCLL and MaxFALL in nits.</summary>
public readonly record struct ContentLightLevel(ushort MaxCll, ushort MaxFall)
{
    public bool IsSpecified => MaxCll > 0 || MaxFall > 0;
}

/// <summary>
/// Colour description that travels with every frame. Blittable.
/// <para>
/// It describes the pixels of the frame it travels with, as they are stored. The pipeline's frames are RGB
/// (<see cref="PixelFormat"/>), so theirs is <see cref="MatrixCoefficients.Identity"/> and <see cref="ColorRange.Full"/>,
/// with the primaries, transfer function and HDR metadata of the content (<see cref="SdrBt709"/>, <see cref="Hdr10"/>).
/// A source that decodes YCbCr describes its converted frames this way; the YCbCr matrix and range it converted from
/// belong to the source's own stream description, not to the frames.
/// </para>
/// </summary>
public readonly record struct ColorMetadata
{
    public ColorPrimaries Primaries { get; init; }

    public TransferFunction Transfer { get; init; }

    public MatrixCoefficients Matrix { get; init; }

    public ColorRange Range { get; init; }

    public MasteringDisplay Mastering { get; init; }

    public ContentLightLevel LightLevel { get; init; }

    public bool IsHdr => Transfer is TransferFunction.Pq or TransferFunction.Hlg;

    public static ColorMetadata SdrBt709 => new()
    {
        Primaries = ColorPrimaries.Bt709,
        Transfer = TransferFunction.Bt709,
        Matrix = MatrixCoefficients.Identity,
        Range = ColorRange.Full,
    };

    /// <summary>
    /// HDR10 RGB frames with the defaults for a source that sends no metadata (<see cref="MasteringDisplay.Bt2020D65"/> at
    /// <see cref="DefaultPeakNits"/>, MaxCLL the same, MaxFALL not stated).
    /// </summary>
    public static ColorMetadata Hdr10 => Hdr10WithPeak(DefaultPeakNits, DefaultPeakNits, 0);

    /// <summary>The mastering peak (and MaxCLL) assumed for a source that states none, in nits.</summary>
    public const int DefaultPeakNits = 1000;

    /// <summary>HDR10 RGB frames from a source with no metadata of its own, with the given peak, MaxCLL and MaxFALL (0: not stated).</summary>
    public static ColorMetadata Hdr10WithPeak(int peakNits, int maxCll, int maxFall) => new()
    {
        Primaries = ColorPrimaries.Bt2020,
        Transfer = TransferFunction.Pq,
        Matrix = MatrixCoefficients.Identity,
        Range = ColorRange.Full,
        Mastering = MasteringDisplay.Bt2020D65(peakNits),
        LightLevel = new ContentLightLevel((ushort)Math.Clamp(maxCll, 0, ushort.MaxValue), (ushort)Math.Clamp(maxFall, 0, ushort.MaxValue)),
    };

    /// <summary>True for content in the SMPTE ST 2084 transfer: what an HDR10 swapchain shows as it is.</summary>
    public bool IsPq => Transfer == TransferFunction.Pq;
}
