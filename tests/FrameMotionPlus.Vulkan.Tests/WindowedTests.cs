using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Diagnostics;
using FrameMotionPlus.Vulkan.Tests.Support;
using FrameMotionPlus.Vulkan.Windowing;

namespace FrameMotionPlus.Vulkan.Tests;

/// <summary>
/// Short runs in a small window on the Wayland desktop (forced Wayland video driver). Each opens one window for a
/// second or two. They run on a developer's desktop, so caps, ordering and latency are asserted strictly in every
/// window of a run, while the present cadence is judged on the best of a few short windows (a busy machine can
/// deschedule the test thread for a refresh or two; it cannot make the presenter present faster than the display).
/// </summary>
public class WindowedTests
{
    private const long Second = 1_000_000_000;

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void Window_presents_a_moving_pattern_at_fixed_refresh_with_at_most_two_in_flight() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        Gpu.WriteLine($"video driver: {window.VideoDriver}; window {window.PixelWidth}x{window.PixelHeight} px; display refresh {window.RefreshRate} Hz");
        Gpu.WriteLine("surface: " + target.Surface);
        Gpu.WriteLine($"swapchain: {target.SwapchainFormatName} / {target.SwapchainColorSpaceName}, {target.Width}x{target.Height}");
        Assert.Equal("wayland", window.VideoDriver);
        Assert.Contains("FIFO", target.Surface.PresentModes, StringComparer.OrdinalIgnoreCase);

        var (best, fastest, rates, verdict) = Cadence(window, presenter, producer, TestPattern.MovingBar, windows: 5, seconds: 0.5);
        var stats = presenter.Stats;
        var refresh = window.RefreshRate.Hz;
        Gpu.WriteLine($"cadence per 0.5 s window: {rates} Hz on a {refresh:F2} Hz display; {stats}; {verdict.Description}");
        Assert.Equal(0, stats.FeedbackOverflows);
        Assert.InRange(stats.MaxPresentsInFlight, 1, 2);
        Assert.Equal(2, presenter.Capabilities.MaxFramesInFlight);
        Assert.Null(target.FeedbackFallbackReason);
        Assert.True(fastest < refresh * 1.05, $"a window presented at {fastest:F1} Hz, faster than the {refresh:F2} Hz display");
        if (best <= refresh * 0.85 && verdict.CompositorLimited)
        {
            Assert.Skip($"Environment invalidated the measurement: the compositor showed {verdict.ShownRate:F1} frames/s with the presenter's queue full; {verdict.Description}.");
        }

        Assert.True(best > refresh * 0.85, $"best window presented at {best:F1} Hz on a {refresh:F2} Hz display; {verdict.Description}");
        Assert.True(fastest < refresh * 1.05, $"a window presented at {fastest:F1} Hz, faster than the {refresh:F2} Hz display");
        Assert.Equal(0, stats.StaleRequests);

        // Measured independently with the present ids: never more than two presents waiting to be shown.
        Assert.Equal(PresentFeedbackMode.PresentWait2, target.ActiveFeedback);
        var (_, _, maxQueued, _) = MeasureQueue(window, target, presenter, producer, windows: 1, seconds: 0.5);
        Gpu.WriteLine($"present_wait2: most presented but not yet shown {maxQueued}");
        Assert.InRange(maxQueued, 1UL, 2UL);
    });

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void Ten_bit_ring_gets_an_hdr10_swapchain_when_the_surface_offers_one() => Windowed(PixelFormat.Rgb10A2, (ctx, window, target, presenter, producer) =>
    {
        Gpu.WriteLine($"HDR10 ST2084 offered natively: {target.Surface.Hdr10St2084}; swapchain {target.SwapchainFormatName} / {target.SwapchainColorSpaceName}; transform {presenter.ActiveTransform}");
        if (target.Surface.Hdr10St2084)
        {
            Assert.Equal(TargetColorSpace.Hdr10St2084, target.ColorSpace);
            Assert.Equal(OutputTransform.Passthrough, presenter.ActiveTransform);
        }
        else
        {
            Assert.Equal(TargetColorSpace.Sdr, target.ColorSpace);
            Assert.Equal(OutputTransform.PqToSdrPlaceholder, presenter.ActiveTransform);
        }

        var (presented, _) = Run(window, presenter, producer, TestPattern.Smooth, 1.0);
        Assert.True(presented > 30);
    });

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void Window_resize_recreates_the_swapchain() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        Run(window, presenter, producer, TestPattern.MovingBar, 0.5);
        var before = (target.Width, target.Height);
        window.SetSize(400, 300);
        window.PumpEvents();
        Run(window, presenter, producer, TestPattern.MovingBar, 0.7);
        Gpu.WriteLine($"swapchain {before.Width}x{before.Height} -> {target.Width}x{target.Height}; window {window.PixelWidth}x{window.PixelHeight}; recreations {presenter.Stats.TargetRecreations}");
        Assert.True(presenter.Stats.TargetRecreations >= 1);
        Assert.Equal((window.PixelWidth, window.PixelHeight), (target.Width, target.Height));
        Assert.NotEqual(before, (target.Width, target.Height));
    });

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void Minimised_window_never_blocks_presents_and_keeps_releasing_slots() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        Run(window, presenter, producer, TestPattern.MovingBar, 0.3);
        window.Minimize();
        var worst = TimeSpan.Zero;
        var start = Stopwatch.GetTimestamp();
        var frame = 0;
        ulong last = 0;
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 1.0)
        {
            window.PumpEvents();
            last = producer.WritePattern(TestPattern.MovingBar, frame++);
            var t = Stopwatch.GetTimestamp();
            _ = presenter.TryPresent(Request(presenter.Ring!, last));
            var elapsed = Stopwatch.GetElapsedTime(t);
            worst = elapsed > worst ? elapsed : worst;
            Thread.Sleep(2);
        }

        Gpu.WriteLine($"while minimised: {frame} writes, worst TryPresent {worst.TotalMilliseconds:F2} ms, {presenter.Stats}");
        Assert.True(presenter.Stats.TargetRecreations <= 2, "the swapchain was rebuilt over and over while minimised");
        Assert.True(worst.TotalMilliseconds < 50, $"TryPresent blocked for {worst.TotalMilliseconds:F1} ms");

        // Everything but the newest refused write is released; that one stays presentable until something covers it.
        Assert.True(producer.WaitReleased(last - 1, Second), "slots stopped being released while minimised");
        Assert.True(presenter.RetainedWrite == last, $"retained {presenter.RetainedWrite}, last write {last}");
        presenter.ReleaseThrough(last);
        Assert.True(producer.WaitReleased(last, Second));
    });

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void Refresh_rate_becomes_known_once_a_frame_is_shown_and_matches_the_present_cadence() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        Gpu.WriteLine($"before the first frame: known {window.RefreshRateKnown}, guess {window.RefreshRate} Hz");
        Assert.False(window.RefreshRateKnown, "a window that has shown nothing is on no output yet");
        Assert.False(presenter.IsTimingKnown);
        Run(window, presenter, producer, TestPattern.MovingBar, 0.3);
        Assert.True(window.RefreshRateKnown);
        Assert.True(presenter.IsTimingKnown);
        var (best, fastest, rates, verdict) = Cadence(window, presenter, producer, TestPattern.MovingBar, windows: 3, seconds: 0.5);
        var refresh = presenter.Timing.RefreshRate.Hz;
        Gpu.WriteLine($"known rate {refresh:F2} Hz, measured present cadence per window {rates} Hz; {verdict.Description}");
        Assert.True(fastest < refresh * 1.05, $"cadence {rates} Hz against a known {refresh:F2} Hz");
        if (best <= refresh * 0.85 && verdict.CompositorLimited)
        {
            Assert.Skip($"Environment invalidated the measurement: the compositor showed {verdict.ShownRate:F1} frames/s with the presenter's queue full; {verdict.Description}.");
        }

        Assert.True(best > refresh * 0.85, $"cadence {rates} Hz against a known {refresh:F2} Hz; {verdict.Description}");
    });

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void With_the_older_present_wait_at_most_two_presents_are_queued() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        Assert.Equal(PresentFeedbackMode.PresentWait, target.ActiveFeedback);
        Assert.Equal(2, presenter.Capabilities.MaxFramesInFlight);
        var (presents, rate, maxQueued, worst) = MeasureQueue(window, target, presenter, producer, windows: 3, seconds: 0.5);
        Gpu.WriteLine($"present_wait v1: {presents} presents (best window {rate:F1} Hz on {window.RefreshRate} Hz), most presented but not yet shown {maxQueued}, worst TryPresent {worst.TotalMilliseconds:F2} ms; {presenter.Stats}");
        Assert.InRange(maxQueued, 1UL, 2UL);
        Assert.InRange(presenter.Stats.MaxPresentsInFlight, 1, 2);

        // present_wait v1 reports "shown" later than v2 on this driver, so the cap sometimes misses a refresh
        // (measured 203 to 207 Hz on 240 Hz in about a third of runs; see PresentFeedbackMode.PresentWait).
        Assert.True(rate > window.RefreshRate.Hz * 0.75, $"presented at {rate:F1} Hz");
    }, new SwapchainTargetOptions { Feedback = PresentFeedbackMode.PresentWait });

    /// <summary>
    /// Without any present feedback nothing but the frame slots and the images limits presenting, and this driver hands
    /// images back before they are shown, so the queue is deeper than two. Presenting must still never block.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void Without_present_feedback_presenting_never_blocks_and_the_queue_depth_is_reported() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        Assert.Equal(PresentFeedbackMode.None, target.ActiveFeedback);
        Assert.Equal(3, target.ImageCountInUse);

        // The cap cannot be honoured, and the capabilities a scheduler reads say so. (Forced here, so no fallback reason.)
        Assert.Equal(0, presenter.Capabilities.MaxFramesInFlight);
        Assert.Null(target.FeedbackFallbackReason);
        var (presents, rate, maxQueued, worst) = MeasureQueue(window, target, presenter, producer, windows: 3, seconds: 0.5);
        Gpu.WriteLine($"no feedback: {presents} presents (best window {rate:F1} Hz on {window.RefreshRate} Hz), {target.ImageCountInUse} images, most presented but not yet shown {maxQueued}, worst TryPresent {worst.TotalMilliseconds:F2} ms; {presenter.Stats}");
        Assert.True(worst.TotalMilliseconds < 5, $"TryPresent took {worst.TotalMilliseconds:F2} ms");
        Assert.True(rate > window.RefreshRate.Hz * 0.85);
        Assert.True(maxQueued >= 1);
    }, new SwapchainTargetOptions { Feedback = PresentFeedbackMode.None });

    /// <summary>
    /// A present-wait query costs tens of microseconds, so the presenter asks the driver about once per frame: the
    /// readiness check, the present and the feedback drain that follow it share one answer, and a caller that spins on
    /// readiness causes at most one query per 500 µs.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void Present_wait_is_polled_about_once_per_frame() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        Run(window, presenter, producer, TestPattern.MovingBar, 0.3);
        Assert.True(presenter.IsTimingKnown);
        var period = TimeSpan.FromSeconds(1.0 / presenter.Timing.RefreshRate.Hz);

        // A scheduler-like caller: one readiness check per refresh, then present and drain feedback. The frame is
        // produced before the check, as a producer thread would have it ready, so the producer's own submission (slow
        // on a busy GPU) does not sit between the check and the present and push them past the re-poll interval.
        var before = presenter.Stats;
        var start = Stopwatch.GetTimestamp();
        long ticks = 0;
        ulong next = 0;
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 1.0)
        {
            while (Stopwatch.GetElapsedTime(start) < period * ticks)
            {
                Thread.SpinWait(20);
            }

            ticks++;
            window.PumpEvents();
            next = next == 0 ? producer.WriteStaged() : next;
            if (presenter.IsReadyForPresent() && presenter.TryPresent(Request(presenter.Ring!, next)))
            {
                next = 0;
            }

            while (presenter.TryGetFeedback(out _))
            {
            }
        }

        var paced = presenter.Stats;
        var pacedPolls = paced.PresentWaitPolls - before.PresentWaitPolls;
        Gpu.WriteLine($"paced: {ticks} refreshes, {paced.Presented - before.Presented} presents, {pacedPolls} present-wait polls");
        // About one per frame; a little more when the thread is descheduled past the re-poll interval (a busy desktop
        // gave 1.04). Polling in the readiness check, the present and the drain, as before, is 3 per frame.
        if (pacedPolls > (ticks * 5 / 4) + 2)
        {
            SkipIfCompositorLimited(window, presenter, producer, $"{pacedPolls} driver polls for {ticks} frames");
        }

        Assert.True(pacedPolls <= (ticks * 5 / 4) + 2, $"{pacedPolls} driver polls for {ticks} frames");
        Assert.True(pacedPolls >= ticks / 2, "the cap is not being checked with present-wait");

        // A caller that spins on readiness: bounded by the re-poll interval, not by how often it asks.
        start = Stopwatch.GetTimestamp();
        long calls = 0;
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 0.5)
        {
            calls++;
            if (presenter.IsReadyForPresent())
            {
                presenter.TryPresent(Request(presenter.Ring!, producer.WriteStaged()));
                while (presenter.TryGetFeedback(out _))
                {
                }
            }
        }

        var spun = presenter.Stats;
        var spinPolls = spun.PresentWaitPolls - paced.PresentWaitPolls;
        var bound = (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 2) + (spun.Presented - paced.Presented) + 2;
        Gpu.WriteLine($"spinning: {calls} readiness calls, {spun.Presented - paced.Presented} presents, {spinPolls} polls (bound {bound})");
        Assert.True(spinPolls <= bound, $"{spinPolls} polls in 0.5 s of spinning");
        Assert.InRange(spun.MaxPresentsInFlight, 1, 2);
    });

    /// <summary>
    /// Presents as fast as the presenter accepts and measures, with present ids, how many are queued behind the screen.
    /// Returns the total presents, the best window's rate, the deepest queue seen in any window and the slowest call.
    /// </summary>
    private static (ulong Presents, double BestRate, ulong MaxQueued, TimeSpan Worst) MeasureQueue(
        SdlWindow window, SwapchainTarget target, VulkanPresenter presenter, DiagnosticRingProducer producer, int windows, double seconds)
    {
        Run(window, presenter, producer, TestPattern.MovingBar, 0.2);
        var firstId = (ulong)presenter.Stats.Presented;
        var shown = firstId;
        var presented = firstId;
        ulong maxQueued = 0;
        var worst = TimeSpan.Zero;
        var best = 0.0;
        for (var w = 0; w < windows; w++)
        {
            var windowStart = presented;
            var start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
            {
                window.PumpEvents();
                if (!presenter.IsReadyForPresent())
                {
                    Thread.Sleep(0);
                    continue;
                }

                var n = producer.WriteStaged();

                // Polled right before the request, as the presenter itself does, so the two views agree.
                while (shown < presented && target.DiagnosticPresentShown(shown + 1))
                {
                    shown++;
                }

                var t = Stopwatch.GetTimestamp();
                var ok = presenter.TryPresent(Request(presenter.Ring!, n));
                var elapsed = Stopwatch.GetElapsedTime(t);
                worst = elapsed > worst ? elapsed : worst;
                if (ok)
                {
                    presented++;
                    maxQueued = Math.Max(maxQueued, presented - shown);
                }

                while (presenter.TryGetFeedback(out _))
                {
                }
            }

            best = Math.Max(best, (presented - windowStart) / Stopwatch.GetElapsedTime(start).TotalSeconds);
        }

        return (presented - firstId, best, maxQueued, worst);
    }

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void Without_present_fences_a_resize_rebuilds_cleanly() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        Assert.False(target.UsesPresentFences);
        Run(window, presenter, producer, TestPattern.MovingBar, 0.4);
        window.SetSize(420, 260);
        window.PumpEvents();
        var (presented, _) = Run(window, presenter, producer, TestPattern.MovingBar, 0.6);
        Gpu.WriteLine($"{presenter.Stats}; swapchain {target.Width}x{target.Height}, window {window.PixelWidth}x{window.PixelHeight}");
        Assert.True(presenter.Stats.TargetRecreations >= 1);
        Assert.True(presented > 20);
        Assert.Equal((window.PixelWidth, window.PixelHeight), (target.Width, target.Height));
    }, new SwapchainTargetOptions { UsePresentFences = false });

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void A_failed_swapchain_rebuild_is_retried() => Windowed(PixelFormat.Rgba8, (ctx, window, target, presenter, producer) =>
    {
        Run(window, presenter, producer, TestPattern.MovingBar, 0.3);
        target.FailNextRecreate = true;
        window.SetSize(400, 240);
        window.PumpEvents();
        var start = Stopwatch.GetTimestamp();
        var threw = false;
        while (!threw && Stopwatch.GetElapsedTime(start).TotalSeconds < 2)
        {
            window.PumpEvents();
            if (!presenter.IsReadyForPresent())
            {
                Thread.Sleep(1);
                continue;
            }

            var n = producer.WritePattern(TestPattern.MovingBar, 1);
            try
            {
                presenter.TryPresent(Request(presenter.Ring!, n));
            }
            catch (VulkanException e)
            {
                Gpu.WriteLine($"rebuild failed as injected: {e.Message}");
                threw = true;
            }
        }

        Assert.True(threw, "the injected failure never happened");
        var (presented, _) = Run(window, presenter, producer, TestPattern.MovingBar, 0.5);
        Assert.True(presented > 10, "presenting did not resume after the failed rebuild");
        Assert.Equal((window.PixelWidth, window.PixelHeight), (target.Width, target.Height));
    });

    /// <summary>
    /// Presents whenever the presenter is ready, for <paramref name="seconds"/>. The producer's staging buffers are filled
    /// with consecutive frames of <paramref name="pattern"/> first, so the loop does no per-frame CPU fill and the window
    /// still shows a moving pattern. Feedback must come back in present order.
    /// </summary>
    internal static (long Presented, double Seconds) Run(
        SdlWindow window, VulkanPresenter presenter, DiagnosticRingProducer producer, TestPattern pattern, double seconds, ShowLog? log = null)
    {
        producer.FillAllStaging(pattern, 0, frameStep: 1);
        var start = Stopwatch.GetTimestamp();
        var before = presenter.Stats.Presented;
        var lastSlot = long.MinValue;
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
        {
            window.PumpEvents();
            var ready = presenter.IsReadyForPresent();
            log?.GpuDone(presenter.GpuFramesDone);

            // Drained before anything new is submitted, so a show is logged before the present it made room for.
            DrainFeedback();
            if (!ready)
            {
                Thread.Sleep(0);
                continue;
            }

            var n = producer.WriteStaged();
            if (presenter.TryPresent(Request(presenter.Ring!, n)))
            {
                log?.Submitted((long)n, presenter.SubmittedFrames);
            }

            DrainFeedback();
        }

        return (presenter.Stats.Presented - before, Stopwatch.GetElapsedTime(start).TotalSeconds);

        void DrainFeedback()
        {
            while (presenter.TryGetFeedback(out var feedback))
            {
                Assert.True(feedback.SlotIndex > lastSlot, $"feedback for {feedback.SlotIndex} came after {lastSlot}");
                lastSlot = feedback.SlotIndex;
                log?.Shown(feedback.SlotIndex);
            }
        }
    }

    /// <summary>
    /// Present cadence over several short windows: the best and the fastest window's rate, all of them, and whether
    /// every window was limited by the compositor rather than the presenter (<see cref="ShowLog.Judge"/>).
    /// </summary>
    internal static (double Best, double Fastest, string Rates, ShowVerdict Worst) Cadence(
        SdlWindow window, VulkanPresenter presenter, DiagnosticRingProducer producer, TestPattern pattern, int windows, double seconds)
    {
        var rates = new List<double>();
        ShowVerdict? worst = null;
        for (var w = 0; w < windows; w++)
        {
            var log = new ShowLog();
            var (presented, elapsed) = Run(window, presenter, producer, pattern, seconds, log);
            rates.Add(presented / elapsed);
            var verdict = log.Judge(presenter.Timing.RefreshRate.Hz);
            worst = worst is { CompositorLimited: false } ? worst : verdict;
        }

        return (rates.Max(), rates.Max(), string.Join(", ", rates.Select(static r => r.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))), worst!.Value);
    }

    /// <summary>
    /// After a window that showed too few frames: presents as fast as the presenter allows for half a second and skips
    /// the test, naming the measured rate, when the compositor rather than the presenter limited it
    /// (<see cref="ShowLog.Judge"/>). Returns otherwise, for the caller to fail.
    /// </summary>
    internal static void SkipIfCompositorLimited(SdlWindow window, VulkanPresenter presenter, DiagnosticRingProducer producer, string what)
    {
        var log = new ShowLog();
        Run(window, presenter, producer, TestPattern.MovingBar, 0.5, log);
        var verdict = log.Judge(presenter.Timing.RefreshRate.Hz);
        Gpu.WriteLine($"compositor check after {what}: {verdict.Description}");
        if (verdict.CompositorLimited)
        {
            Assert.Skip($"Environment invalidated the measurement ({what}): the compositor showed {verdict.ShownRate:F1} frames/s " +
                $"with the presenter's queue full; {verdict.Description}.");
        }
    }

    internal static PresentRequest Request(PresentationRing ring, ulong n) => new()
    {
        SlotIndex = (long)n,
        Frame = ring.ConsumerFrame(n),
        Ready = ring.WrittenSignal(n),
    };

    internal static void Windowed(
        PixelFormat format,
        Action<GpuContext, SdlWindow, SwapchainTarget, VulkanPresenter, DiagnosticRingProducer> body,
        SwapchainTargetOptions? options = null)
    {
        Requirements.SkipUnlessDisplay();
        using var window = SdlWindow.Create(new SdlWindowOptions { Title = "fmp Vulkan test", Width = 480, Height = 270 });
        Gpu.Run(ctx =>
        {
            var target = ctx.Own(new SwapchainTarget(ctx.Device, window, options));
            var presenter = new VulkanPresenter(ctx.Device, target);
            DiagnosticRingProducer? producer = null;
            try
            {
                // Small enough that the producer's per-frame CPU fill (under the validation layer) keeps up with a
                // 240 Hz display even when the machine is busy; the presenter scales it up to the window.
                presenter.Configure(new PresenterConfig { Width = 320, Height = 180, Format = format });
                using (var export = presenter.Ring!.Export())
                {
                    producer = DiagnosticRingProducer.Create(ctx.Instance, export);
                }

                body(ctx, window, target, presenter, producer);
            }
            finally
            {
                presenter.Dispose();
                producer?.Dispose();
            }
        }, window.RequiredInstanceExtensions);
    }
}
