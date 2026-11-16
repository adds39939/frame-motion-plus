namespace FrameMotionPlus.Core.Frames;

public enum ColorPrimaries
{
    Unspecified = 0,
    Bt709 = 1,
    Bt2020 = 2,
    DciP3 = 3,
    DisplayP3 = 4,
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

    /// <summary>A common HDR10 default when the source carries none: P3-D65 primaries, 1000 nits.</summary>
    public static MasteringDisplay P3D65Default => new()
    {
        Red = new Chromaticity(0.680f, 0.320f),
        Green = new Chromaticity(0.265f, 0.690f),
        Blue = new Chromaticity(0.150f, 0.060f),
        WhitePoint = new Chromaticity(0.3127f, 0.3290f),
        MaxLuminanceNits = 1000f,
        MinLuminanceNits = 0.0001f,
    };
}

/// <summary>CTA-861.3 content light level: MaxCLL and MaxFALL in nits.</summary>
public readonly record struct ContentLightLevel(ushort MaxCll, ushort MaxFall)
{
    public bool IsSpecified => MaxCll > 0 || MaxFall > 0;
}

/// <summary>Colour description that travels with every frame. Blittable.</summary>
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

    public static ColorMetadata Hdr10 => new()
    {
        Primaries = ColorPrimaries.Bt2020,
        Transfer = TransferFunction.Pq,
        Matrix = MatrixCoefficients.Identity,
        Range = ColorRange.Full,
        Mastering = MasteringDisplay.P3D65Default,
        LightLevel = new ContentLightLevel(1000, 400),
    };
}
