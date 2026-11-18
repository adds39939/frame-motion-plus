using System.Globalization;
using FrameMotionPlus.Core.Frames;
using Vortice.Vulkan;

namespace FrameMotionPlus.Vulkan;

/// <summary>
/// The HDR10 static metadata a presenter hands to Vulkan for an HDR10 swapchain (<c>VkHdrMetadataEXT</c>,
/// VK_EXT_hdr_metadata): SMPTE ST 2086 mastering display (primaries, white point, luminance range) and CTA-861.3 content
/// light levels (MaxCLL, MaxFALL; 0 = not stated). The compositor uses it to fit the content to the display.
/// </summary>
public readonly record struct HdrMetadata
{
    public Chromaticity Red { get; init; }

    public Chromaticity Green { get; init; }

    public Chromaticity Blue { get; init; }

    public Chromaticity WhitePoint { get; init; }

    public float MaxLuminanceNits { get; init; }

    public float MinLuminanceNits { get; init; }

    public float MaxContentLightLevel { get; init; }

    public float MaxFrameAverageLightLevel { get; init; }

    /// <summary>
    /// What is sent for PQ content in <paramref name="content"/>'s colour: its mastering display and light levels when it
    /// states them; otherwise BT.2020 primaries, D65, <see cref="ColorMetadata.DefaultPeakNits"/> (the sources that know
    /// better, such as a capture with <c>--hdr-peak-nits</c>, state their own).
    /// </summary>
    public static HdrMetadata For(in ColorMetadata content)
    {
        var mastering = content.Mastering.IsSpecified ? content.Mastering : MasteringDisplay.Bt2020D65(ColorMetadata.DefaultPeakNits);
        return new HdrMetadata
        {
            Red = mastering.Red,
            Green = mastering.Green,
            Blue = mastering.Blue,
            WhitePoint = mastering.WhitePoint,
            MaxLuminanceNits = mastering.MaxLuminanceNits,
            MinLuminanceNits = mastering.MinLuminanceNits,
            MaxContentLightLevel = content.LightLevel.MaxCll,
            MaxFrameAverageLightLevel = content.LightLevel.MaxFall,
        };
    }

    internal VkHdrMetadataEXT ToVulkan() => new()
    {
        displayPrimaryRed = new VkXYColorEXT { x = Red.X, y = Red.Y },
        displayPrimaryGreen = new VkXYColorEXT { x = Green.X, y = Green.Y },
        displayPrimaryBlue = new VkXYColorEXT { x = Blue.X, y = Blue.Y },
        whitePoint = new VkXYColorEXT { x = WhitePoint.X, y = WhitePoint.Y },
        maxLuminance = MaxLuminanceNits,
        minLuminance = MinLuminanceNits,
        maxContentLightLevel = MaxContentLightLevel,
        maxFrameAverageLightLevel = MaxFrameAverageLightLevel,
    };

    /// <summary>Read back from the structure exactly as it was handed to Vulkan (tests and reports).</summary>
    internal static HdrMetadata FromVulkan(in VkHdrMetadataEXT v) => new()
    {
        Red = new Chromaticity(v.displayPrimaryRed.x, v.displayPrimaryRed.y),
        Green = new Chromaticity(v.displayPrimaryGreen.x, v.displayPrimaryGreen.y),
        Blue = new Chromaticity(v.displayPrimaryBlue.x, v.displayPrimaryBlue.y),
        WhitePoint = new Chromaticity(v.whitePoint.x, v.whitePoint.y),
        MaxLuminanceNits = v.maxLuminance,
        MinLuminanceNits = v.minLuminance,
        MaxContentLightLevel = v.maxContentLightLevel,
        MaxFrameAverageLightLevel = v.maxFrameAverageLightLevel,
    };

    /// <summary>"BT.2020 primaries, D65 white; mastering 0.0001 to 1000 nits; MaxCLL 1000, MaxFALL not stated".</summary>
    public override string ToString()
    {
        var ic = CultureInfo.InvariantCulture;
        var primaries = Near(Red, 0.708f, 0.292f) && Near(Green, 0.170f, 0.797f) && Near(Blue, 0.131f, 0.046f) ? "BT.2020 primaries"
            : Near(Red, 0.680f, 0.320f) && Near(Green, 0.265f, 0.690f) && Near(Blue, 0.150f, 0.060f) ? "P3 primaries"
            : Near(Red, 0.640f, 0.330f) && Near(Green, 0.300f, 0.600f) && Near(Blue, 0.150f, 0.060f) ? "BT.709 primaries"
            : string.Create(ic, $"primaries R {Red.X:0.###},{Red.Y:0.###} G {Green.X:0.###},{Green.Y:0.###} B {Blue.X:0.###},{Blue.Y:0.###}");
        var white = Near(WhitePoint, 0.3127f, 0.3290f) ? "D65 white" : string.Create(ic, $"white {WhitePoint.X:0.####},{WhitePoint.Y:0.####}");
        var cll = MaxContentLightLevel > 0 ? string.Create(ic, $"MaxCLL {MaxContentLightLevel:0}") : "MaxCLL not stated";
        var fall = MaxFrameAverageLightLevel > 0 ? string.Create(ic, $"MaxFALL {MaxFrameAverageLightLevel:0}") : "MaxFALL not stated";
        return string.Create(ic, $"{primaries}, {white}; mastering {MinLuminanceNits:0.####} to {MaxLuminanceNits:0} nits; {cll}, {fall}");
    }

    private static bool Near(Chromaticity c, float x, float y) => Math.Abs(c.X - x) < 0.002f && Math.Abs(c.Y - y) < 0.002f;
}
