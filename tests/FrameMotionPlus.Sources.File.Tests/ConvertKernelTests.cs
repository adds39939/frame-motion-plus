using System.Globalization;
using System.Numerics;
using FrameMotionPlus.App.Decode;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Cuda.Tests.Support;
using FrameMotionPlus.Sources.File.Tests.Support;

namespace FrameMotionPlus.Sources.File.Tests;

/// <summary>
/// The YCbCr-to-RGB kernel (native/kernels/yuv.cu) against the CPU reference (<see cref="YuvReference"/>), bit for bit,
/// for every matrix, range, bit depth, output format and chroma siting, on pitched inputs whose padding holds values that
/// would change the result if read and outputs inside guard bytes that must stay untouched. Three derivations agree:
/// the kernel's closed-form integer coefficients (<see cref="YuvConversion"/>), the reference's Gauss–Jordan inverse of
/// the forward matrix, and an exact rational oracle here that evaluates H.273's equations per pixel. The reference is
/// also compared with FFmpeg's swscale, which rounds differently, within a stated tolerance.
/// </summary>
[Trait(Category.Name, Category.Media)]
public class ConvertKernelTests
{
    public static TheoryData<MatrixCoefficients, ColorRange, int, PixelFormat> Configurations()
    {
        var data = new TheoryData<MatrixCoefficients, ColorRange, int, PixelFormat>();
        foreach (var (matrix, range, bits, output) in AllConfigurations())
        {
            data.Add(matrix, range, bits, output);
        }

        return data;
    }

    private static IEnumerable<(MatrixCoefficients Matrix, ColorRange Range, int Bits, PixelFormat Output)> AllConfigurations()
    {
        foreach (var matrix in new[] { MatrixCoefficients.Bt601, MatrixCoefficients.Bt709, MatrixCoefficients.Bt2020NonConstant })
        {
            foreach (var range in new[] { ColorRange.Limited, ColorRange.Full })
            {
                foreach (var bits in new[] { 8, 10 })
                {
                    foreach (var output in new[] { PixelFormat.Rgba8, PixelFormat.Rgb10A2 })
                    {
                        yield return (matrix, range, bits, output);
                    }
                }
            }
        }
    }

    /// <summary>Every siting at once per configuration, on an odd size with random codes over the whole sample range.</summary>
    [Theory]
    [MemberData(nameof(Configurations))]
    public void Kernel_is_bit_exact_against_the_cpu_reference(MatrixCoefficients matrix, ColorRange range, int bits, PixelFormat output)
    {
        var env = Media.RequireCuda();
        const int w = 67, h = 37;
        var (y, cb, cr) = Planes.Random(w, h, bits, seed: (int)matrix * 100 + (int)range * 10 + bits + (int)output);
        foreach (var siting in Enum.GetValues<ChromaSiting>())
        {
            var conversion = YuvConversion.Create(matrix, range, bits, siting, output);
            var actual = Planes.RunKernel(env, conversion, y, cb, cr, w, h);
            var expected = new uint[w * h];
            YuvReference.Create(matrix, range, bits, siting, output).Convert(y, cb, cr, w, h, expected);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{conversion}: {Planes.FirstDifference(expected, actual, w)}");
        }
    }

    /// <summary>Sizes at the edges of the chroma grid: 1x1, odd widths and heights, a 2-pixel image, a non-multiple of 16.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(1, 3)]
    [InlineData(5, 7)]
    [InlineData(1366, 766)]
    public void Kernel_is_bit_exact_at_every_size(int w, int h)
    {
        var env = Media.RequireCuda();
        foreach (var (bits, output, siting) in new[] { (8, PixelFormat.Rgba8, ChromaSiting.Left), (10, PixelFormat.Rgb10A2, ChromaSiting.TopLeft), (8, PixelFormat.Rgba8, ChromaSiting.Bottom) })
        {
            var (y, cb, cr) = Planes.Random(w, h, bits, seed: w * 31 + h);
            var conversion = YuvConversion.Create(MatrixCoefficients.Bt709, ColorRange.Limited, bits, siting, output);
            var actual = Planes.RunKernel(env, conversion, y, cb, cr, w, h);
            var expected = new uint[w * h];
            YuvReference.Create(MatrixCoefficients.Bt709, ColorRange.Limited, bits, siting, output).Convert(y, cb, cr, w, h, expected);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{w}x{h} {conversion}: {Planes.FirstDifference(expected, actual, w)}");
        }
    }

    /// <summary>
    /// The reference against an exact oracle written here from H.273's equations (per pixel, rational arithmetic:
    /// dequantise, place each chroma sample at its sited luma coordinates and interpolate linearly, apply the closed-form
    /// inverse R' = Y' + 2(1 − Kr)Cr, B' = Y' + 2(1 − Kb)Cb, G' = (Y' − Kr·R' − Kb·B') / Kg, round half up, clamp), for every
    /// configuration and siting, including codes outside the legal range and the extremes.
    /// </summary>
    [Fact]
    public void The_cpu_reference_equals_an_exact_oracle_of_the_standards()
    {
        const int w = 9, h = 7;
        var checkedPixels = 0;
        foreach (var (matrix, range, bits, output) in AllConfigurations())
        {
            var (y, cb, cr) = Planes.Random(w, h, bits, seed: 7 + checkedPixels);
            foreach (var siting in Enum.GetValues<ChromaSiting>())
            {
                var reference = YuvReference.Create(matrix, range, bits, siting, output);
                for (var py = 0; py < h; py++)
                {
                    for (var px = 0; px < w; px++)
                    {
                        var expected = Oracle.Pixel(matrix, range, bits, siting, output, y, cb, cr, w, h, px, py);
                        var actual = reference.Pixel(y, cb, cr, w, h, px, py);
                        Assert.True(expected == actual, $"{matrix} {range} {bits}-bit {siting} {output} at ({px},{py}): oracle 0x{expected:x8}, reference 0x{actual:x8}");
                        checkedPixels++;
                    }
                }
            }
        }

        Assert.Equal(24 * 6 * w * h, checkedPixels);
    }

    /// <summary>Known values: limited-range black, white and the BT.709 primaries; full-range grey; 10-bit PQ white.</summary>
    [Fact]
    public void Reference_values_of_known_colours()
    {
        static uint One(MatrixCoefficients m, ColorRange r, int bits, PixelFormat o, ushort y, ushort cb, ushort cr) =>
            YuvReference.Create(m, r, bits, ChromaSiting.Left, o).Pixel([y], [cb], [cr], 1, 1, 0, 0);

        Assert.Equal(0xFF000000u, One(MatrixCoefficients.Bt709, ColorRange.Limited, 8, PixelFormat.Rgba8, 16, 128, 128));
        Assert.Equal(0xFFFFFFFFu, One(MatrixCoefficients.Bt709, ColorRange.Limited, 8, PixelFormat.Rgba8, 235, 128, 128));
        Assert.Equal(0xFF000000u, One(MatrixCoefficients.Bt709, ColorRange.Limited, 8, PixelFormat.Rgba8, 0, 128, 128)); // below black clamps
        Assert.Equal(0xFF808080u, One(MatrixCoefficients.Bt601, ColorRange.Full, 8, PixelFormat.Rgba8, 128, 128, 128));
        Assert.Equal((3u << 30) | 1023u | (1023u << 10) | (1023u << 20), One(MatrixCoefficients.Bt2020NonConstant, ColorRange.Limited, 10, PixelFormat.Rgb10A2, 940, 512, 512));

        // BT.709 limited red, from the forward equations: Y = 16 + 219·0.2126 = 62.56, Cb = 128 − 112·0.2126/0.9278 = 102.34,
        // Cr = 240: the codes 63/102/240 come back as pure red within one code.
        var red = One(MatrixCoefficients.Bt709, ColorRange.Limited, 8, PixelFormat.Rgba8, 63, 102, 240);
        Assert.InRange((int)(red & 0xFF), 254, 255);
        Assert.InRange((int)((red >> 8) & 0xFF), 0, 1);
        Assert.InRange((int)((red >> 16) & 0xFF), 0, 1);
    }

    /// <summary>Wrong inputs are refused before anything is launched.</summary>
    [Fact]
    public void Misuse_is_refused()
    {
        var env = Media.RequireCuda();
        using var stream = CudaStream.Create(env.Context);
        using var rgb = CudaDeviceBuffer.AllocateImage(env.Context, 16, 16, PixelFormat.Rgba8);
        using var yuv = CudaDeviceBuffer.Allocate(env.Context, 16 * 24 * 2);
        var conversion = YuvConversion.Create(MatrixCoefficients.Bt709, ColorRange.Limited, 8, ChromaSiting.Left, PixelFormat.Rgba8);
        var planes = new YuvPlanes { Luma = yuv.Address, LumaPitch = 16, Chroma = yuv.Address + 256, ChromaPitch = 16, Width = 16, Height = 16, Format = YuvSampleFormat.Nv12 };
        env.Kernels.EnqueueYuvToRgb(stream, planes, rgb.AsFrame(), conversion);
        stream.Synchronize();
        Assert.Throws<ArgumentException>(() => env.Kernels.EnqueueYuvToRgb(stream, planes with { Format = YuvSampleFormat.P010 }, rgb.AsFrame(), conversion));
        Assert.Throws<ArgumentException>(() => env.Kernels.EnqueueYuvToRgb(stream, planes with { LumaPitch = 15 }, rgb.AsFrame(), conversion));
        Assert.Throws<ArgumentException>(() => env.Kernels.EnqueueYuvToRgb(stream, planes with { Chroma = 0 }, rgb.AsFrame(), conversion));
        Assert.Throws<ArgumentException>(() => env.Kernels.EnqueueYuvToRgb(stream, planes with { Width = 8 }, rgb.AsFrame(), conversion));
        Assert.Throws<ArgumentException>(() => env.Kernels.EnqueueYuvToRgb(stream, planes, rgb.AsFrame() with { Format = PixelFormat.Rgb10A2 }, conversion));
        Assert.Throws<ArgumentException>(() => env.Kernels.EnqueueYuvToRgb(stream, planes, rgb.AsFrame(), default));
        Assert.Throws<ArgumentException>(() => YuvConversion.Create(MatrixCoefficients.Identity, ColorRange.Limited, 8, ChromaSiting.Left, PixelFormat.Rgba8));
        Assert.Throws<ArgumentException>(() => YuvConversion.Create(MatrixCoefficients.Bt709, ColorRange.Unspecified, 8, ChromaSiting.Left, PixelFormat.Rgba8));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvConversion.Create(MatrixCoefficients.Bt709, ColorRange.Limited, 12, ChromaSiting.Left, PixelFormat.Rgba8));
        Assert.Throws<ArgumentException>(() => YuvConversion.Create(MatrixCoefficients.Bt709, ColorRange.Limited, 8, ChromaSiting.Left, PixelFormat.Bgra8));
    }

    /// <summary>
    /// Secondary to zscale above: the CPU reference against FFmpeg's swscale on a decoded test frame (software decode of an 8-bit BT.709 limited
    /// clip and a 10-bit BT.2020 one, as swscale reads them), with swscale told the same matrix, ranges and chroma siting
    /// and asked for accurate rounding and full chroma interpolation. swscale rounds and interpolates its own way, so this
    /// is a tolerance check: per channel, at most 2 codes (8-bit) or 6 codes (10-bit) apart, and under 0.5 / 1.5 codes on
    /// average. It guards the reference against a shared misreading of the standards (a wrong offset, range or matrix
    /// row would be tens of codes off). Measured: 8-bit agrees within 1 code (mean 0.001); swscale's 10-bit RGB
    /// (gbrp10le) runs about 3 codes low at the top of the range (limited white, 940, comes out as about 1020 where
    /// H.273 gives 1023), up to 4 codes (mean 1.3). Its rgb48 path is coarser still and is not used.
    /// </summary>
    [Theory]
    [InlineData("h264", 8)]
    [InlineData("hevc10-pq", 10)]
    public void The_cpu_reference_agrees_with_swscale_within_tolerance(string clip, int bits)
    {
        var env = Media.Require();
        var path = env.Clip(clip);
        var info = SoftwareFrame.Probe(env, path);
        var (y, cb, cr) = SoftwareFrame.Read(env, path, info.Width, info.Height, bits);
        var matrix = bits == 8 ? MatrixCoefficients.Bt709 : MatrixCoefficients.Bt2020NonConstant;
        var siting = bits == 8 ? ChromaSiting.Left : ChromaSiting.TopLeft;
        var output = bits == 8 ? PixelFormat.Rgba8 : PixelFormat.Rgb10A2;
        var ours = new uint[info.Width * info.Height];
        YuvReference.Create(matrix, ColorRange.Limited, bits, siting, output).Convert(y, cb, cr, info.Width, info.Height, ours);
        var swscale = SoftwareFrame.Swscale(env, path, info.Width, info.Height, bits, bits == 8 ? "bt709" : "bt2020", siting == ChromaSiting.Left ? "left" : "topleft");
        long max = 0;
        double sum = 0;
        var shift = bits == 8 ? 8 : 10;
        var mask = (1u << shift) - 1;
        for (var i = 0; i < ours.Length; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                var a = (ours[i] >> (c * shift)) & mask;
                var d = Math.Abs((long)a - swscale[(i * 3) + c]);
                max = Math.Max(max, d);
                sum += d;
            }
        }

        var mean = sum / (ours.Length * 3.0);

        Log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{clip}: reference vs swscale, max {max} codes, mean {mean:0.000} codes over {ours.Length} pixels"));
        Assert.True(max <= (bits == 8 ? 2 : 6), $"max difference {max} codes");
        Assert.True(mean < (bits == 8 ? 0.5 : 1.5), $"mean difference {mean:0.000} codes");
    }

    /// <summary>
    /// The CPU reference against zscale (the zimg library: the standards' exact coefficients in floating point, chroma
    /// upsampled bilinearly from its sited positions, rounded to nearest without dither), told the same matrix, ranges
    /// and chroma siting, on a decoded frame of each clip: 8-bit BT.709 and BT.601 limited, 8-bit BT.709 full, and
    /// 10-bit BT.2020 with top-left chroma. zimg upsamples an integer input's chroma in that integer format, rounding each
    /// interpolated sample to a whole code (on 10-bit input that alone is up to 2 codes of RGB, measured against exact float
    /// arithmetic). For the 10-bit clip a first zscale widens the input to 16 bits (zimg's own depth conversion, for
    /// limited range an exact shift by 6), so the interpolation keeps 6 more bits; that path is within 0.0002 codes of
    /// exact float arithmetic. (8-bit needs no widening: zimg interpolates 8-bit input in 16 bits already; and for full
    /// range the widening is not exact.) Against the reference zscale agrees to within one code (rounding at exact halves),
    /// with almost every value equal.
    /// </summary>
    [Theory]
    [InlineData("h264", 8, MatrixCoefficients.Bt709, ColorRange.Limited, ChromaSiting.Left, "709", "limited", "left")]
    [InlineData("h264-bt601", 8, MatrixCoefficients.Bt601, ColorRange.Limited, ChromaSiting.Left, "170m", "limited", "left")]
    [InlineData("h264-full", 8, MatrixCoefficients.Bt709, ColorRange.Full, ChromaSiting.Left, "709", "full", "left")]
    [InlineData("hevc10-pq", 10, MatrixCoefficients.Bt2020NonConstant, ColorRange.Limited, ChromaSiting.TopLeft, "2020_ncl", "limited", "topleft")]
    public void The_cpu_reference_agrees_with_zscale_to_one_code(
        string clip, int bits, MatrixCoefficients matrix, ColorRange range, ChromaSiting siting, string zMatrix, string zRange, string zChroma)
    {
        var env = Media.Require();
        var path = env.Clip(clip);
        var info = SoftwareFrame.Probe(env, path);
        var (y, cb, cr) = SoftwareFrame.Read(env, path, info.Width, info.Height, bits);
        var output = bits == 8 ? PixelFormat.Rgba8 : PixelFormat.Rgb10A2;
        var ours = new uint[info.Width * info.Height];
        YuvReference.Create(matrix, range, bits, siting, output).Convert(y, cb, cr, info.Width, info.Height, ours);
        var filter = (bits > 8 ? "zscale=dither=none,format=yuv420p16le," : string.Empty) +
                     $"zscale=matrixin={zMatrix}:rangein={zRange}:chromalin={zChroma}:range=full:filter=bilinear:dither=none,format={(bits == 8 ? "gbrp" : "gbrp10le")}";
        var zscale = SoftwareFrame.PlanarRgb(env, path, info.Width, info.Height, bits, filter);
        long max = 0, differing = 0;
        var shift = bits == 8 ? 8 : 10;
        var mask = (1u << shift) - 1;
        for (var i = 0; i < ours.Length; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                var d = Math.Abs((long)((ours[i] >> (c * shift)) & mask) - zscale[(i * 3) + c]);
                max = Math.Max(max, d);
                differing += d > 0 ? 1 : 0;
            }
        }

        var share = differing / (ours.Length * 3.0);
        Log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{clip}: reference vs zscale, max {max} codes, {share:P3} of the values differ"));
        Assert.True(max <= 1, $"max difference {max} codes");
        Assert.True(share < 0.001, $"{share:P3} of the values differ");
    }

    /// <summary>The exact oracle (rational arithmetic, closed-form inverse, explicit sample positions).</summary>
    private static class Oracle
    {
        public static uint Pixel(MatrixCoefficients matrix, ColorRange range, int bits, ChromaSiting siting, PixelFormat output,
            ushort[] y, ushort[] cb, ushort[] cr, int w, int h, int x, int yy)
        {
            var (kr, kb) = matrix switch
            {
                MatrixCoefficients.Bt601 => (R(299, 1000), R(114, 1000)),
                MatrixCoefficients.Bt709 => (R(2126, 10000), R(722, 10000)),
                _ => (R(2627, 10000), R(593, 10000)),
            };
            var kg = R(1) - kr - kb;
            BigRational luma, cbv, crv;
            var s = 1 << (bits - 8);
            var (cbCode, crCode) = (Chroma(cb, w, h, x, yy, siting), Chroma(cr, w, h, x, yy, siting));
            if (range == ColorRange.Limited)
            {
                luma = (R(y[(yy * w) + x]) - R(16 * s)) / R(219 * s);
                cbv = (cbCode - R(128 * s)) / R(224 * s);
                crv = (crCode - R(128 * s)) / R(224 * s);
            }
            else
            {
                var max = (1 << bits) - 1;
                luma = R(y[(yy * w) + x]) / R(max);
                cbv = (cbCode - R(1 << (bits - 1))) / R(max);
                crv = (crCode - R(1 << (bits - 1))) / R(max);
            }

            var red = luma + (R(2) * (R(1) - kr) * crv);
            var blue = luma + (R(2) * (R(1) - kb) * cbv);
            var green = (luma - (kr * red) - (kb * blue)) / kg;
            var m = output == PixelFormat.Rgb10A2 ? 1023 : 255;
            uint Q(BigRational v) => (uint)Math.Clamp((long)BigRational.FloorHalfUp(v * R(m)), 0, m);
            return output == PixelFormat.Rgb10A2
                ? Q(red) | (Q(green) << 10) | (Q(blue) << 20) | (3u << 30)
                : Q(red) | (Q(green) << 8) | (Q(blue) << 16) | (255u << 24);
        }

        /// <summary>Chroma at luma (x, y): samples sit at luma coordinates (ox + 2i, oy + 2j); linear in each axis, edges held.</summary>
        private static BigRational Chroma(ushort[] plane, int w, int h, int x, int y, ChromaSiting siting)
        {
            var (ox, oy) = siting switch
            {
                ChromaSiting.Left => (R(0), R(1, 2)),
                ChromaSiting.Center => (R(1, 2), R(1, 2)),
                ChromaSiting.TopLeft => (R(0), R(0)),
                ChromaSiting.Top => (R(1, 2), R(0)),
                ChromaSiting.BottomLeft => (R(0), R(1)),
                _ => (R(1, 2), R(1)),
            };
            var cw = (w + 1) / 2;
            var ch = (h + 1) / 2;
            var px = (R(x) - ox) / R(2); // position in chroma samples
            var py = (R(y) - oy) / R(2);
            var i = BigRational.Floor(px);
            var j = BigRational.Floor(py);
            var fx = px - R(i);
            var fy = py - R(j);
            BigRational At(BigInteger ci, BigInteger cj) => R(plane[((int)BigInteger.Clamp(cj, 0, ch - 1) * cw) + (int)BigInteger.Clamp(ci, 0, cw - 1)]);
            var top = ((R(1) - fx) * At(i, j)) + (fx * At(i + 1, j));
            var bottom = ((R(1) - fx) * At(i, j + 1)) + (fx * At(i + 1, j + 1));
            return ((R(1) - fy) * top) + (fy * bottom);
        }

        private static BigRational R(BigInteger n, BigInteger d) => new(n, d);

        private static BigRational R(BigInteger n) => new(n, 1);
    }

    private readonly record struct BigRational
    {
        public BigRational(BigInteger n, BigInteger d)
        {
            if (d.Sign < 0)
            {
                (n, d) = (-n, -d);
            }

            var g = BigInteger.GreatestCommonDivisor(n, d);
            (N, D) = g > 1 ? (n / g, d / g) : (n, d);
        }

        public BigInteger N { get; }

        public BigInteger D { get; }

        public static BigRational operator +(BigRational a, BigRational b) => new((a.N * b.D) + (b.N * a.D), a.D * b.D);

        public static BigRational operator -(BigRational a, BigRational b) => new((a.N * b.D) - (b.N * a.D), a.D * b.D);

        public static BigRational operator *(BigRational a, BigRational b) => new(a.N * b.N, a.D * b.D);

        public static BigRational operator /(BigRational a, BigRational b) => new(a.N * b.D, a.D * b.N);

        public static BigInteger Floor(BigRational v)
        {
            var q = BigInteger.DivRem(v.N, v.D, out var r);
            return r.Sign < 0 ? q - 1 : q;
        }

        /// <summary>floor(v + ½).</summary>
        public static BigInteger FloorHalfUp(BigRational v) => Floor(v + new BigRational(1, 2));
    }
}

/// <summary>Synthetic planes and the kernel run with guarded, pitched buffers.</summary>
internal static class Planes
{
    private const byte Guard = 0xA5;

    /// <summary>Random codes over the full sample range (8 or 10 bits), extremes forced into the first pixels.</summary>
    public static (ushort[] Y, ushort[] Cb, ushort[] Cr) Random(int w, int h, int bits, int seed)
    {
        var random = new Random(seed);
        var max = (1 << bits) - 1;
        var cw = (w + 1) / 2;
        var ch = (h + 1) / 2;
        var y = new ushort[w * h];
        var cb = new ushort[cw * ch];
        var cr = new ushort[cw * ch];
        for (var i = 0; i < y.Length; i++)
        {
            y[i] = (ushort)random.Next(max + 1);
        }

        for (var i = 0; i < cb.Length; i++)
        {
            cb[i] = (ushort)random.Next(max + 1);
            cr[i] = (ushort)random.Next(max + 1);
        }

        ushort[] extremes = [0, (ushort)max, (ushort)(16 << (bits - 8)), (ushort)(235 << (bits - 8)), (ushort)(240 << (bits - 8))];
        for (var i = 0; i < Math.Min(extremes.Length, y.Length); i++)
        {
            y[i] = extremes[i];
        }

        for (var i = 0; i < Math.Min(extremes.Length, cb.Length); i++)
        {
            cb[i] = extremes[i];
            cr[i] = extremes[extremes.Length - 1 - i];
        }

        return (y, cb, cr);
    }

    /// <summary>
    /// Uploads the planes as NV12 or P010 with a padded pitch and padding filled with guard bytes (which, read as samples,
    /// would change the result), converts into an output with guard bytes around it, checks the guards, returns the pixels.
    /// </summary>
    public static uint[] RunKernel(CudaEnvironment env, in YuvConversion conversion, ushort[] y, ushort[] cb, ushort[] cr, int w, int h)
    {
        var bps = conversion.SourceBits > 8 ? 2 : 1;
        var shift = conversion.SourceBits > 8 ? 16 - conversion.SourceBits : 0;
        var cw = (w + 1) / 2;
        var ch = (h + 1) / 2;
        var lumaPitch = (w * bps) + 24 * bps;
        var chromaPitch = (cw * 2 * bps) + 40 * bps;
        var lumaRows = h + 2;   // a guard row below
        var chromaRows = ch + 2;
        var host = new byte[(lumaPitch * lumaRows) + (chromaPitch * chromaRows)];
        Array.Fill(host, Guard);
        void Put(int offset, ushort value)
        {
            var v = (ushort)(value << shift);
            if (bps == 1)
            {
                host[offset] = (byte)v;
            }
            else
            {
                host[offset] = (byte)v;
                host[offset + 1] = (byte)(v >> 8);
            }
        }

        for (var r = 0; r < h; r++)
        {
            for (var x = 0; x < w; x++)
            {
                Put((r * lumaPitch) + (x * bps), y[(r * w) + x]);
            }
        }

        var chromaBase = lumaPitch * lumaRows;
        for (var r = 0; r < ch; r++)
        {
            for (var i = 0; i < cw; i++)
            {
                Put(chromaBase + (r * chromaPitch) + (2 * i * bps), cb[(r * cw) + i]);
                Put(chromaBase + (r * chromaPitch) + (((2 * i) + 1) * bps), cr[(r * cw) + i]);
            }
        }

        using var stream = CudaStream.Create(env.Context);
        using var input = CudaDeviceBuffer.Allocate(env.Context, host.Length);
        stream.CopyFromHost<byte>(host, input.Address);
        var planes = new YuvPlanes
        {
            Luma = input.Address,
            LumaPitch = lumaPitch,
            Chroma = input.Address + (ulong)chromaBase,
            ChromaPitch = chromaPitch,
            Width = w,
            Height = h,
            Format = bps == 2 ? YuvSampleFormat.P010 : YuvSampleFormat.Nv12,
        };

        // The output: 2 guard rows above and below, 16 guard bytes before each row and 20 after.
        var pitch = 16 + (w * 4) + 20;
        using var output = CudaDeviceBuffer.Allocate(env.Context, (long)pitch * (h + 4));
        stream.MemsetAsync(output.Address, Guard, output.SizeBytes);
        var frame = output.AsFrame(w, h, conversion.Output, pitch, offset: (2L * pitch) + 16);
        env.Kernels.EnqueueYuvToRgb(stream, planes, frame, conversion);
        var raw = new byte[output.SizeBytes];
        stream.CopyToHost<byte>(output.Address, raw);
        var pixels = new uint[w * h];
        for (long i = 0; i < raw.Length; i++)
        {
            var row = (i / pitch) - 2;
            var column = (i % pitch) - 16;
            var inside = row >= 0 && row < h && column >= 0 && column < w * 4;
            if (inside)
            {
                continue;
            }

            Assert.True(raw[i] == Guard, $"byte {i} outside the output (row {row}, byte {column}) was written: 0x{raw[i]:x2}");
        }

        for (var r = 0; r < h; r++)
        {
            for (var x = 0; x < w; x++)
            {
                pixels[(r * w) + x] = BitConverter.ToUInt32(raw, (int)(((r + 2) * pitch) + 16 + (x * 4)));
            }
        }

        return pixels;
    }

    public static string FirstDifference(ReadOnlySpan<uint> expected, ReadOnlySpan<uint> actual, int width)
    {
        var i = expected.CommonPrefixLength(actual);
        return i >= expected.Length ? "equal" : string.Create(CultureInfo.InvariantCulture, $"first difference at ({i % width}, {i / width}): expected 0x{expected[i]:x8}, got 0x{actual[i]:x8}");
    }
}

/// <summary>One frame of the ffmpeg CLI's software decode, and swscale's RGB of it.</summary>
internal static class SoftwareFrame
{
    public static (int Width, int Height) Probe(MediaEnvironment env, string path)
    {
        var (exit, output) = Tools.Run(env.Ffprobe ?? "ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", path]);
        Assert.True(exit == 0, output);
        var parts = output.Trim().Split(',');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>The first frame's planes as code values.</summary>
    public static (ushort[] Y, ushort[] Cb, ushort[] Cr) Read(MediaEnvironment env, string path, int w, int h, int bits)
    {
        var raw = Raw(env, path, ["-frames:v", "1", "-f", "rawvideo", "-pix_fmt", bits == 8 ? "yuv420p" : "yuv420p10le"]);
        var bps = bits == 8 ? 1 : 2;
        var cw = (w + 1) / 2;
        var ch = (h + 1) / 2;
        ushort At(int sample) => bps == 1 ? raw[sample] : BitConverter.ToUInt16(raw, sample * 2);
        var y = new ushort[w * h];
        var cb = new ushort[cw * ch];
        var cr = new ushort[cw * ch];
        for (var i = 0; i < y.Length; i++)
        {
            y[i] = At(i);
        }

        for (var i = 0; i < cb.Length; i++)
        {
            cb[i] = At(y.Length + i);
            cr[i] = At(y.Length + cb.Length + i);
        }

        return (y, cb, cr);
    }

    /// <summary>The first frame through <paramref name="filter"/>, which ends in planar gbrp / gbrp10le: R, G, B per pixel.</summary>
    public static uint[] PlanarRgb(MediaEnvironment env, string path, int w, int h, int bits, string filter)
    {
        var raw = Raw(env, path, ["-frames:v", "1", "-vf", filter, "-f", "rawvideo"]);
        var values = new uint[w * h * 3];
        var n = w * h;
        uint At(int plane, int i) => bits == 8 ? raw[(plane * n) + i] : BitConverter.ToUInt16(raw, 2 * ((plane * n) + i));
        for (var i = 0; i < n; i++)
        {
            values[(i * 3) + 1] = At(0, i); // G plane
            values[(i * 3) + 2] = At(1, i); // B plane
            values[(i * 3) + 0] = At(2, i); // R plane
        }

        return values;
    }

    /// <summary>swscale's full-range RGB of the first frame, per channel code values (R, G, B per pixel).</summary>
    public static uint[] Swscale(MediaEnvironment env, string path, int w, int h, int bits, string matrix, string chromaLocation)
    {
        // 8-bit: packed rgb24. 10-bit: planar gbrp10le (G, B, R planes of 10-bit samples), swscale's native 10-bit RGB.
        var filter = $"scale=in_color_matrix={matrix}:in_range=tv:out_range=pc:in_chroma_loc={chromaLocation}:flags=accurate_rnd+full_chroma_int+full_chroma_inp+bilinear,format={(bits == 8 ? "rgb24" : "gbrp10le")}";
        var raw = Raw(env, path, ["-frames:v", "1", "-vf", filter, "-f", "rawvideo"]);
        var values = new uint[w * h * 3];
        var n = w * h;
        for (var i = 0; i < n; i++)
        {
            if (bits == 8)
            {
                values[(i * 3) + 0] = raw[(i * 3) + 0];
                values[(i * 3) + 1] = raw[(i * 3) + 1];
                values[(i * 3) + 2] = raw[(i * 3) + 2];
            }
            else
            {
                values[(i * 3) + 1] = BitConverter.ToUInt16(raw, 2 * i);              // G plane
                values[(i * 3) + 2] = BitConverter.ToUInt16(raw, 2 * (n + i));        // B plane
                values[(i * 3) + 0] = BitConverter.ToUInt16(raw, 2 * ((2 * n) + i));  // R plane
            }
        }

        return values;
    }

    private static byte[] Raw(MediaEnvironment env, string path, string[] args)
    {
        var output = Path.Combine(Clips.Directory, $"raw-{Guid.NewGuid():N}.bin");
        try
        {
            var (exit, log) = Tools.Run(env.Ffmpeg!, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", path, .. args, output]);
            Assert.True(exit == 0, log);
            return System.IO.File.ReadAllBytes(output);
        }
        finally
        {
            System.IO.File.Delete(output);
        }
    }
}
