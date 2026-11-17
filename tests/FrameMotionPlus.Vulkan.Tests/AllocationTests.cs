using System.Diagnostics;
using System.Runtime;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Diagnostics;
using FrameMotionPlus.Vulkan.Tests.Support;
using FrameMotionPlus.Vulkan.Windowing;

namespace FrameMotionPlus.Vulkan.Tests;

/// <summary>
/// Hot-path rule: nothing the present thread does per frame allocates. Measured with the thread's allocation counter,
/// which is exact because the test project runs non-concurrent GC (see the csproj) and the assembly runs tests one at
/// a time. Each test also checks, through the presenter's counters, that every path it claims to cover really ran.
/// </summary>
public class AllocationTests
{
    private const int Windows = 3;

    /// <summary>The csproj's TieredPGO=false reaches the test host (see the comment there).</summary>
    [Fact]
    public void Test_host_runs_without_dynamic_pgo() =>
        Assert.True(AppContext.TryGetSwitch("System.Runtime.TieredPGO", out var pgo) && !pgo, "System.Runtime.TieredPGO is not false in this host");

    [Fact]
    public void Test_host_runs_without_background_gc() => Assert.Equal(GCLatencyMode.Batch, GCSettings.LatencyMode);

    /// <summary>
    /// Every per-frame path of the offscreen presenter, each iteration: copy-and-present, repeat, refused requests
    /// (with the refused write retained), a retry that shows the retained write, an explicit release through
    /// <see cref="IPresenter.Release"/>, a stale request and feedback.
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(PixelFormat.Rgba8)]
    [InlineData(PixelFormat.Rgb10A2)]
    public void Every_per_frame_presenter_path_allocates_nothing(PixelFormat format) => Gpu.Run(ctx =>
    {
        // Scaled (1280x720 into 1920x1080) so the full pass runs; no readback, so nothing waits on the CPU.
        using var rig = OffscreenRig.Create(ctx, 1280, 720, format, new OffscreenTargetOptions { Width = 1920, Height = 1080, Readback = false });
        rig.Producer.FillAllStaging(TestPattern.Smooth, 0);
        var presenter = rig.Presenter;
        IPresenter core = presenter;
        var producer = rig.Producer;
        var ring = rig.Ring;
        const int Iterations = 100;
        var unexpected = 0;

        PresentRequest Request(ulong n) => new()
        {
            SlotIndex = (long)n,
            Frame = ring.ConsumerFrame(n),
            Ready = ring.WrittenSignal(n),
            TargetTimeNs = (long)n,
        };

        void Window()
        {
            for (var i = 0; i < Iterations; i++)
            {
                // Both frame slots free, so the first two requests are certain to be accepted and the third refused.
                unexpected += presenter.WaitIdle(1_000_000_000) ? 0 : 1;
                var k = producer.NextWrite;
                unexpected += core.TryPresent(Request(k)) ? 0 : 1;          // copy path, waits on the GPU for write k
                unexpected += core.TryPresent(Request(k)) ? 0 : 1;          // repeat path (second frame in flight)
                unexpected += core.TryPresent(Request(k + 1)) ? 1 : 0;      // refused: no frame slot; k + 1 retained
                unexpected += core.TryPresent(Request(k + 1)) ? 1 : 0;      // refused again, still retained
                if (k > 1)
                {
                    unexpected += core.TryPresent(Request(k - 1)) ? 1 : 0;  // stale (k - 1 was released)
                }

                producer.WriteStaged();
                producer.WriteStaged();
                rig.WaitReady();
                unexpected += core.TryPresent(Request(k + 1)) ? 0 : 1;      // the retained write is shown after all
                var released = producer.WriteStaged();
                core.Release(Request(released));                           // release path
                while (core.TryGetFeedback(out _))
                {
                }
            }
        }

        Window(); // warm-up: JIT, first-use paths
        var before = presenter.Stats;
        long allocated = 0;
        for (var w = 0; w < Windows; w++)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            Window();
            allocated += GC.GetAllocatedBytesForCurrentThread() - start;
        }

        Assert.True(presenter.WaitIdle(1_000_000_000));
        var after = presenter.Stats;
        Gpu.WriteLine($"{allocated} bytes over {Windows * Iterations} measured iterations; {after}");
        Assert.Equal(0, unexpected);
        const long Expected = Windows * Iterations;
        Assert.Equal(2 * Expected, after.Presented - before.Presented - (after.RetainedShown - before.RetainedShown));
        Assert.Equal(Expected, after.Repeats - before.Repeats);
        Assert.Equal(2 * Expected, after.Dropped - before.Dropped);
        Assert.Equal(Expected, after.RetainedShown - before.RetainedShown);
        Assert.Equal(Expected, after.StaleRequests - before.StaleRequests);
        Assert.True(after.Releases - before.Releases >= Expected);
        Assert.Equal(0, allocated);
    });

    /// <summary>
    /// The windowed path: event pump, acquire, present (FIFO), present-wait polling, refused requests, feedback; and a
    /// swapchain rebuild after a resize.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void Windowed_presenting_and_the_event_pump_allocate_nothing()
    {
        Requirements.SkipUnlessDisplay();
        using var window = SdlWindow.Create(new SdlWindowOptions { Title = "fmp allocation test", Width = 320, Height = 180 });
        Gpu.Run(ctx =>
        {
            var target = ctx.Own(new SwapchainTarget(ctx.Device, window));
            var presenter = new VulkanPresenter(ctx.Device, target);
            DiagnosticRingProducer? producer = null;
            try
            {
                presenter.Configure(new PresenterConfig { Width = 320, Height = 180, Format = PixelFormat.Rgba8 });
                using (var export = presenter.Ring!.Export())
                {
                    producer = DiagnosticRingProducer.Create(ctx.Instance, export);
                }

                producer.FillAllStaging(TestPattern.MovingBar, 0);
                var ring = presenter.Ring!;
                var pumps = 0;
                long Run(double seconds)
                {
                    var start = Stopwatch.GetTimestamp();
                    var allocatedStart = GC.GetAllocatedBytesForCurrentThread();
                    while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
                    {
                        window.PumpEvents();
                        pumps++;
                        var n = producer.WriteStaged();
                        var request = new PresentRequest { SlotIndex = (long)n, Frame = ring.ConsumerFrame(n), Ready = ring.WrittenSignal(n) };

                        // Not gated on IsReadyForPresent: most of these are refused, which exercises that path too.
                        if (presenter.TryPresent(request))
                        {
                            presenter.TryPresent(request); // a repeat when a slot is free, otherwise refused
                        }

                        while (presenter.TryGetFeedback(out _))
                        {
                        }

                        Thread.SpinWait(2000);
                    }

                    return GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
                }

                Run(0.5); // warm-up

                // Half-second windows. One in which the target was rebuilt (the compositor reconfigured the window) is
                // run again rather than judged; every other one must allocate exactly nothing.
                long presented = 0, dropped = 0, completed = 0;
                var clean = 0;
                for (var attempt = 0; attempt < 10 && clean < 3; attempt++)
                {
                    var before = presenter.Stats;
                    var allocated = Run(0.5);
                    var after = presenter.Stats;
                    var rebuilds = after.TargetRecreations - before.TargetRecreations;
                    Gpu.WriteLine($"window {attempt}: {allocated} bytes, {rebuilds} target rebuilds; {after}");
                    if (rebuilds != 0)
                    {
                        continue;
                    }

                    Assert.Equal(0, allocated);
                    presented += after.Presented - before.Presented;
                    dropped += after.Dropped - before.Dropped;
                    completed += after.Completed - before.Completed;
                    clean++;
                }

                Gpu.WriteLine($"{pumps} pumps");
                Assert.True(clean == 3, $"only {clean} of the windows ran without a target rebuild");
                Assert.True(presented > 50, "too few presents to have covered the present path");
                Assert.True(dropped > 50, "the refused path did not run");
                Assert.True(completed > 50, "present completions were not polled");

                // A resize during playback rebuilds the swapchain with as many images as before: that allocates nothing.
                var images = target.ImageCountInUse;
                var rebuiltBefore = presenter.Stats.TargetRecreations;
                window.SetSize(400, 240);
                var resizeAllocated = Run(0.5);
                Gpu.WriteLine($"resize: {resizeAllocated} bytes, {presenter.Stats.TargetRecreations - rebuiltBefore} target rebuilds, images {images} -> {target.ImageCountInUse}");
                Assert.True(presenter.Stats.TargetRecreations > rebuiltBefore, "the resize did not rebuild the swapchain");
                if (target.ImageCountInUse == images)
                {
                    Assert.Equal(0, resizeAllocated);
                }
            }
            finally
            {
                presenter.Dispose();
                producer?.Dispose();
            }
        }, window.RequiredInstanceExtensions);
    }
}
