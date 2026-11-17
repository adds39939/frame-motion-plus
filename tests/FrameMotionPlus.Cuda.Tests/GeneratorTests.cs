using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

[Trait("Category", "Gpu")]
public class GeneratorTests
{
    private const int Width = 320;
    private const int Height = 180;

    public static TheoryData<PixelFormat> Formats => [PixelFormat.Rgba8, PixelFormat.Bgra8, PixelFormat.Rgb10A2];

    [Theory]
    [MemberData(nameof(Formats))]
    public void Blend_generator_writes_the_blend_and_signals_done(PixelFormat format)
    {
        var env = Gpu.Require();
        using var setup = CudaStream.Create(env.Context);
        using var a = env.Image(Width, Height, format);
        using var b = env.Image(Width, Height, format);
        using var o = env.Image(Width, Height, format);
        using var done = CudaEvent.Create(env.Context);
        env.Kernels.EnqueueTestPattern(setup, a.AsFrame(), 10);
        env.Kernels.EnqueueTestPattern(setup, b.AsFrame(), 11);
        setup.Synchronize();

        using IFrameGenerator generator = new BlendGenerator(env.Context, env.Kernels);
        Assert.Equal(MemoryDomain.CudaDevice, generator.Capabilities.Domain);
        Assert.True(generator.Capabilities.Formats.Contains(format));
        generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = format });
        foreach (var t in new[] { 0.5f, 0.3f, 0f, 1f })
        {
            generator.Enqueue(a.AsFrame(), b.AsFrame(), t, o.AsFrame(), done.Signal);
            done.Synchronize();
            var expected = Reference.Blend(Reference.Pattern(Width, Height, 10, format), Reference.Pattern(Width, Height, 11, format), Reference.BlendWeight(t), format);
            var actual = env.Read(setup, o.AsFrame());
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"t={t}: {Reference.FirstDifference(expected, actual, Width)}");
        }

        var blend = (BlendGenerator)generator;
        var spans = 0;
        while (blend.TryReadGpuTime(out var ns))
        {
            Assert.InRange(ns, 0, 50_000_000);
            spans++;
        }

        Assert.Equal(4, spans);
        Assert.Equal(blend.Stream.Priority, env.Context.StreamPriorityRange.Greatest);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Repeat_generator_shows_the_nearest_frame(PixelFormat format)
    {
        var env = Gpu.Require();
        using var setup = CudaStream.Create(env.Context);
        using var a = env.Image(Width, Height, format);
        using var b = env.Image(Width, Height, format);
        using var o = env.PaddedImage(Width, Height, 64, out var output, format);
        env.Kernels.EnqueueTestPattern(setup, a.AsFrame(), 1);
        env.Kernels.EnqueueTestPattern(setup, b.AsFrame(), 2);
        setup.Synchronize();
        using var generator = new RepeatGenerator(env.Context, setup);
        Assert.Same(setup, generator.Stream);
        generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = format });

        foreach (var (t, frame) in new[] { (0.1f, 1u), (0.49f, 1u), (0.5f, 2u), (0.9f, 2u), (0f, 1u), (1f, 2u) })
        {
            generator.Enqueue(a.AsFrame(), b.AsFrame(), t, output, GpuSignal.None);
            var actual = env.Read(setup, output);
            Assert.Equal(frame, Reference.DecodeCounter(actual, Width, Height, format));
            Assert.Equal(Reference.Pattern(Width, Height, frame, format), actual);
            env.AssertGuardsIntact(setup, o, output);
        }
    }

    /// <summary>
    /// Core's "the next pair" (review fix): after <see cref="IFrameGenerator.NotifyDiscontinuity"/> every frame of the pair
    /// of the next call is the current frame (2x: one t, 4x: three), whatever t, so the picture switches once and never
    /// steps back; t = 0 is the previous frame and does not use the notification up; a different pair ends it. The old
    /// generators applied it to one call only (the blend then mixed across the cut, the repeat stepped back to prev).
    /// </summary>
    [Theory]
    [InlineData("blend", 2)]
    [InlineData("blend", 4)]
    [InlineData("repeat", 2)]
    [InlineData("repeat", 4)]
    public void A_discontinuity_covers_every_frame_of_the_next_pair(string kind, int multiplier)
    {
        var env = Gpu.Require();
        using var setup = CudaStream.Create(env.Context);
        using var a = env.Image(Width, Height, PixelFormat.Rgba8);
        using var b = env.Image(Width, Height, PixelFormat.Rgba8);
        using var c = env.Image(Width, Height, PixelFormat.Rgba8);
        using var o = env.Image(Width, Height, PixelFormat.Rgba8);
        env.Kernels.EnqueueTestPattern(setup, a.AsFrame(), 100);
        env.Kernels.EnqueueTestPattern(setup, b.AsFrame(), 200);
        env.Kernels.EnqueueTestPattern(setup, c.AsFrame(), 300);
        setup.Synchronize();
        using CudaFrameGenerator generator = kind == "blend" ? new BlendGenerator(env.Context, env.Kernels, setup) : new RepeatGenerator(env.Context, setup);
        generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 });
        Assert.False(generator.Capabilities.EnqueueMayBlock);
        var prev = Reference.Pattern(Width, Height, 100, PixelFormat.Rgba8);
        var curr = Reference.Pattern(Width, Height, 200, PixelFormat.Rgba8);
        uint[] Run(GpuFrame p, GpuFrame q, float t)
        {
            generator.Enqueue(p, q, t, o.AsFrame(), GpuSignal.None);
            return env.Read(setup, o.AsFrame());
        }

        float[] timesteps = multiplier == 2 ? [0.5f] : [0.25f, 0.5f, 0.75f];

        // Every generated frame of the pair across the cut, twice over (the pair may be revisited), is curr.
        generator.NotifyDiscontinuity();
        foreach (var t in timesteps.Concat(timesteps))
        {
            Assert.True(curr.AsSpan().SequenceEqual(Run(a.AsFrame(), b.AsFrame(), t)), $"{kind} {multiplier}x t={t} after the cut is not curr");
        }

        // t = 0 is prev itself and does not use the notification up: the pair's next generated frame is still curr.
        generator.NotifyDiscontinuity();
        Assert.True(prev.AsSpan().SequenceEqual(Run(a.AsFrame(), b.AsFrame(), 0f)), $"{kind} t=0 after the cut is not prev");
        Assert.True(curr.AsSpan().SequenceEqual(Run(a.AsFrame(), b.AsFrame(), 0.25f)), $"{kind} t=0.25 after a t=0 call is not curr");

        // The next pair is continuous again.
        var next = Run(b.AsFrame(), c.AsFrame(), 0.25f);
        var expected = kind == "blend"
            ? Reference.Blend(curr, Reference.Pattern(Width, Height, 300, PixelFormat.Rgba8), Reference.BlendWeight(0.25f), PixelFormat.Rgba8)
            : curr; // the repeat shows its prev (frame 200) at t = 0.25
        Assert.True(expected.AsSpan().SequenceEqual(next), $"{kind}: the pair after the cut pair is not interpolated normally");
        var blendLike = Run(a.AsFrame(), b.AsFrame(), 0.25f);
        Assert.False(kind == "blend" && curr.AsSpan().SequenceEqual(blendLike), "the old pair revisited after a new one is still treated as cut");
    }

    [Fact]
    public void Enqueue_rejects_bad_arguments_with_clear_exceptions()
    {
        var env = Gpu.Require();
        using var a = env.Image(Width, Height, PixelFormat.Rgba8);
        using var other = env.Image(Width, Height, PixelFormat.Rgb10A2);
        using var generator = new BlendGenerator(env.Context, env.Kernels);
        var f = a.AsFrame();

        Assert.Contains("Configure", Assert.Throws<InvalidOperationException>(() => generator.Enqueue(f, f, 0.5f, f, GpuSignal.None)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = PixelFormat.Unknown }));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Configure(new FrameGenConfig { Width = 0, Height = Height, Format = PixelFormat.Rgba8 }));
        generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 });

        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Enqueue(f, f, -0.1f, f, GpuSignal.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Enqueue(f, f, 1.5f, f, GpuSignal.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Enqueue(f, f, float.NaN, f, GpuSignal.None));
        Assert.Equal("curr", Assert.Throws<ArgumentException>(() => generator.Enqueue(f, other.AsFrame(), 0.5f, f, GpuSignal.None)).ParamName);
        Assert.Equal("output", Assert.Throws<ArgumentException>(() => generator.Enqueue(f, f, 0.5f, f with { Width = Width - 1 }, GpuSignal.None)).ParamName);
        Assert.Equal("prev", Assert.Throws<ArgumentException>(() => generator.Enqueue(f with { Domain = MemoryDomain.Vulkan }, f, 0.5f, f, GpuSignal.None)).ParamName);
        Assert.Equal("prev", Assert.Throws<ArgumentException>(() => generator.Enqueue(f with { SizeBytes = 100 }, f, 0.5f, f, GpuSignal.None)).ParamName);

        // A signal that belongs to another API is refused, not handed to CUDA.
        var vulkan = GpuSignal.Timeline(MemoryDomain.Vulkan, 1234, 5);
        Assert.Throws<ArgumentException>(() => generator.Enqueue(f, f, 0.5f, f, vulkan));
        Assert.Throws<ArgumentException>(() => generator.Enqueue(f with { Ready = vulkan }, f, 0.5f, f, GpuSignal.None));
        Assert.Throws<ArgumentException>(() => generator.Enqueue(f, f, 0.5f, f, GpuSignal.Timeline(1, 1)));

        generator.Stream.Synchronize();
        generator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => generator.Enqueue(f, f, 0.5f, f, GpuSignal.None));
    }

    [Fact]
    public void A_failed_enqueue_leaves_the_gpu_timer_closed_and_the_generator_usable()
    {
        var env = Gpu.Require();
        using var a = env.Image(Width, Height, PixelFormat.Rgba8);
        using var b = env.Image(Width, Height, PixelFormat.Rgba8);
        using var o = env.Image(Width, Height, PixelFormat.Rgba8);
        var kernels = Kernels.ImageKernels.Load(env.Context);
        using var generator = new BlendGenerator(env.Context, kernels);
        generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 });
        using var setup = CudaStream.Create(env.Context);
        env.Kernels.EnqueueTestPattern(setup, b.AsFrame(), 9);
        setup.Synchronize();
        kernels.Dispose();

        // The blend fails inside the timed span (its module is gone). Before the fix the span stayed open and every
        // later call failed with "Begin while a span is open" instead.
        Assert.Throws<ObjectDisposedException>(() => generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, o.AsFrame(), GpuSignal.None));
        Assert.Throws<ObjectDisposedException>(() => generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, o.AsFrame(), GpuSignal.None));
        generator.Stream.Synchronize();
        Assert.False(generator.TryReadGpuTime(out _));

        // The discontinuity path needs no kernels: the generator still works and times it.
        generator.NotifyDiscontinuity();
        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, o.AsFrame(), GpuSignal.None);
        Assert.Equal(Reference.Pattern(Width, Height, 9, PixelFormat.Rgba8), env.Read(generator.Stream, o.AsFrame()));
        Assert.True(generator.TryReadGpuTime(out _));
    }

    [Fact]
    public void A_failed_enqueue_keeps_the_discontinuity_for_the_rest_of_the_pair()
    {
        var env = Gpu.Require();
        using var a = env.Image(Width, Height, PixelFormat.Rgba8);
        using var generator = new ScriptedGenerator(env.Context);
        generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 });
        var f = a.AsFrame();

        generator.NotifyDiscontinuity();
        generator.FailNext = true;
        Assert.Throws<InvalidOperationException>(() => generator.Enqueue(f, f, 0.5f, f, GpuSignal.None));
        generator.Enqueue(f, f, 0.5f, f, GpuSignal.None); // the cut was not lost by the failed call
        generator.Enqueue(f, f, 0.5f, f, GpuSignal.None); // and covers the whole pair (Core's "next pair")
        generator.Enqueue(f, f, 0f, f, GpuSignal.None);   // except t = 0, which is prev itself
        generator.Enqueue(f with { ContentTimeNs = 1 }, f, 0.5f, f, GpuSignal.None); // another pair: continuous
        Assert.Equal([true, true, true, false, false], generator.Seen);
        generator.Stream.Synchronize();
    }

    /// <summary>Records the discontinuity flag each call sees and fails on request, inside the timed span.</summary>
    private sealed class ScriptedGenerator(CudaContext context) : CudaFrameGenerator(context, null, CudaStreamPriority.Normal)
    {
        public bool FailNext { get; set; }

        public List<bool> Seen { get; } = [];

        public override FrameGenCapabilities Capabilities { get; } = new()
        {
            MaxMultiplier = 8,
            SupportsArbitraryTimestep = true,
            Formats = PixelFormatSet.All,
            Domain = MemoryDomain.CudaDevice,
        };

        private protected override void EnqueueWork(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, bool discontinuity)
        {
            Seen.Add(discontinuity);
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("scripted failure");
            }
        }
    }

    [Fact]
    public void Kernel_helpers_reject_mismatched_frames()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var a = env.Image(64, 64, PixelFormat.Rgba8);
        using var b = env.Image(64, 64, PixelFormat.Rgb10A2);
        var f = a.AsFrame();
        Assert.Throws<ArgumentException>(() => env.Kernels.EnqueueBlend(stream, f, b.AsFrame(), 0.5f, f));
        Assert.Throws<ArgumentOutOfRangeException>(() => env.Kernels.EnqueueBlend(stream, f, f, 2f, f));
        Assert.Throws<ArgumentOutOfRangeException>(() => env.Kernels.EnqueueBlendWeight(stream, f, f, 65537, f));
        Assert.Throws<ArgumentException>(() => env.Kernels.EnqueueConvert(stream, f, f));
        Assert.Throws<ArgumentException>(() => env.Kernels.EnqueueConvert(stream, f, b.AsFrame() with { Width = 32 }));
        Assert.Throws<ArgumentException>(() => stream.CopyFrameAsync(f, b.AsFrame()));
        Assert.Equal(0u, ImageKernels.BlendWeight(float.NaN));
        Assert.Equal(65536u, ImageKernels.BlendWeight(float.PositiveInfinity));
    }
}
