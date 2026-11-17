using System.Diagnostics;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Diagnostics;
using FrameMotionPlus.Vulkan.Tests.Support;
using FrameMotionPlus.Vulkan.Windowing;
using static FrameMotionPlus.Vulkan.Tests.WindowedTests;

namespace FrameMotionPlus.Vulkan.Tests;

/// <summary>The Phase 2 review's window probes, kept as regression tests.</summary>
public class ReviewWindowRegressionTests
{
    private readonly record struct PacedResult(
        int Calls, int Accepted, long Dropped, long Stale, TimeSpan Worst, long Allocated, long Rebuilds, double Hz, double EndHz);

    /// <summary>
    /// Presents on a fixed cadence (every <paramref name="periodScale"/> refresh periods of the known rate), never asking
    /// IsReadyForPresent, as a scheduler would. The ticks lie on one lattice from <paramref name="origin"/>, shifted by
    /// <paramref name="phase"/> periods, so windows with different phases meet vblank at different points.
    /// Allocations are counted after a warm-up.
    /// </summary>
    private static PacedResult Paced(
        SdlWindow window, VulkanPresenter presenter, DiagnosticRingProducer producer, double seconds, double periodScale, bool repeatsOnly, long origin, double phase)
    {
        var hz = presenter.Timing.RefreshRate.Hz;
        var period = TimeSpan.FromSeconds(periodScale / hz);
        var offset = period * phase;
        var s0 = presenter.Stats;
        var begin = Stopwatch.GetElapsedTime(origin);
        var end = begin + TimeSpan.FromSeconds(seconds);
        var first = (long)Math.Ceiling((begin - offset) / period);
        var tick = first;
        var worst = TimeSpan.Zero;
        int calls = 0, accepted = 0;
        var last = presenter.LastPresentedWrite;
        long allocStart = 0;
        long allocated = 0;
        while (true)
        {
            while (Stopwatch.GetElapsedTime(origin) < offset + (period * tick))
            {
                Thread.SpinWait(20);
            }

            if (Stopwatch.GetElapsedTime(origin) >= end)
            {
                break;
            }

            if (tick - first == 30)
            {
                allocStart = GC.GetAllocatedBytesForCurrentThread();
            }

            window.PumpEvents();
            var n = repeatsOnly && last != 0 ? last : producer.WriteStaged();
            var t = Stopwatch.GetTimestamp();
            var ok = presenter.TryPresent(Request(presenter.Ring!, n));
            var elapsed = Stopwatch.GetElapsedTime(t);
            worst = elapsed > worst ? elapsed : worst;
            calls++;
            if (ok)
            {
                accepted++;
                last = n;
            }

            while (presenter.TryGetFeedback(out _))
            {
            }

            tick++;
            if (tick - first > 30)
            {
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocStart;
            }
        }

        var s1 = presenter.Stats;
        return new PacedResult(
            calls,
            accepted,
            s1.Dropped - s0.Dropped,
            s1.StaleRequests - s0.StaleRequests,
            worst,
            allocated,
            s1.TargetRecreations - s0.TargetRecreations,
            hz,
            presenter.Timing.RefreshRate.Hz);
    }

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void Paced_presentation_without_ready_gating_is_accepted_at_the_display_rate() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        // Show a frame first, so the refresh rate is that of the output the window is actually on.
        Run(window, presenter, producer, TestPattern.MovingBar, 0.3);
        Assert.True(presenter.IsTimingKnown);
        var refresh = presenter.Timing.RefreshRate.Hz;
        Gpu.WriteLine($"refresh {refresh:F2} Hz, images {target.ImageCountInUse}");
        foreach (var (scale, repeats) in new[] { (1.0, false), (2.0, false), (1.0, true), (0.5, false) })
        {
            // The pacing timer is not locked to vblank, and how many ticks are accepted depends on where they fall
            // against it: the three one-second windows are a third of a period apart on one lattice, and acceptance is
            // judged on the best. Every window must be stale-free and never accept more than the display shows. One in
            // which the target was rebuilt or the refresh rate changed (the compositor reconfigured the window or
            // moved it to another output) is run again at the same phase; the others must also be fast and
            // allocation-free.
            var origin = Stopwatch.GetTimestamp();
            var best = 0.0;
            var bestShare = 0.0;
            var clean = 0;
            for (var w = 0; w < 9 && clean < 3; w++)
            {
                var phase = clean / 3.0;
                var r = Paced(window, presenter, producer, 1.0, scale, repeats, origin, phase);
                Gpu.WriteLine($"period x{scale} repeatsOnly={repeats} phase {phase:F2}: {r}");
                Assert.Equal(0, r.Stale);
                var shown = Math.Max(r.Hz, r.EndHz);
                Assert.True(r.Accepted <= (shown * 1.05) + 2, $"{r.Accepted} presents accepted in 1 s on a {shown:F2} Hz display");
                if (r.Rebuilds != 0 || r.EndHz != r.Hz)
                {
                    continue;
                }

                Assert.Equal(0, r.Allocated);
                Assert.True(r.Worst.TotalMilliseconds < 5, $"TryPresent took {r.Worst.TotalMilliseconds:F2} ms");
                best = Math.Max(best, r.Accepted / (double)r.Calls);
                bestShare = Math.Max(bestShare, r.Accepted / r.Hz);
                clean++;
            }

            Assert.True(clean == 3, $"only {clean} windows at x{scale} ran without a target rebuild or a refresh change");
            var floor = scale >= 2.0 ? 0.97 : 0.85;
            if ((scale >= 1.0 ? best : bestShare) < floor)
            {
                SkipIfCompositorLimited(window, presenter, producer, $"best {(scale >= 1.0 ? best : bestShare):P1} at period x{scale}");
            }

            if (scale >= 2.0)
            {
                Assert.True(best >= 0.97, $"best {best:P1} accepted at half the display rate");
            }
            else if (scale >= 1.0)
            {
                Assert.True(best >= 0.85, $"best {best:P1} accepted at the display rate");
            }
            else
            {
                // Twice the display rate: about one per refresh fits, the rest is refused.
                Assert.True(bestShare >= 0.85, $"best {bestShare:P1} of the refreshes in 1 s shown at twice the display rate");
            }
        }

        Assert.InRange(presenter.Stats.MaxPresentsInFlight, 1, 2);
    });

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void A_resize_storm_never_blocks_for_long() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        Run(window, presenter, producer, TestPattern.MovingBar, 0.3);
        var worst = TimeSpan.Zero;
        var frame = 0;
        for (var i = 0; i < 12; i++)
        {
            window.SetSize(300 + (i * 17 % 150), 200 + (i * 29 % 120));
            for (var k = 0; k < 3; k++)
            {
                window.PumpEvents();
                var n = producer.WritePattern(TestPattern.MovingBar, frame++);
                var t = Stopwatch.GetTimestamp();
                presenter.TryPresent(Request(presenter.Ring!, n));
                var elapsed = Stopwatch.GetElapsedTime(t);
                worst = elapsed > worst ? elapsed : worst;
                Thread.Sleep(4);
            }
        }

        var (presented, _) = Run(window, presenter, producer, TestPattern.MovingBar, 0.3);
        Gpu.WriteLine($"resize storm: worst TryPresent {worst.TotalMilliseconds:F1} ms; {presenter.Stats}; swapchain {target.Width}x{target.Height}, window {window.PixelWidth}x{window.PixelHeight}");
        Assert.True(worst.TotalMilliseconds < 30, $"TryPresent took {worst.TotalMilliseconds:F1} ms during a resize");
        Assert.True(presented > 10);
        Assert.Equal((window.PixelWidth, window.PixelHeight), (target.Width, target.Height));
    });

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void A_window_hidden_without_the_presenter_knowing_never_blocks() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        Run(window, presenter, producer, TestPattern.MovingBar, 0.4);
        window.Minimize(); // and no event pump: the presenter keeps presenting to the swapchain
        var worst = TimeSpan.Zero;
        var start = Stopwatch.GetTimestamp();
        var frame = 1000;
        ulong last = 0;
        var period = TimeSpan.FromSeconds(1.0 / 120);
        var tick = 0;
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 1.5)
        {
            while (Stopwatch.GetElapsedTime(start) < period * tick)
            {
                Thread.SpinWait(20);
            }

            tick++;
            last = producer.WritePattern(TestPattern.MovingBar, frame++);
            var t = Stopwatch.GetTimestamp();
            presenter.TryPresent(Request(presenter.Ring!, last));
            var elapsed = Stopwatch.GetElapsedTime(t);
            worst = elapsed > worst ? elapsed : worst;
        }

        Gpu.WriteLine($"hidden, not told: {tick} calls, worst TryPresent {worst.TotalMilliseconds:F2} ms; {presenter.Stats}");
        Assert.True(worst.TotalMilliseconds < 5, $"TryPresent took {worst.TotalMilliseconds:F2} ms");
        Assert.True(producer.WaitReleased(last - 1, 1_000_000_000), "older writes must keep being released");

        window.PumpEvents();
        var n = producer.WritePattern(TestPattern.MovingBar, frame++);
        var sw = Stopwatch.StartNew();
        presenter.TryPresent(Request(presenter.Ring!, n));
        Gpu.WriteLine($"first TryPresent after learning of the minimise: {sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(sw.Elapsed.TotalMilliseconds < 30);
        sw.Restart();
        presenter.Dispose();
        Gpu.WriteLine($"presenter dispose: {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(sw.Elapsed.TotalMilliseconds < 2500);
    });
}
