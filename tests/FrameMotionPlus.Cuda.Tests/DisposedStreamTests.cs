using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>
/// A disposed stream's handle is zero, which the driver reads as the legacy default stream: work enqueued through a
/// disposed stream would silently run there. Every call that takes a stream must throw instead.
/// </summary>
[Trait("Category", "Gpu")]
public class DisposedStreamTests
{
    private const int Width = 64;
    private const int Height = 40;

    [Fact]
    public unsafe void Every_call_that_takes_a_disposed_stream_throws_and_nothing_runs_on_the_default_stream()
    {
        var env = Gpu.Require();
        using var target = env.Image(Width, Height, PixelFormat.Rgba8);
        using var other = env.Image(Width, Height, PixelFormat.Rgba8);
        using var tenBit = env.Image(Width, Height, PixelFormat.Rgb10A2);
        using var e = CudaEvent.Create(env.Context);
        using var timer = CudaSpanTimer.Create(env.Context);
        using var ring = CudaSpanTimerRing.Create(env.Context, 4);
        using var module = CudaModule.Load(env.Context, ImageKernels.GetEmbeddedPtx("util"), "util");
        var kernel = module.GetKernel("fmp_delay_ns");
        var frame = target.AsFrame();
        var host = new uint[Width * Height];
        var stream = CudaStream.Create(env.Context);
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Handle);
        Assert.Throws<ObjectDisposedException>(() => env.Kernels.EnqueueTestPattern(stream, frame, 1));
        Assert.Throws<ObjectDisposedException>(() => env.Kernels.EnqueueBlend(stream, frame, frame, 0.5f, frame));
        Assert.Throws<ObjectDisposedException>(() => env.Kernels.EnqueueBlendWeight(stream, frame, frame, 7, frame));
        Assert.Throws<ObjectDisposedException>(() => env.Kernels.EnqueueConvert(stream, frame, tenBit.AsFrame()));
        Assert.Throws<ObjectDisposedException>(() => env.Kernels.EnqueueDelay(stream, 1000));
        Assert.Throws<ObjectDisposedException>(() =>
        {
            ulong ns = 0;
            var args = stackalloc void*[1] { &ns };
            kernel.Launch(stream, new CudaDim3(1), new CudaDim3(1), 0, args);
        });
        Assert.Throws<ObjectDisposedException>(() => e.Record(stream));
        Assert.Throws<ObjectDisposedException>(() => timer.Begin(stream));
        Assert.Throws<ObjectDisposedException>(() => ring.Begin(stream));
        Assert.Throws<ObjectDisposedException>(() => stream.Wait(e));
        Assert.Throws<ObjectDisposedException>(() => stream.Wait(e.Signal));
        Assert.Throws<ObjectDisposedException>(() => stream.Signal(e.Signal));
        Assert.Throws<ObjectDisposedException>(() => stream.Wait(GpuSignal.None));
        Assert.Throws<ObjectDisposedException>(() => stream.Signal(GpuSignal.None));
        Assert.Throws<ObjectDisposedException>(() => stream.Synchronize());
        Assert.Throws<ObjectDisposedException>(() => stream.IsIdle);
        Assert.Throws<ObjectDisposedException>(() => stream.CopyAsync(other.Address, target.Address, 4));
        Assert.Throws<ObjectDisposedException>(() => stream.CopyFrameAsync(frame, other.AsFrame()));
        Assert.Throws<ObjectDisposedException>(() => stream.Copy2DAsync(other.Address, 256, target.Address, 256, 256, 2));
        Assert.Throws<ObjectDisposedException>(() => stream.CopyFrameToHost(frame, host));
        Assert.Throws<ObjectDisposedException>(() => stream.CopyFrameFromHost(host, frame));
        Assert.Throws<ObjectDisposedException>(() => stream.CopyFromHost<uint>(host, target.Address));
        Assert.Throws<ObjectDisposedException>(() => stream.CopyToHost<uint>(target.Address, host));
        Assert.Throws<ObjectDisposedException>(() => stream.MemsetAsync(target.Address, 1, 4));
        Assert.Throws<ObjectDisposedException>(() => stream.FillFrameAsync(frame, 1));
        Assert.Throws<ObjectDisposedException>(() => CudaExternalSemaphore.SignalMany(stream, new CudaExternalSemaphore[1], [1ul]));
        Assert.Throws<ObjectDisposedException>(() => CudaExternalSemaphore.WaitMany(stream, new CudaExternalSemaphore[1], [1ul]));
        Assert.Throws<ObjectDisposedException>(() => new BlendGenerator(env.Context, env.Kernels, stream));
        Assert.Throws<ObjectDisposedException>(() => new RepeatGenerator(env.Context, stream));

        // The failed calls left the timers usable, and nothing reached the default stream.
        using var live = CudaStream.Create(env.Context);
        ring.Begin(live);
        ring.End(live);
        timer.Begin(live);
        timer.End(live);
        env.Context.Synchronize();
        Assert.All(env.Read(live, frame), p => Assert.Equal(0u, p));
        Assert.True(e.IsComplete);
    }

    [Theory]
    [InlineData("blend")]
    [InlineData("repeat")]
    public void A_generator_whose_stream_was_disposed_refuses_to_enqueue(string kind)
    {
        var env = Gpu.Require();
        using var a = env.Image(Width, Height, PixelFormat.Rgba8);
        using var o = env.Image(Width, Height, PixelFormat.Rgba8);
        var stream = CudaStream.Create(env.Context);
        using IFrameGenerator generator = kind == "blend" ? new BlendGenerator(env.Context, env.Kernels, stream) : new RepeatGenerator(env.Context, stream);
        generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 });
        using var setup = CudaStream.Create(env.Context);
        env.Kernels.EnqueueTestPattern(setup, a.AsFrame(), 3);
        setup.Synchronize();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => generator.Enqueue(a.AsFrame(contentTimeNs: 1), a.AsFrame(contentTimeNs: 2), 0.7f, o.AsFrame(), GpuSignal.None));
        env.Context.Synchronize();
        Assert.All(env.Read(setup, o.AsFrame()), p => Assert.Equal(0u, p));
    }

    [Fact]
    public void A_disposed_event_is_refused_wherever_it_is_taken()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var other = CudaEvent.CreateForTiming(env.Context);
        var e = CudaEvent.CreateForTiming(env.Context);
        e.Dispose();
        Assert.Throws<ObjectDisposedException>(() => e.Handle);
        Assert.Throws<ObjectDisposedException>(() => e.Signal);
        Assert.Throws<ObjectDisposedException>(() => stream.Wait(e));
        Assert.Throws<ObjectDisposedException>(() => e.Record(stream));
        Assert.Throws<ObjectDisposedException>(() => e.Synchronize());
        Assert.Throws<ObjectDisposedException>(() => CudaEvent.TryGetElapsedNanoseconds(e, other, out _));
    }
}
