using System.Runtime.InteropServices;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Cuda.Interop;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>
/// Failure paths leave nothing behind: no device memory and no reference on the context. Factories take the context
/// reference before any driver call and free what they made when a later step fails. Serial, so free device memory
/// is not moved by other tests. Other processes on this desktop can still move it, which <see cref="AssertNoLeak"/>
/// allows for without losing a real leak.
/// </summary>
[Collection(SerialGpuDefinition.Name)]
[Trait("Category", "Gpu")]
public class LeakTests
{
    private const long Block = 512L << 20;
    private const long Tolerance = 1L << 30;

    [Fact]
    public unsafe void Factories_on_a_disposed_context_throw_before_allocating_anything()
    {
        var env = Gpu.Require();
        var context = CudaContext.RetainPrimary(env.Context.Device);
        var keepAlive = CudaEvent.Create(context);
        context.Dispose();
        Assert.NotEqual(0, context.Handle);
        var host = NativeMemory.AlignedAlloc(1 << 16, 4096);
        try
        {
            // 16 failed 512 MB allocations per round: before the fix each one still allocated, 8 GB a round.
            AssertNoLeak(env, () =>
            {
                for (var i = 0; i < 8; i++)
                {
                    Assert.Throws<ObjectDisposedException>(() => CudaDeviceBuffer.Allocate(context, Block));
                    Assert.Throws<ObjectDisposedException>(() => CudaDeviceBuffer.AllocateImage(context, 8192, 16384, PixelFormat.Rgba8));
                }
            });

            Assert.Throws<ObjectDisposedException>(() => CudaPinnedBuffer.Allocate(context, 64L << 20));
            Assert.Throws<ObjectDisposedException>(() => CudaHostRegistration.Register(context, host, 1 << 16));
            Assert.Throws<ObjectDisposedException>(() => CudaStream.Create(context));
            Assert.Throws<ObjectDisposedException>(() => CudaStream.CreateWithPriority(context, 0));
            Assert.Throws<ObjectDisposedException>(() => CudaEvent.Create(context));
            Assert.Throws<ObjectDisposedException>(() => CudaEvent.CreateForTiming(context));
            Assert.Throws<ObjectDisposedException>(() => CudaModule.Load(context, ImageKernels.GetEmbeddedPtx("util")));
            Assert.Throws<ObjectDisposedException>(() => ImageKernels.Load(context));
            Assert.Throws<ObjectDisposedException>(() => CudaSpanTimer.Create(context));
            Assert.Throws<ObjectDisposedException>(() => CudaSpanTimerRing.Create(context));
            Assert.Throws<ObjectDisposedException>(() => new BlendGenerator(context, env.Kernels));
            Assert.Throws<ObjectDisposedException>(() => new RepeatGenerator(context));
            Assert.Equal(1, context.ReferenceCount); // only the event's
        }
        finally
        {
            NativeMemory.AlignedFree(host);
        }

        keepAlive.Dispose();
        Assert.Equal(0, context.Handle);
    }

    /// <summary>
    /// Free device memory does not drop by <see cref="Tolerance"/> or more across <paramref name="failingWork"/>. A leak
    /// recurs every time the work runs; another process allocating during one measurement does not, so a drop is
    /// measured once more, over a second run of the work, before it counts. A free elsewhere can only raise free memory.
    /// </summary>
    private static void AssertNoLeak(GpuEnvironment env, Action failingWork)
    {
        long Drop()
        {
            var before = env.Context.MemoryInfo.FreeBytes;
            failingWork();
            return before - env.Context.MemoryInfo.FreeBytes;
        }

        var first = Drop();
        if (first < Tolerance)
        {
            return;
        }

        var second = Drop();
        Assert.True(second < Tolerance, $"free device memory dropped by {first >> 20} MB and then by {second >> 20} MB across the failing calls");
    }

    [Fact]
    public void Generators_that_fail_their_arguments_leave_no_stream_or_events_behind()
    {
        var env = Gpu.Require();
        var context = CudaContext.RetainPrimary(env.Context.Device);
        using (var foreignOwner = CudaStream.Create(context))
        {
            Assert.Throws<ArgumentNullException>(() => new BlendGenerator(context, null!));
            Assert.Throws<ArgumentNullException>(() => new BlendGenerator(context, null!, CudaStreamPriority.Normal));
            Assert.Throws<ArgumentNullException>(() => new BlendGenerator(context, null!, foreignOwner));
            Assert.Throws<ArgumentNullException>(() => new BlendGenerator(context, env.Kernels, (CudaStream)null!));
            Assert.Throws<ArgumentNullException>(() => new RepeatGenerator(context, (CudaStream)null!));
            Assert.Throws<ArgumentNullException>(() => new BlendGenerator(null!, env.Kernels));
            Assert.Equal(2, context.ReferenceCount); // its own and the stream's
        }

        // Before the fix the first call alone left a stream and 64 timer events holding the primary context.
        context.Dispose();
        Assert.Equal(0, context.ReferenceCount);
        Assert.Equal(0, context.Handle);
    }

    [Fact]
    public void Driver_failures_inside_factories_release_their_references_and_memory()
    {
        var env = Gpu.Require();
        var context = CudaContext.RetainPrimary(env.Context.Device);
        AssertNoLeak(env, () =>
        {
            Assert.Equal(CudaResult.ErrorOutOfMemory, Assert.Throws<CudaException>(() => CudaDeviceBuffer.Allocate(context, 1L << 50)).Result);
            Assert.Throws<CudaException>(() => CudaDeviceBuffer.AllocateImage(context, 1 << 20, 1 << 20, PixelFormat.Rgba8));
            Assert.Throws<CudaException>(() => CudaPinnedBuffer.Allocate(context, 1L << 46));
            Assert.Throws<CudaException>(() => CudaModule.Load(context, "not a module"u8));
            Assert.Throws<CudaException>(() => ImageKernels.Load(context, _ => ImageKernels.GetEmbeddedPtx("util"))); // kernels missing
        });
        Assert.Equal(1, context.ReferenceCount);
        context.Dispose();
        Assert.Equal(0, context.Handle);
    }

    [Fact]
    public unsafe void Creating_objects_while_the_context_is_disposed_never_releases_the_primary_context_twice()
    {
        var env = Gpu.Require();
        for (var round = 0; round < 40; round++)
        {
            var context = CudaContext.RetainPrimary(env.Context.Device);
            using var start = new Barrier(5);
            var workers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                for (var i = 0; i < 200; i++)
                {
                    try
                    {
                        using var e = CudaEvent.Create(context);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            })).ToList();
            workers.ForEach(t => t.Start());
            start.SignalAndWait(TestContext.Current.CancellationToken);
            context.Dispose();
            workers.ForEach(t => t.Join());
            Assert.Equal(0, context.ReferenceCount);
            Assert.Equal(0, context.Handle);
        }

        // A double release would have dropped the shared context's retain: the primary context must still be active.
        uint flags;
        int active;
        Assert.Equal(CudaResult.Success, CuNative.cuDevicePrimaryCtxGetState(env.Context.Device.Handle, &flags, &active));
        Assert.Equal(1, active);
        using var stream = CudaStream.Create(env.Context);
        using var buffer = env.Image(32, 32, PixelFormat.Rgba8, stream);
        env.Kernels.EnqueueTestPattern(stream, buffer.AsFrame(), 2);
        Assert.Equal(Reference.Pattern(32, 32, 2, PixelFormat.Rgba8), env.Read(stream, buffer.AsFrame()));
    }
}
