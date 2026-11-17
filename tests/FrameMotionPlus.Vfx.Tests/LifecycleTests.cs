using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vfx.Tests.Support;

namespace FrameMotionPlus.Vfx.Tests;

/// <summary>
/// Create, configure (load), run and destroy, repeated: nothing leaks. Checked three ways after a warm-up cycle: the
/// CUDA context's reference count (every stream, event and buffer the generator made is released), the process's GPU
/// memory as nvidia-smi reports it, and the context's free memory.
/// </summary>
[Trait(Category.Name, Category.Vfx)]
public class LifecycleTests
{
    [Fact]
    public void Repeated_create_configure_run_destroy_leaks_nothing()
    {
        var env = VfxHardware.Require();
        var configs = new[]
        {
            new FrameGenConfig { Width = 640, Height = 360, Format = PixelFormat.Rgba8, Quality = FrameGenQuality.Medium },
            new FrameGenConfig { Width = 1280, Height = 720, Format = PixelFormat.Rgb10A2, Quality = FrameGenQuality.Low },
            new FrameGenConfig { Width = 960, Height = 540, Format = PixelFormat.Bgra8, Quality = FrameGenQuality.High },
        };

        void Cycle()
        {
            foreach (var config in configs)
            {
                using var generator = new VfgGenerator(env.Context);
                generator.Configure(config);
                generator.Prepare(config with { Format = config.Format == PixelFormat.Rgb10A2 ? PixelFormat.Rgba8 : PixelFormat.Rgb10A2 });
                using var a = env.Pattern(config.Width, config.Height, config.Format, 3);
                using var b = env.Pattern(config.Width, config.Height, config.Format, 5);
                using var o = env.Image(config.Width, config.Height, config.Format);
                using var done = CudaEvent.Create(env.Context);
                for (var i = 0; i < 4; i++)
                {
                    generator.Enqueue(a.AsFrame(contentTimeNs: 1), b.AsFrame(contentTimeNs: 2), 0.25f * (i + 1) / 2, o.AsFrame(), done.Signal);
                }

                done.Synchronize();
                Assert.Equal(2, generator.LoadedInstances);
            }
        }

        Cycle(); // warm-up: the SDK's one-time allocations, the driver's pools
        var references = env.Context.ReferenceCount;
        var smiBefore = Machine.ProcessGpuMemoryMiB();
        var freeBefore = env.Context.MemoryInfo.FreeBytes;
        for (var k = 0; k < 3; k++)
        {
            Cycle();
        }

        var smiAfter = Machine.ProcessGpuMemoryMiB();
        var freeAfter = env.Context.MemoryInfo.FreeBytes;
        Log.WriteLine($"context references {references} -> {env.Context.ReferenceCount}; nvidia-smi {smiBefore} -> {smiAfter} MiB; " +
                      $"context free memory changed by {(freeAfter - freeBefore) / 1048576.0:0.0} MiB over 3 cycles of {configs.Length * 2} loads");
        Assert.Equal(references, env.Context.ReferenceCount);
        if (smiBefore >= 0 && smiAfter >= 0)
        {
            Assert.InRange(smiAfter - smiBefore, -64, 16);
        }

        // The free count is the whole GPU's (other processes allocate too), so only a large loss means anything.
        Assert.True(freeBefore - freeAfter < 256L << 20, $"{(freeBefore - freeAfter) >> 20} MiB less free memory");
    }

    [Fact]
    public void A_generator_releases_every_cuda_object_it_made()
    {
        var env = VfxHardware.Require();
        var references = env.Context.ReferenceCount;
        var generator = new VfgGenerator(env.Context);
        Assert.True(env.Context.ReferenceCount > references);
        generator.Configure(new FrameGenConfig { Width = 320, Height = 240, Format = PixelFormat.Rgba8 });
        generator.Dispose();
        Assert.Equal(references, env.Context.ReferenceCount);

        // With a stream of the caller's: the generator neither keeps nor disposes it.
        using var stream = CudaStream.Create(env.Context);
        var withStream = env.Context.ReferenceCount;
        var borrowed = new VfgGenerator(env.Context, new VfgOptions { Stream = stream });
        borrowed.Configure(new FrameGenConfig { Width = 320, Height = 240, Format = PixelFormat.Rgba8 });
        borrowed.Dispose();
        Assert.False(stream.IsDisposed);
        Assert.Equal(withStream, env.Context.ReferenceCount);
    }
}
