using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

[Trait("Category", "Gpu")]
public class DisposalTests
{
    [Fact]
    public void Disposing_the_context_first_keeps_children_usable_until_they_are_disposed()
    {
        Gpu.Require();
        var context = CudaContext.RetainPrimary(0);
        var stream = CudaStream.Create(context);
        var buffer = CudaDeviceBuffer.AllocateImage(context, 64, 32, PixelFormat.Rgba8);
        var e = CudaEvent.Create(context);
        var kernels = ImageKernels.Load(context);
        var generator = new BlendGenerator(context, kernels);
        var pinned = CudaPinnedBuffer.Allocate(context, 4096);

        context.Dispose();
        Assert.True(context.IsDisposed);
        Assert.NotEqual(0, context.Handle); // still retained by its children
        Assert.Throws<ObjectDisposedException>(() => CudaStream.Create(context));

        kernels.EnqueueTestPattern(stream, buffer.AsFrame(), 4);
        e.Record(stream);
        e.Synchronize();
        var pixels = new uint[64 * 32];
        stream.CopyFrameToHost(buffer.AsFrame(), pixels);
        Assert.Equal(Reference.Pattern(64, 32, 4, PixelFormat.Rgba8), pixels);

        generator.Dispose();
        kernels.Dispose();
        buffer.Dispose();
        e.Dispose();
        stream.Dispose();
        Assert.NotEqual(0, context.Handle);
        pinned.Dispose();
        Assert.Equal(0, context.Handle); // the last child released the primary context reference
    }

    [Fact]
    public void Children_disposed_in_any_order_twice_is_harmless()
    {
        Gpu.Require();
        using var context = CudaContext.RetainPrimary(0);
        var objects = new List<IDisposable>
        {
            CudaStream.Create(context),
            CudaEvent.CreateForTiming(context),
            CudaDeviceBuffer.Allocate(context, 1024),
            CudaPinnedBuffer.Allocate(context, 1024),
            CudaSpanTimer.Create(context),
            CudaSpanTimerRing.Create(context, 4),
            new RepeatGenerator(context),
        };
        objects.Reverse();
        foreach (var o in objects)
        {
            o.Dispose();
            o.Dispose();
        }

        Assert.NotEqual(0, context.Handle);
    }
}
