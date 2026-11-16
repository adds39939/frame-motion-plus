namespace FrameMotionPlus.Core.Frames;

/// <summary>GPU frame pixel formats. All are 32 bits per pixel; RGB10A2 is the 10-bit HDR path.</summary>
public enum PixelFormat
{
    Unknown = 0,
    Rgba8 = 1,
    Bgra8 = 2,

    /// <summary>10-bit RGB with 2-bit alpha packed in 32 bits (NVCV_RGB10A2 / VK_FORMAT_A2B10G10R10_UNORM_PACK32).</summary>
    Rgb10A2 = 3,
}

/// <summary>Format families: VFG requires previous, current and output images from the same family.</summary>
public enum PixelFormatFamily
{
    Unknown = 0,
    EightBit = 1,
    TenBit = 2,
}

/// <summary>A set of <see cref="PixelFormat"/> values, for capability declarations.</summary>
[Flags]
public enum PixelFormatSet
{
    None = 0,
    Rgba8 = 1 << 0,
    Bgra8 = 1 << 1,
    Rgb10A2 = 1 << 2,
    All = Rgba8 | Bgra8 | Rgb10A2,
}

public static class PixelFormatExtensions
{
    public static int BytesPerPixel(this PixelFormat format) => format == PixelFormat.Unknown ? 0 : 4;

    public static int BitsPerColorChannel(this PixelFormat format) => format switch
    {
        PixelFormat.Rgba8 or PixelFormat.Bgra8 => 8,
        PixelFormat.Rgb10A2 => 10,
        _ => 0,
    };

    public static PixelFormatFamily Family(this PixelFormat format) => format switch
    {
        PixelFormat.Rgba8 or PixelFormat.Bgra8 => PixelFormatFamily.EightBit,
        PixelFormat.Rgb10A2 => PixelFormatFamily.TenBit,
        _ => PixelFormatFamily.Unknown,
    };

    public static PixelFormatSet ToSet(this PixelFormat format) => format switch
    {
        PixelFormat.Rgba8 => PixelFormatSet.Rgba8,
        PixelFormat.Bgra8 => PixelFormatSet.Bgra8,
        PixelFormat.Rgb10A2 => PixelFormatSet.Rgb10A2,
        _ => PixelFormatSet.None,
    };

    public static bool Contains(this PixelFormatSet set, PixelFormat format)
    {
        var bit = format.ToSet();
        return bit != PixelFormatSet.None && (set & bit) == bit;
    }

    /// <summary>Minimum row pitch in bytes for a tightly packed row.</summary>
    public static int MinimumPitch(this PixelFormat format, int width) => checked(width * format.BytesPerPixel());
}
