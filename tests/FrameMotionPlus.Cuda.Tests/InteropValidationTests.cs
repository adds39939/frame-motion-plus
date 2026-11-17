using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>
/// External memory and semaphore import need a Vulkan exporter for a real round trip (the CudaVulkan integration
/// step). Here: argument validation, and the driver rejecting descriptors that are not exportable objects cleanly,
/// which also exercises the descriptor layouts through the real entry points. Serial: some tests count the process's
/// open descriptors or follow descriptor numbers, which other tests opening files or pipes in parallel would disturb.
/// </summary>
[Collection(SerialGpuDefinition.Name)]
[Trait("Category", "Gpu")]
public class InteropValidationTests
{
    [Fact]
    public void External_memory_import_validates_arguments()
    {
        var env = Gpu.Require();
        Assert.Throws<ArgumentOutOfRangeException>(() => CudaExternalMemory.ImportOpaqueFd(env.Context, -1, 4096, dedicated: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => CudaExternalMemory.ImportOpaqueFd(env.Context, 0, 0, dedicated: true));
        Assert.Throws<ArgumentNullException>(() => CudaExternalMemory.ImportOpaqueFd(null!, 3, 4096, dedicated: false));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => CudaExternalMemory.ImportOpaqueWin32(env.Context, 1, 4096, dedicated: true));
        }
    }

    [Fact]
    public void External_semaphore_import_validates_arguments()
    {
        var env = Gpu.Require();
        Assert.Throws<ArgumentOutOfRangeException>(() => CudaExternalSemaphore.ImportTimelineFd(env.Context, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CudaExternalSemaphore.ImportBinaryFd(env.Context, -5));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => CudaExternalSemaphore.ImportTimelineWin32(env.Context, 1));
        }

        using var stream = CudaStream.Create(env.Context);
        Assert.Throws<ArgumentOutOfRangeException>(() => CudaExternalSemaphore.SignalMany(stream, [], []));
        Assert.Throws<ArgumentException>(() => CudaExternalSemaphore.WaitMany(stream, [], [1ul]));
        var tooMany = new CudaExternalSemaphore[CudaExternalSemaphore.MaxBatch + 1];
        var values = new ulong[tooMany.Length];
        Assert.Throws<ArgumentOutOfRangeException>(() => CudaExternalSemaphore.SignalMany(stream, tooMany, values));
        Assert.Throws<ArgumentNullException>(() => CudaExternalSemaphore.WaitMany(stream, new CudaExternalSemaphore[1], [1ul]));
    }

    [Fact]
    public void Descriptors_that_are_not_exported_objects_are_rejected_cleanly()
    {
        var env = Gpu.Require();
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("needs a POSIX file descriptor");
        }

        // /dev/null is a valid descriptor but no exported GPU object: the driver must say so, not crash. On failure
        // the caller keeps ownership, so the descriptor is still ours to close.
        using var devNull = File.OpenHandle("/dev/null");
        var fd = (int)devNull.DangerousGetHandle();
        var memory = Assert.Throws<CudaException>(() => CudaExternalMemory.ImportOpaqueFd(env.Context, fd, 1 << 20, dedicated: true));
        Assert.NotEqual(CudaResult.Success, memory.Result);
        Assert.Equal("cuImportExternalMemory", memory.Function);
        var semaphore = Assert.Throws<CudaException>(() => CudaExternalSemaphore.ImportTimelineFd(env.Context, fd));
        Assert.Equal("cuImportExternalSemaphore", semaphore.Function);
        AssertStillRefersTo(fd, "/dev/null");

        using var stream = CudaStream.Create(env.Context);
        using var buffer = env.Image(16, 16, PixelFormat.Rgba8, stream);
        env.Kernels.EnqueueTestPattern(stream, buffer.AsFrame(), 1);
        Assert.Equal(Reference.Pattern(16, 16, 1, PixelFormat.Rgba8), env.Read(stream, buffer.AsFrame()));
    }

    [Fact]
    public void A_failed_import_leaves_the_descriptor_open_with_the_caller_and_leaks_nothing()
    {
        // Found joining CUDA and Vulkan: the driver (620.70) closes the descriptor when cuImportExternalMemory fails,
        // although the documented rule, and this wrapper's, is that the caller keeps it on failure; the caller's own
        // close would then hit a number that may already be reused. The wrapper hands the driver a private duplicate.
        var env = Gpu.Require();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("needs POSIX file descriptors and /proc");
        }

        var path = Path.GetTempFileName();
        try
        {
            foreach (var target in new[] { "/dev/null", path })
            {
                using var handle = File.OpenHandle(target);
                var fd = (int)handle.DangerousGetHandle();
                var open = Directory.GetFiles("/proc/self/fd").Length;
                Assert.Throws<CudaException>(() => CudaExternalMemory.ImportOpaqueFd(env.Context, fd, 1 << 20, dedicated: true));
                Assert.Equal(target, new FileInfo($"/proc/self/fd/{fd}").LinkTarget);
                Assert.Throws<CudaException>(() => CudaExternalSemaphore.ImportTimelineFd(env.Context, fd));
                Assert.Equal(target, new FileInfo($"/proc/self/fd/{fd}").LinkTarget);
                Assert.Equal(open, Directory.GetFiles("/proc/self/fd").Length);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Import_through_a_disposed_context_fails_before_the_driver_takes_the_descriptor()
    {
        // Found joining CUDA and Vulkan: a disposed CudaContext whose primary context is still alive (an object created
        // from it holds a reference) let the driver import, and so take and close, a real exported descriptor before the
        // wrapper threw ObjectDisposedException; the caller, told it still owns the descriptor on failure, then closed
        // it a second time. The check must come first. With /dev/null the driver would reject the descriptor itself
        // (CudaException), so ObjectDisposedException proves the driver was never called.
        var env = Gpu.Require();
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("needs a POSIX file descriptor");
        }

        var context = CudaContext.RetainPrimary(env.Context.Device);
        using var keepsItAlive = CudaStream.Create(context);
        context.Dispose();
        Assert.NotEqual(0, context.Handle);

        using var devNull = File.OpenHandle("/dev/null");
        var fd = (int)devNull.DangerousGetHandle();
        Assert.Throws<ObjectDisposedException>(() => CudaExternalMemory.ImportOpaqueFd(context, fd, 1 << 20, dedicated: true));
        Assert.Throws<ObjectDisposedException>(() => CudaExternalSemaphore.ImportTimelineFd(context, fd));
        Assert.Throws<ObjectDisposedException>(() => CudaExternalSemaphore.ImportBinaryFd(context, fd));
        AssertStillRefersTo(fd, "/dev/null");
    }

    [Fact]
    public void Concurrent_failed_imports_never_close_another_threads_descriptor()
    {
        // Each failed import is given a private duplicate that the driver may or may not close. The check-then-close of
        // that duplicate is serialised with the other imports, so it can never close a duplicate another thread has
        // just been handed. Every thread's own descriptor must still name its own file at the end.
        var env = Gpu.Require();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("needs POSIX file descriptors and /proc");
        }

        var paths = Enumerable.Range(0, 6).Select(_ => Path.GetTempFileName()).ToArray();
        var open = Directory.GetFiles("/proc/self/fd").Length;
        try
        {
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var threads = paths.Select(path => new Thread(() =>
            {
                using var handle = File.OpenHandle(path);
                var fd = (int)handle.DangerousGetHandle();
                for (var i = 0; i < 40; i++)
                {
                    try
                    {
                        if (i % 2 == 0)
                        {
                            CudaExternalMemory.ImportOpaqueFd(env.Context, fd, 1 << 20, dedicated: true).Dispose();
                        }
                        else
                        {
                            CudaExternalSemaphore.ImportTimelineFd(env.Context, fd).Dispose();
                        }

                        errors.Enqueue($"import of {path} succeeded");
                    }
                    catch (CudaException)
                    {
                    }

                    var target = new FileInfo($"/proc/self/fd/{fd}").LinkTarget;
                    if (target != path)
                    {
                        errors.Enqueue($"descriptor {fd} for {path} now names {target ?? "nothing"}");
                        return;
                    }
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            Assert.Empty(errors);
            Assert.Equal(open, Directory.GetFiles("/proc/self/fd").Length);
        }
        finally
        {
            foreach (var path in paths)
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>The descriptor number is still open and names <paramref name="path"/> (not closed, not reused).</summary>
    private static void AssertStillRefersTo(int fd, string path)
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(path, new FileInfo($"/proc/self/fd/{fd}").LinkTarget);
        }
    }
}
