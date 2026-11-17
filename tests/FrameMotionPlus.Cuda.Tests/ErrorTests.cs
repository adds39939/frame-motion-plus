using System.Text;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>Failures surface as clear exceptions and leave the context usable (none of these is a sticky error).</summary>
[Trait("Category", "Gpu")]
public class ErrorTests
{
    [Fact]
    public void Bad_ptx_fails_with_the_jit_log()
    {
        var env = Gpu.Require();
        const string Ptx = ".version 7.0\n.target sm_75\n.address_size 64\n.visible .entry broken()\n{\n\tnot_an_instruction %r1;\n\tret;\n}\n";
        var error = Assert.Throws<CudaException>(() => CudaModule.Load(env.Context, Encoding.ASCII.GetBytes(Ptx), "broken"));
        Assert.Equal(CudaResult.ErrorInvalidPtx, error.Result);
        Assert.Equal("CUDA_ERROR_INVALID_PTX", error.ErrorName);
        Assert.Equal("cuModuleLoadDataEx", error.Function);
        Assert.Contains("not_an_instruction", error.Message, StringComparison.Ordinal);
        Assert.Contains("'broken'", error.Message, StringComparison.Ordinal);

        var garbage = Assert.Throws<CudaException>(() => CudaModule.Load(env.Context, "definitely not a module"u8, "garbage"));
        Assert.True(garbage.Result is CudaResult.ErrorInvalidImage or CudaResult.ErrorInvalidPtx, garbage.Message);
        Assert.Throws<ArgumentException>(() => CudaModule.Load(env.Context, ReadOnlySpan<byte>.Empty));
        AssertContextStillWorks(env);
    }

    [Fact]
    public void Unknown_kernel_and_invalid_launches_fail_cleanly()
    {
        var env = Gpu.Require();
        using var module = CudaModule.Load(env.Context, ImageKernels.GetEmbeddedPtx("util"), "util");
        var missing = Assert.Throws<CudaException>(() => module.GetKernel("no_such_kernel"));
        Assert.Equal(CudaResult.ErrorNotFound, missing.Result);
        Assert.Contains("no_such_kernel", missing.Message, StringComparison.Ordinal);

        var kernel = module.GetKernel("fmp_delay_ns");
        using var stream = CudaStream.Create(env.Context);
        var tooBig = Assert.Throws<CudaException>(() => Launch(kernel, stream, new CudaDim3(1), new CudaDim3(4096)));
        Assert.Equal(CudaResult.ErrorInvalidValue, tooBig.Result);
        Assert.Contains("block 4096x1x1", tooBig.Message, StringComparison.Ordinal);
        Assert.Equal(CudaResult.ErrorInvalidValue, Assert.Throws<CudaException>(() => Launch(kernel, stream, new CudaDim3(0), new CudaDim3(1))).Result);
        AssertContextStillWorks(env);

        static unsafe void Launch(CudaKernel kernel, CudaStream stream, CudaDim3 grid, CudaDim3 block)
        {
            ulong ns = 0;
            var args = stackalloc void*[1] { &ns };
            kernel.Launch(stream, grid, block, 0, args);
        }
    }

    [Fact]
    public void Invalid_sizes_are_rejected_before_or_by_the_driver()
    {
        var env = Gpu.Require();
        Assert.Throws<ArgumentOutOfRangeException>(() => CudaDeviceBuffer.Allocate(env.Context, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CudaDeviceBuffer.Allocate(env.Context, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CudaDeviceBuffer.AllocateImage(env.Context, 0, 10, PixelFormat.Rgba8));
        Assert.Throws<ArgumentOutOfRangeException>(() => CudaDeviceBuffer.AllocateImage(env.Context, 10, 10, PixelFormat.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => CudaPinnedBuffer.Allocate(env.Context, 0));
        var oom = Assert.Throws<CudaException>(() => CudaDeviceBuffer.Allocate(env.Context, 1L << 50));
        Assert.Equal(CudaResult.ErrorOutOfMemory, oom.Result);
        Assert.StartsWith("cuMemAlloc_v2: CUDA_ERROR_OUT_OF_MEMORY", oom.Message, StringComparison.Ordinal);

        using var buffer = CudaDeviceBuffer.Allocate(env.Context, 64 * 64 * 4);
        Assert.Throws<ArgumentException>(() => buffer.AsFrame(64, 65, PixelFormat.Rgba8, 256)); // past the end
        Assert.Throws<ArgumentException>(() => buffer.AsFrame(64, 64, PixelFormat.Rgba8, 252)); // pitch below a row
        Assert.Throws<ArgumentException>(() => buffer.AsFrame(64, 63, PixelFormat.Rgba8, 256, offset: 2)); // misaligned
        Assert.Throws<ArgumentException>(() => buffer.AsFrame(64, 64, PixelFormat.Rgba8, 256, offset: 4)); // shifted past the end
        Assert.Throws<InvalidOperationException>(() => buffer.AsFrame());

        using var stream = CudaStream.Create(env.Context);
        Assert.Throws<ArgumentException>(() => stream.CopyAsync(buffer.Address, buffer.Address, 0));
        Assert.Throws<ArgumentException>(() => stream.CopyAsync(0, buffer.Address, 4));
        Assert.Throws<ArgumentException>(() => stream.Copy2DAsync(buffer.Address, 16, buffer.Address, 256, 32, 4));
        var invalidCopy = Assert.Throws<CudaException>(() => stream.CopyAsync(buffer.Address, buffer.Address, buffer.SizeBytes * 4));
        Assert.Equal(CudaResult.ErrorInvalidValue, invalidCopy.Result);
        AssertContextStillWorks(env);
    }

    [Fact]
    public void Disposed_objects_throw_object_disposed()
    {
        var env = Gpu.Require();
        var stream = CudaStream.Create(env.Context);
        var e = CudaEvent.Create(env.Context);
        var buffer = CudaDeviceBuffer.AllocateImage(env.Context, 16, 16, PixelFormat.Rgba8);
        stream.Dispose();
        e.Dispose();
        buffer.Dispose();
        stream.Dispose();
        Assert.True(stream.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => stream.Synchronize());
        Assert.Throws<ObjectDisposedException>(() => _ = e.IsComplete);
        Assert.Throws<ObjectDisposedException>(() => buffer.AsFrame());
        Assert.Throws<ObjectDisposedException>(() => CudaStream.Create(DisposedContext()));
    }

    [Fact]
    public void Signals_from_other_domains_are_refused()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        Assert.Throws<ArgumentException>(() => stream.Wait(GpuSignal.Timeline(MemoryDomain.Vulkan, 1, 1)));
        Assert.Throws<ArgumentException>(() => stream.Signal(GpuSignal.Timeline(7, 1)));
        Assert.Throws<ArgumentException>(() => stream.Wait(new GpuSignal { Kind = GpuSignalKind.CudaEvent, Domain = MemoryDomain.CudaDevice }));
        stream.Wait(GpuSignal.None);
        stream.Signal(GpuSignal.None);
    }

    private static CudaContext DisposedContext()
    {
        var context = CudaContext.RetainPrimary(0);
        context.Dispose();
        return context;
    }

    private static void AssertContextStillWorks(GpuEnvironment env)
    {
        using var stream = CudaStream.Create(env.Context);
        using var buffer = env.Image(32, 32, PixelFormat.Rgba8, stream);
        env.Kernels.EnqueueTestPattern(stream, buffer.AsFrame(), 3);
        Assert.Equal(Reference.Pattern(32, 32, 3, PixelFormat.Rgba8), env.Read(stream, buffer.AsFrame()));
    }
}
