using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Vulkan.Diagnostics;

/// <summary>Synthetic images for tests and diagnostics. Not used by the presenter.</summary>
public enum TestPattern
{
    /// <summary>
    /// Every channel varies per pixel (R with x, G with y, B with both and the frame index), so a 1:1 present that
    /// moves, drops or swaps anything shows up in a bit-exact comparison. Alpha is opaque.
    /// </summary>
    Ramp = 0,

    /// <summary>Low-frequency gradients between 1/8 and 7/8 of full scale, so scaled output can be checked against bilinear expectations.</summary>
    Smooth = 1,

    /// <summary>Colour bars with a white bar moving one step per frame, for watching a window.</summary>
    MovingBar = 2,
}

/// <summary>Generates <see cref="TestPattern"/> pixels in ring layout (pitch-linear Rgba8 or Rgb10A2 words).</summary>
public static class TestPatterns
{
    /// <summary>Packs 8-bit channels as Rgba8 bytes R, G, B, A (little-endian uint32).</summary>
    public static uint PackRgba8(int r, int g, int b, int a) =>
        (uint)(r & 0xFF) | ((uint)(g & 0xFF) << 8) | ((uint)(b & 0xFF) << 16) | ((uint)(a & 0xFF) << 24);

    /// <summary>Packs 10-bit channels and 2-bit alpha: R bits 0–9, G 10–19, B 20–29, A 30–31.</summary>
    public static uint PackRgb10A2(int r, int g, int b, int a) =>
        (uint)(r & 0x3FF) | ((uint)(g & 0x3FF) << 10) | ((uint)(b & 0x3FF) << 20) | ((uint)(a & 0x3) << 30);

    public static (int R, int G, int B, int A) Unpack(uint pixel, PixelFormat format) => format == PixelFormat.Rgb10A2
        ? ((int)(pixel & 0x3FF), (int)((pixel >> 10) & 0x3FF), (int)((pixel >> 20) & 0x3FF), (int)(pixel >> 30))
        : ((int)(pixel & 0xFF), (int)((pixel >> 8) & 0xFF), (int)((pixel >> 16) & 0xFF), (int)(pixel >> 24));

    /// <summary>The packed pixel at (x, y) of a <paramref name="width"/>×<paramref name="height"/> image.</summary>
    public static uint Pixel(TestPattern pattern, PixelFormat format, int x, int y, int width, int height, int frame)
    {
        var ten = format == PixelFormat.Rgb10A2;
        var max = ten ? 1023 : 255;
        int r, g, b;
        switch (pattern)
        {
            case TestPattern.Ramp:
                r = (x * 3) + frame;
                g = (y * 5) + (frame * 7);
                b = (x * 7) + (y * 11) + (frame * 13);
                break;
            case TestPattern.Smooth:
                // Kept away from 0 so the content rectangle can be told apart from the black bars.
                var lo = max / 8;
                var span = max - (2 * lo);
                r = lo + (int)((long)x * span / Math.Max(1, width - 1));
                g = lo + (int)((long)y * span / Math.Max(1, height - 1));
                b = lo + (int)((long)(x + y) * span / Math.Max(1, width + height - 2));
                break;
            case TestPattern.MovingBar:
                var bar = x * 8 / Math.Max(1, width);
                r = (bar & 1) != 0 ? max : max / 8;
                g = (bar & 2) != 0 ? max : max / 8;
                b = (bar & 4) != 0 ? max : max / 8;
                var position = frame * Math.Max(1, width / 120) % Math.Max(1, width);
                if (x >= position && x < position + Math.Max(2, width / 40))
                {
                    r = g = b = max;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pattern), pattern, null);
        }

        return ten ? PackRgb10A2(r & 0x3FF, g & 0x3FF, b & 0x3FF, 3) : PackRgba8(r & 0xFF, g & 0xFF, b & 0xFF, 255);
    }

    /// <summary>Fills <paramref name="destination"/> (rows of <paramref name="pitchBytes"/>) with a pattern. Allocation-free.</summary>
    public static void Fill(Span<byte> destination, TestPattern pattern, PixelFormat format, int width, int height, int pitchBytes, int frame)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, (pitchBytes * (height - 1)) + (width * 4), nameof(destination));
        for (var y = 0; y < height; y++)
        {
            var row = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(destination.Slice(y * pitchBytes, width * 4));
            for (var x = 0; x < row.Length; x++)
            {
                row[x] = Pixel(pattern, format, x, y, width, height, frame);
            }
        }
    }
}
