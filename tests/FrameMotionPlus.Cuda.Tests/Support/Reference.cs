using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Cuda.Tests.Support;

/// <summary>
/// CPU implementations of the kernels, written from the specifications in native/kernels (not from the kernel code),
/// for bit-exact comparison.
/// </summary>
internal static class Reference
{
    public const uint Bar8 = 191;
    public const uint Box8 = 255;
    public const uint Background8 = 48;

    /// <summary>Full-range 10-bit PQ codes of 203, 1000 and 10 nits.</summary>
    public const uint Bar10 = 594;
    public const uint Box10 = 769;
    public const uint Background10 = 307;

    /// <summary>Luminances of the 10-bit step wedge, nits.</summary>
    public static readonly double[] StepNits = [0, 0.1, 1, 10, 100, 203, 400, 1000, 4000, 10000];

    /// <summary>The wedge's PQ codes, derived here from the ST 2084 formula.</summary>
    public static readonly uint[] StepCodes = [.. StepNits.Select(PqCode)];

    // White, yellow, cyan, green, magenta, red, blue, black.
    private static readonly (bool R, bool G, bool B)[] _bars =
    [
        (true, true, true), (true, true, false), (false, true, true), (false, true, false),
        (true, false, true), (true, false, false), (false, false, true), (false, false, false),
    ];

    /// <summary>SMPTE ST 2084 inverse EOTF: luminance in nits to a full-range 10-bit code.</summary>
    public static uint PqCode(double nits)
    {
        const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128, C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;
        var y = Math.Pow(nits / 10000.0, M1);
        var e = Math.Pow((C1 + (C2 * y)) / (1 + (C3 * y)), M2);
        return (uint)Math.Round(e * 1023);
    }

    public static uint Pack8(uint r, uint g, uint b, uint a) => r | (g << 8) | (b << 16) | (a << 24);

    public static uint Pack10(uint r, uint g, uint b, uint a) => r | (g << 10) | (b << 20) | (a << 30);

    public static (uint R, uint G, uint B, uint A) Unpack(uint pixel, PixelFormat format) => format switch
    {
        PixelFormat.Rgb10A2 => (pixel & 0x3FF, (pixel >> 10) & 0x3FF, (pixel >> 20) & 0x3FF, pixel >> 30),
        PixelFormat.Bgra8 => ((pixel >> 16) & 0xFF, (pixel >> 8) & 0xFF, pixel & 0xFF, pixel >> 24),
        _ => (pixel & 0xFF, (pixel >> 8) & 0xFF, (pixel >> 16) & 0xFF, pixel >> 24),
    };

    public static uint Pack(uint r, uint g, uint b, uint a, PixelFormat format) => format switch
    {
        PixelFormat.Rgb10A2 => Pack10(r, g, b, a),
        PixelFormat.Bgra8 => Pack8(b, g, r, a),
        _ => Pack8(r, g, b, a),
    };

    public static uint[] Pattern(int width, int height, uint frame, PixelFormat format)
    {
        var pixels = new uint[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = PatternPixel(x, y, width, height, frame, format);
            }
        }

        return pixels;
    }

    public static uint PatternPixel(int x, int y, int width, int height, uint frame, PixelFormat format)
    {
        var tenBit = format == PixelFormat.Rgb10A2;
        var maxCode = tenBit ? 1023u : 255u;
        var alpha = tenBit ? 3u : 255u;
        var barsEnd = height * 7 / 12;
        var boxEnd = height * 9 / 12;
        var rampEnd = height * 10 / 12;

        if (y < barsEnd)
        {
            var (r, g, b) = _bars[x * 8 / width];
            var level = tenBit ? Bar10 : Bar8;
            return Pack(r ? level : 0, g ? level : 0, b ? level : 0, alpha, format);
        }

        uint v;
        if (y < boxEnd)
        {
            var boxWidth = Math.Max(width / 16, 1);
            var step = Math.Max(width / 240, 1);
            var travel = width - boxWidth;
            var boxX = travel == 0 ? 0 : (long)frame * step % travel;
            var inBox = x >= boxX && x < boxX + boxWidth;
            v = inBox ? (tenBit ? Box10 : Box8) : (tenBit ? Background10 : Background8);
        }
        else if (y < rampEnd)
        {
            v = tenBit && y >= (boxEnd + rampEnd) / 2
                ? StepCodes[x * 10 / width]
                : width > 1 ? (uint)((long)x * maxCode / (width - 1)) : 0;
        }
        else
        {
            var cellWidth = width / 32;
            var cell = cellWidth > 0 ? x / cellWidth : 32;
            v = cell < 32 && ((frame >> (31 - cell)) & 1) != 0 ? maxCode : 0;
        }

        return Pack(v, v, v, alpha, format);
    }

    /// <summary>Reads the frame counter back from a pattern image (cell centres, threshold at half scale).</summary>
    public static uint DecodeCounter(ReadOnlySpan<uint> pixels, int width, int height, PixelFormat format)
    {
        var cellWidth = width / 32;
        var y = ((height * 10 / 12) + height) / 2;
        var half = format == PixelFormat.Rgb10A2 ? 512u : 128u;
        uint value = 0;
        for (var cell = 0; cell < 32; cell++)
        {
            var (_, g, _, _) = Unpack(pixels[(y * width) + (cell * cellWidth) + (cellWidth / 2)], format);
            value = (value << 1) | (g >= half ? 1u : 0u);
        }

        return value;
    }

    public static uint BlendWeight(float t) => t <= 0 ? 0 : t >= 1 ? 65536u : (uint)Math.Round(t * 65536.0, MidpointRounding.ToEven);

    public static uint Mix(uint a, uint b, uint weight) => (uint)((((65536L - weight) * a) + ((long)weight * b) + 32768) >> 16);

    public static uint[] Blend(ReadOnlySpan<uint> prev, ReadOnlySpan<uint> curr, uint weight, PixelFormat format)
    {
        var result = new uint[prev.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var (ar, ag, ab, aa) = Unpack(prev[i], format);
            var (br, bg, bb, ba) = Unpack(curr[i], format);
            result[i] = Pack(Mix(ar, br, weight), Mix(ag, bg, weight), Mix(ab, bb, weight), Mix(aa, ba, weight), format);
        }

        return result;
    }

    public static uint[] Convert(ReadOnlySpan<uint> source, PixelFormat from, PixelFormat to)
    {
        var result = new uint[source.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var (r, g, b, a) = Unpack(source[i], from);
            if (from == PixelFormat.Rgb10A2 && to != PixelFormat.Rgb10A2)
            {
                (r, g, b, a) = (To8(r), To8(g), To8(b), a * 85);
            }
            else if (from != PixelFormat.Rgb10A2 && to == PixelFormat.Rgb10A2)
            {
                (r, g, b, a) = (To10(r), To10(g), To10(b), ((a * 3) + 127) / 255);
            }

            result[i] = Pack(r, g, b, a, to);
        }

        return result;
    }

    public static uint To10(uint c8) => ((c8 * 1023) + 127) / 255;

    public static uint To8(uint c10) => ((c10 * 255) + 511) / 1023;

    /// <summary>Deterministic pseudo-random pixels covering every code value of every channel.</summary>
    public static uint[] Noise(int count, int seed)
    {
        var random = new Random(seed);
        var pixels = new uint[count];
        for (var i = 0; i < count; i++)
        {
            pixels[i] = (uint)random.NextInt64(0, 1L << 32);
        }

        return pixels;
    }

    /// <summary>Index and values of the first difference, for assertion messages.</summary>
    public static string FirstDifference(ReadOnlySpan<uint> expected, ReadOnlySpan<uint> actual, int width)
    {
        if (expected.Length != actual.Length)
        {
            return $"length {expected.Length} vs {actual.Length}";
        }

        var count = 0;
        var first = -1;
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                count++;
                first = first < 0 ? i : first;
            }
        }

        return first < 0
            ? "identical"
            : $"{count} pixels differ; first at ({first % width}, {first / width}): expected 0x{expected[first]:x8}, got 0x{actual[first]:x8}";
    }
}
