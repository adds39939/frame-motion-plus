using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Interop;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

[Trait("Category", "Gpu")]
public class KernelTests
{
    public static TheoryData<string> Modules => [.. ImageKernels.ModuleNames];

    [Theory]
    [MemberData(nameof(Modules))]
    public unsafe void Every_embedded_module_loads_with_cuModuleLoadData(string module)
    {
        var env = Gpu.Require();
        var ptx = ImageKernels.GetEmbeddedPtx(module);
        Assert.StartsWith("//", System.Text.Encoding.ASCII.GetString(ptx, 0, 2), StringComparison.Ordinal);
        byte[] terminated = [.. ptx, 0];
        env.Context.EnsureCurrent();
        nint handle;
        fixed (byte* p = terminated)
        {
            Assert.Equal(CudaResult.Success, CuNative.cuModuleLoadData(&handle, p));
        }

        Assert.Equal(CudaResult.Success, CuNative.cuModuleUnload(handle));
    }

    [Fact]
    public void Kernels_report_their_ptx_version_and_limits()
    {
        var env = Gpu.Require();
        using var module = CudaModule.Load(env.Context, ImageKernels.GetEmbeddedPtx("blend"), "blend");
        var kernel = module.GetKernel("fmp_blend_rgb10a2");
        Assert.True(kernel.IsValid);
        Assert.Equal(75, kernel.PtxVersion); // compute_75, the virtual architecture build.sh targets
        Assert.True(kernel.MaxThreadsPerBlock >= 256);
        Assert.InRange(kernel.RegisterCount, 1, 64);
        module.Dispose();
        Assert.False(kernel.IsValid);
        Assert.Throws<ObjectDisposedException>(() => kernel.MaxThreadsPerBlock);
    }

    public static TheoryData<int, int, int, PixelFormat, uint> PatternCases => new()
    {
        { 64, 48, 0, PixelFormat.Rgba8, 0 },
        { 64, 48, 0, PixelFormat.Rgb10A2, 0 },
        { 257, 131, 20, PixelFormat.Rgba8, 1 },
        { 257, 131, 20, PixelFormat.Rgb10A2, 12345 },
        { 257, 131, 20, PixelFormat.Bgra8, 77 },
        { 1920, 1080, 0, PixelFormat.Rgba8, 0xDEADBEEF },
        { 2560, 1440, 0, PixelFormat.Rgb10A2, 1_000_003 },
        { 31, 7, 4, PixelFormat.Rgb10A2, 3 },
        { 1, 1, 0, PixelFormat.Rgba8, 9 },
    };

    [Theory]
    [MemberData(nameof(PatternCases))]
    public void Test_pattern_is_bit_exact_against_the_cpu_reference(int width, int height, int extraPitch, PixelFormat format, uint frame)
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var buffer = env.PaddedImage(width, height, extraPitch, out var target, format);
        env.Kernels.EnqueueTestPattern(stream, target, frame);
        var actual = env.Read(stream, target);
        var expected = Reference.Pattern(width, height, frame, format);
        Assert.True(expected.AsSpan().SequenceEqual(actual), Reference.FirstDifference(expected, actual, width));

        // Nothing outside the image was written: rows above and below, bytes before and after each row.
        env.AssertGuardsIntact(stream, buffer, target);

        if (width >= 64)
        {
            Assert.Equal(frame, Reference.DecodeCounter(actual, width, height, format));
        }
    }

    [Fact]
    public void Test_pattern_levels_are_the_documented_pq_codes()
    {
        Assert.Equal(Reference.Bar10, Reference.PqCode(203));
        Assert.Equal(Reference.Box10, Reference.PqCode(1000));
        Assert.Equal(Reference.Background10, Reference.PqCode(10));
        Assert.Equal([0u, 64, 153, 307, 520, 594, 668, 769, 923, 1023], Reference.StepCodes);
    }

    [Fact]
    public void Test_pattern_box_moves_and_counter_counts()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var buffer = env.Image(960, 540, PixelFormat.Rgba8);
        var frame = buffer.AsFrame();
        var boxRow = 540 * 8 / 12;
        int? previousX = null;
        for (uint n = 0; n < 4; n++)
        {
            env.Kernels.EnqueueTestPattern(stream, frame, n);
            var pixels = env.Read(stream, frame);
            Assert.Equal(n, Reference.DecodeCounter(pixels, 960, 540, PixelFormat.Rgba8));
            var x = Array.IndexOf(pixels, Reference.Pack8(255, 255, 255, 255), boxRow * 960) - (boxRow * 960);
            Assert.InRange(x, 0, 959);
            if (previousX is { } p)
            {
                Assert.Equal(960 / 240, x - p);
            }

            previousX = x;
        }
    }

    public static TheoryData<PixelFormat, float> BlendCases
    {
        get
        {
            var data = new TheoryData<PixelFormat, float>();
            foreach (var format in new[] { PixelFormat.Rgba8, PixelFormat.Rgb10A2, PixelFormat.Bgra8 })
            {
                foreach (var t in new[] { 0f, 1f, 0.5f, 0.25f, 0.75f, 1f / 3, 0.1f, 1e-6f, 1f - 1e-6f, 1f / 65536, 0.999f })
                {
                    data.Add(format, t);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BlendCases))]
    public void Blend_is_bit_exact_against_the_cpu_reference(PixelFormat format, float t)
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        const int Width = 301, Height = 97;
        using var a = env.PaddedImage(Width, Height, 12, out var prev, format, GpuEnvironment.InputGuard);
        using var b = env.PaddedImage(Width, Height, 28, out var curr, format, GpuEnvironment.SecondInputGuard);
        using var o = env.PaddedImage(Width, Height, 44, out var output, format);
        var prevPixels = Reference.Noise(Width * Height, 10);
        var currPixels = Reference.Noise(Width * Height, 11);
        stream.CopyFrameFromHost(prevPixels, prev);
        stream.CopyFrameFromHost(currPixels, curr);

        env.Kernels.EnqueueBlend(stream, prev, curr, t, output);
        var actual = env.Read(stream, output);
        var weight = Reference.BlendWeight(t);
        Assert.Equal(weight, ImageKernels.BlendWeight(t));
        var expected = Reference.Blend(prevPixels, currPixels, weight, format);
        Assert.True(expected.AsSpan().SequenceEqual(actual), Reference.FirstDifference(expected, actual, Width));
        env.AssertGuardsIntact(stream, o, output);
        env.AssertGuardsIntact(stream, a, prev, GpuEnvironment.InputGuard);
        env.AssertGuardsIntact(stream, b, curr, GpuEnvironment.SecondInputGuard);

        if (t == 0f)
        {
            Assert.Equal(prevPixels, actual);
        }
        else if (t == 1f)
        {
            Assert.Equal(currPixels, actual);
        }
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void Blend_rounds_the_exact_value_half_up(double t)
    {
        // At these t the fixed-point weight is exact, so the kernel must equal floor((1 - t)·a + t·b + 0.5) computed
        // exactly (doubles hold these sums without rounding). All 1024 × 1024 10-bit pairs and 256 × 256 8-bit pairs.
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        foreach (var (format, max) in new[] { (PixelFormat.Rgb10A2, 1023u), (PixelFormat.Rgba8, 255u) })
        {
            var side = (int)max + 1;
            using var a = env.Image(side, side, format);
            using var b = env.Image(side, side, format);
            using var o = env.Image(side, side, format);
            var prev = new uint[side * side];
            var curr = new uint[side * side];
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    prev[(y * side) + x] = Reference.Pack((uint)x, (uint)y, max - (uint)x, format == PixelFormat.Rgb10A2 ? (uint)(x & 3) : (uint)x, format);
                    curr[(y * side) + x] = Reference.Pack((uint)y, (uint)x, (uint)y, format == PixelFormat.Rgb10A2 ? (uint)(y & 3) : (uint)y, format);
                }
            }

            stream.CopyFrameFromHost(prev, a.AsFrame());
            stream.CopyFrameFromHost(curr, b.AsFrame());
            env.Kernels.EnqueueBlend(stream, a.AsFrame(), b.AsFrame(), (float)t, o.AsFrame());
            var actual = env.Read(stream, o.AsFrame());
            for (var i = 0; i < actual.Length; i++)
            {
                var (pr, pg, pb, pa) = Reference.Unpack(prev[i], format);
                var (cr, cg, cb, ca) = Reference.Unpack(curr[i], format);
                var expected = Reference.Pack(Exact(pr, cr), Exact(pg, cg), Exact(pb, cb), Exact(pa, ca), format);
                Assert.True(expected == actual[i], $"{format} t={t} pixel {i}: expected 0x{expected:x8}, got 0x{actual[i]:x8}");
            }
        }

        uint Exact(uint p, uint c) => (uint)Math.Floor(((1 - t) * p) + (t * c) + 0.5);
    }

    public static TheoryData<PixelFormat, PixelFormat> ConvertCases => new()
    {
        { PixelFormat.Rgba8, PixelFormat.Rgb10A2 },
        { PixelFormat.Bgra8, PixelFormat.Rgb10A2 },
        { PixelFormat.Rgb10A2, PixelFormat.Rgba8 },
        { PixelFormat.Rgb10A2, PixelFormat.Bgra8 },
        { PixelFormat.Rgba8, PixelFormat.Bgra8 },
        { PixelFormat.Bgra8, PixelFormat.Rgba8 },
    };

    [Theory]
    [MemberData(nameof(ConvertCases))]
    public void Convert_is_bit_exact_and_rounds_to_nearest(PixelFormat from, PixelFormat to)
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        const int Width = 1024, Height = 33;
        using var s = env.PaddedImage(Width, Height, 8, out var source, from, GpuEnvironment.InputGuard);
        using var d = env.PaddedImage(Width, Height, 24, out var destination, to);
        var pixels = Reference.Noise(Width * Height, 20);
        for (uint i = 0; i < 1024; i++)
        {
            pixels[i] = from == PixelFormat.Rgb10A2 ? Reference.Pack10(i, 1023 - i, i, i & 3) : Reference.Pack8(i & 255, 255 - (i & 255), i >> 2, i & 255);
        }

        stream.CopyFrameFromHost(pixels, source);
        env.Kernels.EnqueueConvert(stream, source, destination);
        var actual = env.Read(stream, destination);
        var expected = Reference.Convert(pixels, from, to);
        Assert.True(expected.AsSpan().SequenceEqual(actual), Reference.FirstDifference(expected, actual, Width));
        env.AssertGuardsIntact(stream, d, destination);
        env.AssertGuardsIntact(stream, s, source, GpuEnvironment.InputGuard);
    }

    [Fact]
    public void Conversion_formulas_round_to_nearest_and_invert()
    {
        for (uint c = 0; c < 256; c++)
        {
            Assert.Equal((uint)Math.Round(c * 1023.0 / 255.0), Reference.To10(c));
            Assert.Equal(c, Reference.To8(Reference.To10(c)));
        }

        for (uint c = 0; c < 1024; c++)
        {
            Assert.Equal((uint)Math.Round(c * 255.0 / 1023.0), Reference.To8(c));
        }
    }
}
