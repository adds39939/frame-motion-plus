using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>
/// A <see cref="GpuSignal"/> is a raw handle. Waiting on or recording a destroyed CUDA event made the driver spin for
/// about a minute and then crash the process; handing it an unknown semaphore handle is no better. Every signal is now
/// checked against the live objects first and refused with an exception.
/// </summary>
[Trait("Category", "Gpu")]
public class StaleSignalTests
{
    [Fact]
    public void A_disposed_events_signal_is_refused_quickly_instead_of_reaching_the_driver()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        var e = CudaEvent.Create(env.Context);
        var stale = e.Signal;
        e.Dispose();

        var clock = Stopwatch.StartNew();
        Assert.Throws<ObjectDisposedException>(() => stream.Wait(stale));
        Assert.Throws<ObjectDisposedException>(() => stream.Signal(stale));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
        stream.Synchronize();
    }

    [Fact]
    public void A_stale_signal_stays_refused_when_the_driver_reuses_its_handle()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        var created = new List<CudaEvent>();
        try
        {
            CudaEvent? reused = null;
            for (var attempt = 0; attempt < 32 && reused is null; attempt++)
            {
                var e = CudaEvent.Create(env.Context);
                var stale = e.Signal;
                e.Dispose();
                for (var i = 0; i < 8 && reused is null; i++)
                {
                    var next = CudaEvent.Create(env.Context);
                    created.Add(next);
                    if ((ulong)next.Handle == stale.Handle)
                    {
                        reused = next;
                        Assert.NotEqual(stale, next.Signal); // same handle, new generation
                        Assert.Throws<ObjectDisposedException>(() => stream.Wait(stale));
                        Assert.Throws<ObjectDisposedException>(() => stream.Signal(stale));
                        stream.Signal(next.Signal);
                        stream.Wait(next.Signal);
                    }
                }
            }

            if (reused is null)
            {
                Assert.Skip("the driver never reused an event handle in 256 creations");
            }

            stream.Synchronize();
        }
        finally
        {
            created.ForEach(e => e.Dispose());
        }
    }

    [Fact]
    public void Unknown_handles_are_refused_and_untagged_live_events_accepted()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var live = CudaEvent.Create(env.Context);

        // Signals built by hand from a live event's handle (no generation tag) are fine.
        stream.Signal(GpuSignal.FromCudaEvent((ulong)live.Handle));
        stream.Wait(GpuSignal.FromCudaEvent((ulong)live.Handle));

        // Handles this library never created never reach the driver: before the check both crashed the process.
        Assert.Throws<ObjectDisposedException>(() => stream.Wait(GpuSignal.FromCudaEvent(0xDEAD0000)));
        Assert.Throws<ObjectDisposedException>(() => stream.Wait(GpuSignal.Timeline(MemoryDomain.CudaDevice, 0xDEAD0000, 1)));
        Assert.Throws<ObjectDisposedException>(() => stream.Signal(GpuSignal.Timeline(MemoryDomain.CudaDevice, 0xDEAD0000, 1)));
        Assert.Throws<ObjectDisposedException>(() => stream.Wait(live.Signal with { Value = live.Signal.Value + 1 }));
        stream.Synchronize();
    }

    [Fact]
    public void A_generator_refuses_a_frame_whose_ready_event_is_gone_before_enqueueing_anything()
    {
        var env = Gpu.Require();
        using var a = env.Image(64, 64, PixelFormat.Rgba8);
        using var o = env.Image(64, 64, PixelFormat.Rgba8);
        using var done = CudaEvent.Create(env.Context);
        using var generator = new BlendGenerator(env.Context, env.Kernels);
        generator.Configure(new FrameGenConfig { Width = 64, Height = 64, Format = PixelFormat.Rgba8 });
        var drawn = CudaEvent.Create(env.Context);
        var history = a.AsFrame(ready: drawn.Signal);
        drawn.Dispose();

        Assert.Throws<ObjectDisposedException>(() => generator.Enqueue(history, history with { ContentTimeNs = 1 }, 0.5f, o.AsFrame(), done.Signal));
        Assert.Throws<ObjectDisposedException>(() => generator.Enqueue(a.AsFrame(), a.AsFrame(contentTimeNs: 1), 0.5f, o.AsFrame(), history.Ready));
        generator.Stream.Synchronize();
        Assert.False(generator.TryReadGpuTime(out _));
        Assert.Equal(0, generator.Stream.EventWaits + generator.Stream.EventSignals);
    }
}
