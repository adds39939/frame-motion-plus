using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Vfx.Tests.Support;

/// <summary>Difference of two images over a band of rows, in code values (8- or 10-bit), colour channels only.</summary>
internal readonly record struct ImageError(double Mae, double Psnr, uint MaxError, long Pixels)
{
    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"MAE {Mae:0.000}, PSNR {Psnr:0.00} dB, max {MaxError}");
}

internal static class Images
{
    public static (uint R, uint G, uint B) Rgb(uint pixel, PixelFormat format) => format switch
    {
        PixelFormat.Rgb10A2 => (pixel & 0x3FF, (pixel >> 10) & 0x3FF, (pixel >> 20) & 0x3FF),
        PixelFormat.Bgra8 => ((pixel >> 16) & 0xFF, (pixel >> 8) & 0xFF, pixel & 0xFF),
        _ => (pixel & 0xFF, (pixel >> 8) & 0xFF, (pixel >> 16) & 0xFF),
    };

    public static uint Pack(uint r, uint g, uint b, PixelFormat format) => format switch
    {
        PixelFormat.Rgb10A2 => r | (g << 10) | (b << 20) | (3u << 30),
        PixelFormat.Bgra8 => b | (g << 8) | (r << 16) | (255u << 24),
        _ => r | (g << 8) | (b << 16) | (255u << 24),
    };

    public static uint MaxCode(PixelFormat format) => format == PixelFormat.Rgb10A2 ? 1023u : 255u;

    /// <summary>Rows [<paramref name="y0"/>, <paramref name="y1"/>) of two tightly packed images.</summary>
    public static ImageError Compare(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, int width, int y0, int y1, PixelFormat format)
    {
        double sumAbs = 0;
        double sumSq = 0;
        uint max = 0;
        long n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (ar, ag, ab) = Rgb(a[(y * width) + x], format);
                var (br, bg, bb) = Rgb(b[(y * width) + x], format);
                foreach (var d in (Span<int>)[(int)ar - (int)br, (int)ag - (int)bg, (int)ab - (int)bb])
                {
                    var e = (uint)Math.Abs(d);
                    sumAbs += e;
                    sumSq += (double)e * e;
                    max = Math.Max(max, e);
                }

                n++;
            }
        }

        var mse = sumSq / (3.0 * n);
        var peak = MaxCode(format);
        var psnr = mse == 0 ? 99.0 : 10 * Math.Log10(peak * (double)peak / mse);
        return new ImageError(sumAbs / (3.0 * n), psnr, max, n);
    }

    /// <summary>(1 − t)·a + t·b per channel, rounded: the linear blend baseline.</summary>
    public static uint[] Blend(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, double t, PixelFormat format)
    {
        var result = new uint[a.Length];
        for (var i = 0; i < a.Length; i++)
        {
            var (ar, ag, ab) = Rgb(a[i], format);
            var (br, bg, bb) = Rgb(b[i], format);
            result[i] = Pack(Mix(ar, br, t), Mix(ag, bg, t), Mix(ab, bb, t), format);
        }

        return result;
    }

    private static uint Mix(uint a, uint b, double t) => (uint)Math.Round(((1 - t) * a) + (t * b));

    /// <summary>
    /// The horizontal centre of mass of brightness above <paramref name="threshold"/> in rows [y0, y1): where a bright
    /// object sits (green channel).
    /// </summary>
    public static double BrightCentroidX(ReadOnlySpan<uint> pixels, int width, int y0, int y1, PixelFormat format, uint threshold)
    {
        double sum = 0;
        double weight = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (_, g, _) = Rgb(pixels[(y * width) + x], format);
                if (g > threshold)
                {
                    sum += x * (double)(g - threshold);
                    weight += g - threshold;
                }
            }
        }

        return weight == 0 ? double.NaN : sum / weight;
    }

    /// <summary>
    /// A smooth, detailed, colourful texture (sums of sinusoids at several scales and orientations), <paramref name="width"/>
    /// px wide, cropped at <paramref name="offsetX"/>: a pan by whole pixels, so every intermediate position is exact.
    /// </summary>
    public static uint[] Texture(int width, int height, int offsetX, PixelFormat format)
    {
        var pixels = new uint[width * height];
        var max = MaxCode(format);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double u = x + offsetX;
                double v = y;
                var r = 0.5 + (0.22 * Math.Sin(u * 0.031 + v * 0.017)) + (0.15 * Math.Sin(u * 0.11 - v * 0.07)) + (0.1 * Math.Sin((u * 0.29) + (v * 0.23)));
                var g = 0.5 + (0.22 * Math.Sin(u * 0.023 - v * 0.029 + 1)) + (0.15 * Math.Sin(v * 0.13 + u * 0.05)) + (0.1 * Math.Sin((u * 0.37) - (v * 0.19)));
                var b = 0.5 + (0.22 * Math.Sin(v * 0.027 + 2)) + (0.15 * Math.Sin(u * 0.09 + v * 0.09)) + (0.1 * Math.Sin((u * 0.21) + (v * 0.41) + 3));
                pixels[(y * width) + x] = Pack(Code(r, max), Code(g, max), Code(b, max), format);
            }
        }

        return pixels;
    }

    private static uint Code(double value, uint max) => (uint)Math.Clamp(Math.Round(value * max), 0, max);
}
