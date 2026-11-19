using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// The present thread's window logic against a model of the compositor's FIFO (<see cref="SimulatedCompositor"/>: an
/// offscreen target whose first-pixel reports come from the model, on the real clock): a 60 fps clip, blend 2x at 119.88
/// Hz, a few seconds each. Each run is judged by the live accounting and by the telemetry verifier, which must agree.
/// </summary>
[Trait(Category.Name, Category.Gpu)]
public class SimulatedDisplayTests
{
    private static readonly FrameRate Hz120 = new(120_000, 1001);
    private static readonly FrameRate Hz240 = FrameRate.FromDouble(240.016);

    /// <summary>
    /// The Phase 5 review's 180 s 4K run (r4k.csv): at 119.88 Hz with the lead of then (12.5 ms, 1.5 refreshes; the latch
    /// 2 ms), the compositor misses a refresh with presents queued in time, shows the queued one a refresh late, and at the
    /// next latch, with two presents ready, discards the older to give the refresh back (as KWin did there). The present
    /// thread must not give the refresh back as well: before, it dropped the next slot too, the FIFO went a refresh ahead
    /// and the next two slots were shown a refresh early (early and dropped ones the pipeline's). Now each missed refresh
    /// costs the late slot and the discarded one, the display's; nothing early.
    /// </summary>
    [Fact]
    public void When_the_compositor_gives_a_missed_refresh_back_the_present_thread_does_not_as_well()
    {
        var cuda = Hardware.RequireGpu();
        var skips = new SkipSchedule(2.5, 3.5, 4.5);
        var sim = new SimulatedCompositor(Hz120) { SkipsRefresh = skips.Skips, GiveBackAtLatch = 2, LatchNs = static _ => 2_000_000 };
        var report = Run(cuda, sim, 6, leadNs: 12_500_000);
        var (near, other, v) = Judge(report, sim, skips);

        // The scenario: the compositor gave refreshes back itself.
        Assert.True(sim.Discarded >= 1, "the compositor never gave a refresh back: the scenario was not exercised");

        // Each missed refresh: the slot meant for it (late) and the one discarded to give the refresh back; nothing early and
        // nothing dropped by the present thread (the double give-back's signature: before the fix, Dropped then Early).
        // Whose: the display's when the late slot's hand-over is provably as early as one that made its refresh, else the
        // pipeline's late present and its consequence (the gate's conservative rule). A generated slot's hand-over is when
        // its content was seen written, a present-thread turn coarse, so a missed refresh that lands on one is often not
        // provable: logged, not asserted.
        Assert.InRange(near.Count, 2, 9);
        Assert.Equal(0, v.EarlySlots);
        Assert.DoesNotContain(near, m => m.Kind is MissKind.Early or MissKind.Dropped);
        Assert.All(near, m => Assert.True(m.Kind is MissKind.DisplayLate or MissKind.CatchUp or MissKind.Discarded or MissKind.LatePresent or MissKind.NotShown, $"{m.Kind} at {m.AtNs / 1e9:0.000} s"));
        Assert.True(other.Count <= 3, $"{other.Count} misses away from the missed refreshes");
    }

    /// <summary>
    /// The same at 240 Hz, whose latch (4.4 ms before the refresh, measured) is more than a refresh, so the lead is too: the
    /// in-flight cap holds the next present until the late one is reported, so two presents are seldom ready at a latch and
    /// the compositor cannot give the refresh back; the present thread does, once a present submitted after the late show's
    /// report is late too (3 to 4 refreshes: up to 7 slots each). Who gave it back is logged, not asserted (a present the cap
    /// held past its refresh is given up too). That someone did, each time, is the bound on misses away from the events
    /// (else the FIFO would stay behind and every later slot be late); that not both did, no early show.
    /// </summary>
    [Fact]
    public void At_240_hz_a_missed_refresh_is_given_back_once_and_costs_a_few_slots_the_displays()
    {
        var cuda = Hardware.RequireGpu();
        var skips = new SkipSchedule(2.5, 3.5, 4.5);
        var sim = new SimulatedCompositor(Hz240) { SkipsRefresh = skips.Skips, GiveBackAtLatch = 2, LatchNs = static _ => 4_440_000 };
        var report = Run(cuda, sim, 6, leadNs: 6_250_000);
        var (near, other, v) = Judge(report, sim, skips);

        Assert.InRange(near.Count, 3, 21);
        Assert.Equal(0, v.EarlySlots);
        Assert.DoesNotContain(near, m => m.Kind == MissKind.Early);
        Assert.True(other.Count <= 3, $"{other.Count} misses away from the missed refreshes");
    }

    /// <summary>Logs a run against a schedule of missed refreshes and checks what every such run must: the compositor missed
    /// each scheduled refresh, and the live accounting and the verifier agree.</summary>
    private static (List<MissEvent> Near, List<MissEvent> Other, VerificationResult V) Judge(PlaybackReport report, SimulatedCompositor sim, SkipSchedule skips)
    {
        var s = report.Statistics;
        var v = Assert.IsType<VerificationResult>(report.Verification);
        Log.WriteLine($"compositor: {sim.Skipped} refreshes missed, {sim.Discarded} presents discarded; verifier {v.Misses} missed: {v.Describe(30)}");
        Log.WriteLine($"live: {string.Join(", ", s.MissEvents.ToArray().Select(m => $"{m.AtNs / 1e9:0.000} s {m.Kind}"))}; catch-ups: compositor {report.CompositorCatchUps}, own {report.OwnCatchUps}; dropped late {report.DroppedLate}");
        Assert.Equal(skips.Count, sim.Skipped);
        Assert.Equal(0, v.Disagreements);
        var (near, other) = skips.Split(report);
        Log.WriteLine($"near the missed refreshes: {string.Join(", ", near.Select(m => m.Kind))}; elsewhere (host scheduling): {string.Join(", ", other.Select(m => m.Kind))}");
        return (near, other, v);
    }

    /// <summary>
    /// A compositor that stays a refresh behind after missing one (a strict FIFO; KWin with one present queued, as at the
    /// 119.88 Hz output's 2 ms latch): the present thread gives the refresh back itself, once, nothing early. Whose the misses
    /// are is logged, not asserted: a missed refresh is the display's only when the late slot's hand-over is provably as
    /// early as one that made its refresh, and after one late show that stays evidence that this lead can miss until a later
    /// present provably made its refresh with less, which presents at one lead seldom do; so the second, 1.5 s later, is
    /// often the pipeline's (conservative, as the gate's rule asks).
    /// </summary>
    [Fact]
    public void When_the_compositor_stays_behind_the_present_thread_gives_the_refresh_back()
    {
        var cuda = Hardware.RequireGpu();
        var skips = new SkipSchedule(2.5, 4.0);
        var sim = new SimulatedCompositor(Hz120) { SkipsRefresh = skips.Skips, GivesBack = false, LatchNs = static _ => 2_000_000 };
        var report = Run(cuda, sim, 6, leadNs: 4_000_000);
        var s = report.Statistics;
        var v = Assert.IsType<VerificationResult>(report.Verification);
        Log.WriteLine($"compositor: {sim.Skipped} missed, {sim.Discarded} discarded; verifier {v.Misses} missed: {v.Describe(30)}");
        Log.WriteLine($"live: {string.Join(", ", s.MissEvents.ToArray().Select(m => $"{m.AtNs / 1e9:0.000} s {m.Kind}"))}; catch-ups: compositor {report.CompositorCatchUps}, own {report.OwnCatchUps}");
        Assert.Equal(2, sim.Skipped);
        Assert.Equal(0, sim.Discarded);
        Assert.Equal(0, v.Disagreements);
        Assert.Equal(0, v.EarlySlots);
        Assert.True(report.OwnCatchUps >= 2, "the present thread never gave the refresh back");
        var (near, other) = skips.Split(report);
        Log.WriteLine($"near the missed refreshes: {string.Join(", ", near.Select(m => m.Kind))}; elsewhere (host scheduling): {string.Join(", ", other.Select(m => m.Kind))}");

        // Each missed refresh: the slot meant for it and the one queued behind it (late), and the one given up to catch
        // up (with one present queued the present thread gives the refresh back at once); nothing early.
        Assert.InRange(near.Count, 2, 6);
        Assert.DoesNotContain(near, m => m.Kind == MissKind.Early);
        Assert.True(other.Count <= 3, $"{other.Count} misses away from the missed refreshes");
    }

    /// <summary>
    /// The windowed test of the same name, made deterministic about the compositor: a lead of 1.5 ms to start from on a
    /// 240 Hz output whose latch is 4.4 ms (DP-3, measured). The presents decided with it miss their latch; each such late
    /// show of the pipeline's raises the lead a step, at once, until it is past the latch, and the misses stop: none of the
    /// pipeline's in the second half.
    /// </summary>
    [Fact]
    public void A_short_starting_lead_rises_past_the_latch_and_the_misses_stop()
    {
        var cuda = Hardware.RequireGpu();
        var sim = new SimulatedCompositor(Hz240) { LatchNs = static _ => 4_440_000 };
        var report = Run(cuda, sim, 5, leadNs: 1_500_000);
        var s = report.Statistics;
        var v = Assert.IsType<VerificationResult>(report.Verification);
        // Miss times count from the start of playback; the measured window starts after the 0.5 s warm-up.
        var half = ((s.LastMeasuredNs - s.FirstMeasuredNs) / 2) + 500_000_000;
        var second = s.MissEvents.ToArray().Where(m => m.AtNs > half && m.Kind is MissKind.LatePresent or MissKind.Dropped).ToList();
        Log.WriteLine($"lead changes {string.Join(" → ", report.LeadChanges.Select(c => $"{c.Why} {c.LeadNs / 1e6:0.000}"))}; misses {string.Join(", ", s.MissEvents.ToArray().Select(m => $"{m.AtNs / 1e9:0.000} s {m.Kind}"))}; second half from {half / 1e9:0.000} s");
        Assert.Equal(0, v.Disagreements);
        Assert.True(report.LeadRaises >= 1, "the lead never went up");
        Assert.True(report.FinalLeadNs > 4_440_000, $"lead {report.FinalLeadNs} at the end, under the latch");
        Assert.True(second.Count == 0, $"{second.Count} misses of the pipeline's in the second half: {string.Join(", ", second.Select(m => $"{m.AtNs / 1e9:0.000} s {m.Kind}"))}");
    }

    /// <summary>
    /// Before playback the present thread measures where the compositor takes a present (probes of the last pre-roll image
    /// into an empty queue), and the present lead starts there plus a margin instead of 1.5 refreshes: at a 2 ms latch on
    /// 119.88 Hz, about 3.5 ms instead of 12.5, and every slot still makes its refresh.
    /// </summary>
    [Fact]
    public void The_present_lead_starts_from_the_latch_measured_before_playback()
    {
        var cuda = Hardware.RequireGpu();
        var sim = new SimulatedCompositor(Hz120) { LatchNs = static _ => 2_000_000 };
        var report = Run(cuda, sim, 4);
        var sweep = Assert.IsType<LatchSweepResult>(report.LatchSweep);
        var v = Assert.IsType<VerificationResult>(report.Verification);
        Log.WriteLine($"sweep: {sweep}; lead at the end {report.FinalLeadNs / 1e6:0.000} ms; changes {string.Join(" → ", report.LeadChanges.Select(c => $"{c.Why} {c.LeadNs / 1e6:0.000}"))}");
        Assert.True(sweep.Bracketed);
        // The model takes a present 2 ms before its refresh, 0.16 ms before the first pixel, 0.2 ms after its submission.
        Assert.InRange(sweep.LateMaxNs, 1_900_000, 2_700_000);
        Assert.InRange(report.FinalLeadNs, 3_900_000, 4_700_000);
        Assert.Equal(0, report.Statistics.TotalMisses);
        Assert.Equal(0, v.Misses);
        Assert.Equal(0, v.Disagreements);

        // Every change reported: the start once the output's rate is known (1.5 refreshes), then the measured lead.
        Assert.Equal([LeadChange.Start, LeadChange.Measured], report.LeadChanges.Select(c => c.Why).ToArray());
        Assert.InRange(report.LeadChanges[0].LeadNs, 12_400_000, 12_600_000);
        Assert.Equal(report.FinalLeadNs, report.LeadChanges[^1].LeadNs);
    }

    /// <summary>
    /// The compositor's latch moves 3 ms earlier during playback (its own frames got heavier): the presents handed over at
    /// the old lead miss it, those are the pipeline's, the lead goes up at once (a step at a time) and the misses stop.
    /// </summary>
    [Fact]
    public void When_the_latch_moves_earlier_the_lead_follows_and_the_misses_stop()
    {
        var cuda = Hardware.RequireGpu();
        var change = new SkipSchedule(2.5) { WithinNs = 500_000_000 };
        var moved = false;
        var sim = new SimulatedCompositor(Hz120)
        {
            LatchNs = r =>
            {
                moved |= change.Skips(r);
                return moved ? 5_000_000 : 2_000_000;
            },
        };
        var report = Run(cuda, sim, 6);
        var s = report.Statistics;
        var v = Assert.IsType<VerificationResult>(report.Verification);
        Log.WriteLine($"lead changes {string.Join(" → ", report.LeadChanges.Select(c => $"{c.Why} {c.LeadNs / 1e6:0.000}"))}; misses {string.Join(", ", s.MissEvents.ToArray().Select(m => $"{m.AtNs / 1e9:0.000} s {m.Kind}"))}");
        Assert.Equal(0, v.Disagreements);
        Assert.True(report.LeadRaises >= 1, "the lead never went up");
        Assert.True(report.FinalLeadNs > 5_000_000, $"lead {report.FinalLeadNs} at the end, under the latch");
        var (near, other) = change.Split(report);
        Log.WriteLine($"after the latch moved: {near.Count} ({string.Join(", ", near.Select(m => m.Kind))}); elsewhere (host scheduling): {other.Count}");
        Assert.InRange(near.Count, 1, 20);
        Assert.True(other.Count <= 3, $"{other.Count} misses after the lead caught up with the latch");
    }

    /// <summary>
    /// Variable refresh (the design doc's preferred clock): a 60 fps clip at 2x on a simulated 240 Hz VRR display. Slots
    /// follow the source, each handed over at its time and shown the display's delay later: every slot within half the
    /// shortest refresh period of its time, intervals as even as the source's (a sixtieth of a second over two), and the
    /// delay to light the scheduler's D plus that delay, with no present lead and no rounding to a refresh.
    /// </summary>
    [Fact]
    public void Variable_refresh_shows_each_slot_at_its_own_time()
    {
        var cuda = Hardware.RequireGpu();
        var sim = new SimulatedCompositor(Hz240) { Vrr = true };
        var report = Run(cuda, sim, 4, refresh: RefreshMode.Vrr, clip: "c60-mp4");
        var s = report.Statistics;
        var v = Assert.IsType<VerificationResult>(report.Verification);
        Log.WriteLine($"{report.RefreshModeUsed}: intervals mean {s.OutputIntervals.Mean / 1e6:0.000} sd {s.OutputIntervals.StdDev / 1e6:0.000} ms; real frames {s.RealFrameLatency.Percentile(0.5) / 1e6:0.000} ms after content (D {s.PipelineDelays.Percentile(0.5) / 1e6:0.000}); misses {s.TotalMisses}; verifier {v.Misses}, {v.Describe()}");
        Assert.Equal(RefreshMode.Vrr, report.RefreshModeUsed);
        Assert.Equal(0, v.Disagreements);
        Assert.True(v.Judged > 300, $"{v.Judged} judged");
        Assert.True(s.TotalMisses <= 2, $"{s.TotalMisses} misses");
        Assert.InRange(s.OutputIntervals.Mean, 8_200_000, 8_500_000);
        Assert.True(s.OutputIntervals.StdDev < 500_000, $"interval sd {s.OutputIntervals.StdDev / 1e6:0.000} ms");
        var light = s.RealFrameLatency.Percentile(0.5) - s.PipelineDelays.Percentile(0.5);
        Assert.InRange(light, 500_000, 1_500_000);
    }

    /// <summary>
    /// The owner's two-hour run (30 → 60 under variable refresh on a 119.88 Hz output, a hand-over-to-first-pixel delay
    /// measured between 0.6 and 3.1 ms): a delay that varies over that range misses nothing (half the shortest period,
    /// 4.17 ms, covers it), and the live counters and the verifier agree. A stretch where the output shows on a fixed
    /// 119.88 Hz grid instead (each present waits for the next refresh) makes most of its slots late by the verifier's
    /// definition with the intervals still even: both accountings count the same slots, and the live one gives them to the
    /// display (handed over in time), not to the pipeline.
    /// </summary>
    [Fact]
    public void Variable_refresh_on_a_119_88_Hz_output_with_a_varying_display_delay_is_judged_alike_by_both_accountings()
    {
        var cuda = Hardware.RequireGpu();
        static long Varying(long handoverNs) => 400_000 + (long)(unchecked((ulong)handoverNs * 0x9E3779B97F4A7C15UL) % 2_700_000UL); // 0.4 to 3.1 ms
        var varying = Run(cuda, new SimulatedCompositor(Hz120) { Vrr = true, VrrDelayAt = Varying }, 4, refresh: RefreshMode.Vrr, clip: "c30");
        var v1 = Assert.IsType<VerificationResult>(varying.Verification);
        Log.WriteLine($"varying 0.4-3.1 ms: misses {varying.Statistics.TotalMisses}, verifier {v1.Misses} of {v1.Judged}, disagreements {v1.Disagreements}; intervals sd {varying.Statistics.OutputIntervals.StdDev / 1e6:0.000} ms");
        Assert.Equal(RefreshMode.Vrr, varying.RefreshModeUsed);
        Assert.Equal(0, v1.Disagreements);
        Assert.True(v1.Judged > 150, $"{v1.Judged} judged");
        Assert.True(varying.Statistics.TotalMisses <= 2, $"{varying.Statistics.TotalMisses} misses");

        // From 2 s on, the output follows a fixed 119.88 Hz grid (variable refresh off for the window, the app unaware).
        var period = Hz120.PeriodNs;
        long? start = null;
        long Gridded(long handoverNs)
        {
            start ??= handoverNs;
            if (handoverNs - start < 2 * Nanoseconds.PerSecond)
            {
                return 750_000;
            }

            var earliest = handoverNs + 750_000;
            var next = (long)(Math.Ceiling(earliest / period) * period);
            return next - handoverNs;
        }

        var gridded = Run(cuda, new SimulatedCompositor(Hz120) { Vrr = true, VrrDelayAt = Gridded }, 5, refresh: RefreshMode.Vrr, clip: "c30");
        var v2 = Assert.IsType<VerificationResult>(gridded.Verification);
        var s = gridded.Statistics;
        Log.WriteLine($"fixed grid after 2 s: misses {s.TotalMisses} (late present {s.MissCount(MissKind.LatePresent)}, display late {s.MissCount(MissKind.DisplayLate)}), verifier {v2.Misses} of {v2.Judged}, live over the same {v2.LiveMisses}, disagreements {v2.Disagreements}; intervals p99 {s.OutputIntervals.Percentile(0.99) / 1e6:0.000} ms, sd {s.OutputIntervals.StdDev / 1e6:0.000} ms");
        Assert.Equal(0, v2.Disagreements);
        Assert.Equal(v2.Misses, v2.LiveMisses);
        Assert.True(v2.Misses > 0, "the fixed grid made no slot late");
        Assert.Equal(0, s.MissCount(MissKind.LatePresent));
    }

    /// <summary>
    /// Who is blamed for a late show under variable refresh (119.88 Hz, 30 → 60): the pipeline when its hand-over was later
    /// than the display's excess over its expected delay, or more than a quarter of the tolerance late (1.04 ms here); else
    /// the display. Every hand-over held back 2 ms or 3.5 ms past its time (a generator or
    /// present thread running late) with the display's delay varying 0.4–3.1 ms: the late shows are the pipeline's. Every
    /// hand-over in time and one show in ten taking 6 ms instead of 0.8 (the owner's two-hour run: hand-overs 0.3 ms behind,
    /// the display 3.75 ms or more over): the display's.
    /// </summary>
    [Fact]
    public void A_late_show_under_variable_refresh_is_blamed_on_a_late_hand_over_or_a_slow_display()
    {
        var cuda = Hardware.RequireGpu();
        static long Varying(long handoverNs) => 400_000 + (long)(unchecked((ulong)handoverNs * 0x9E3779B97F4A7C15UL) % 2_700_000UL);
        static Action<PresentJob> HeldBack(long byNs) => job =>
        {
            if (job.SlotIndex < 0 || job.Kind == PresentKind.PreRoll)
            {
                return;
            }

            var until = (job.DueNs > 0 ? job.DueNs : job.TargetNs - 1_000_000) + byNs;
            while (FrameMotionPlus.Platform.Time.Monotonic.NowNs < until)
            {
                Thread.SpinWait(50);
            }
        };

        foreach (var late in new[] { 2_000_000L, 3_500_000L })
        {
            var report = Run(cuda, new SimulatedCompositor(Hz120) { Vrr = true, VrrDelayAt = Varying }, 6, refresh: RefreshMode.Vrr, clip: "c30", beforePresent: HeldBack(late));
            var s = report.Statistics;
            var v = Assert.IsType<VerificationResult>(report.Verification);
            Log.WriteLine($"hand-over {late / 1e6:0.0} ms late: {s.TotalMisses} missed of {s.Slots} (late present {s.MissCount(MissKind.LatePresent)}, display late {s.MissCount(MissKind.DisplayLate)}, dropped {s.MissCount(MissKind.Dropped)}); verifier {v.Misses}, disagreements {v.Disagreements}");
            Assert.Equal(0, v.Disagreements);
            Assert.True(s.TotalMisses > 0, $"nothing missed with every hand-over {late / 1e6:0.0} ms late");
            Assert.Equal(0, s.MissCount(MissKind.DisplayLate));
        }

        static long SlowEveryTenth(long handoverNs) => unchecked((ulong)handoverNs * 0x9E3779B97F4A7C15UL) % 10UL == 0 ? 6_000_000 : 800_000;
        var slow = Run(cuda, new SimulatedCompositor(Hz120) { Vrr = true, VrrDelayAt = SlowEveryTenth }, 6, refresh: RefreshMode.Vrr, clip: "c30");
        var ss = slow.Statistics;
        var vs = Assert.IsType<VerificationResult>(slow.Verification);
        Log.WriteLine($"display slow one show in ten: {ss.TotalMisses} missed of {ss.Slots} (late present {ss.MissCount(MissKind.LatePresent)}, display late {ss.MissCount(MissKind.DisplayLate)}); verifier {vs.Misses}, disagreements {vs.Disagreements}");
        Assert.Equal(0, vs.Disagreements);
        Assert.True(ss.MissCount(MissKind.DisplayLate) > 0, "the slow display made nothing late");
        Assert.Equal(0, ss.MissCount(MissKind.LatePresent));
    }

    /// <summary>
    /// Many missed slots and a verifier's buffer of 1,024 rows that fills in the run, in both clock modes: the live counters
    /// and the verifier agree over the slots it held (no disagreement), and the slots after them are reported as the live
    /// counters' alone. Variable refresh: every slot late (the display 6 ms behind every hand-over on 240 Hz). Fixed refresh:
    /// two presents in three dropped, so more slots are missed than the buffer has rows.
    /// </summary>
    [Fact]
    public void A_run_longer_than_the_verifiers_buffer_with_many_misses_agrees_over_what_it_judged()
    {
        var cuda = Hardware.RequireGpu();
        var vrr = Run(cuda, new SimulatedCompositor(Hz240) { Vrr = true, VrrDelayAt = static _ => 6_000_000 }, 12, refresh: RefreshMode.Vrr, clip: "c60-mp4", verifierRows: 1024);
        var drops = 0;
        var fixedRun = Run(cuda, new SimulatedCompositor(Hz240), 8, verifierRows: 1024, dropPresent: job => job.SlotIndex >= 0 && ++drops % 3 != 0);
        foreach (var (name, report) in new[] { ("variable", vrr), ("fixed", fixedRun) })
        {
            var s = report.Statistics;
            var v = Assert.IsType<VerificationResult>(report.Verification);
            Log.WriteLine($"{name}: {s.TotalMisses} missed of {s.Slots}; verifier judged {v.Judged} ({report.VerifierJudgedNs / 1e9:0.0} s), {v.Misses} missed against the live counters' {v.LiveMisses} over them, disagreements {v.Disagreements}; beyond its buffer {report.VerifierBeyondSlots} slots ({report.VerifierRowsDropped} rows)");
            Assert.Equal(0, v.Disagreements);
            Assert.Equal(v.Misses, v.LiveMisses);
            Assert.True(report.VerifierRowsDropped > 0, $"{name}: the buffer did not fill");
            Assert.True(report.VerifierBeyondSlots > 0, $"{name}: no slot reported beyond the buffer");
            Assert.True(v.Judged > 300, $"{name}: {v.Judged} judged");
            Assert.True(s.TotalMisses > 1024, $"{name}: only {s.TotalMisses} missed");
        }
    }

    /// <summary>Nothing missed: no miss, the verifier agrees, the lead holds.</summary>
    [Fact]
    public void A_compositor_that_misses_nothing_shows_every_slot_at_its_refresh()
    {
        var cuda = Hardware.RequireGpu();
        var sim = new SimulatedCompositor(Hz120);
        var report = Run(cuda, sim, 4, leadNs: 8_000_000);
        var v = Assert.IsType<VerificationResult>(report.Verification);
        Assert.Equal(0, report.Statistics.TotalMisses);
        Assert.Equal(0, v.Misses);
        Assert.Equal(0, v.Disagreements);
        Assert.True(v.Judged > 300, $"{v.Judged} judged");
    }

    internal static PlaybackReport Run(Cuda.CudaContext cuda, SimulatedCompositor sim, double seconds, long leadNs = -1, string clip = "c60-long", RefreshMode refresh = RefreshMode.Auto,
        Action<PresentJob>? beforePresent = null, Func<PresentJob, bool>? dropPresent = null, int verifierRows = 0)
    {
        var options = new PlaybackOptions
        {
            Path = Clips.Get(clip),
            Generator = GeneratorKind.Blend,
            Multiplier = 2,
            Display = DisplayKind.Offscreen,
            Clock = PlaybackClock.Real,
            OffscreenRate = sim.Rate,
            DurationNs = (long)(seconds * 1e9),
            WarmupNs = 500_000_000,
            Loop = true,
            PresentLeadNs = leadNs,
            Refresh = refresh,
            VerifierRows = verifierRows,

            // FMP_TEST_CSV_DIR=<dir> keeps each run's telemetry (for fmp verify) when a run needs a closer look.
            CsvPath = Environment.GetEnvironmentVariable("FMP_TEST_CSV_DIR") is { Length: > 0 } dir
                ? Path.Combine(dir, $"sim-{TestContext.Current.Test?.TestDisplayName.Replace(' ', '_').Replace('"', '_') ?? clip}-{Environment.TickCount64}.csv")
                : null,
        };
        var report = Playback.Run(cuda, options, new PlaybackHooks { SimulatedDisplay = sim, BeforePresent = beforePresent, DropPresent = dropPresent });
        Assert.DoesNotContain(Playback.FailuresIgnoringHookAllocations(report), f => !f.Contains("disagrees", StringComparison.Ordinal));
        return report;
    }

    /// <summary>Refreshes to miss: the first refresh after each of these times (seconds from the first refresh, which is in
    /// the session's pre-roll, about 0.7 s before playback starts).</summary>
    internal sealed class SkipSchedule
    {
        private readonly long[] _at;
        private long _base = long.MinValue;
        private int _next;

        public SkipSchedule(params double[] seconds) => _at = [.. seconds.Select(s => (long)(s * 1e9))];

        /// <summary>The refreshes it misses.</summary>
        public int Count => _at.Length;

        /// <summary>The refreshes missed (CLOCK_MONOTONIC). Sized up front: it is added to on the present thread, whose
        /// allocations in the measured window the run reports as failures.</summary>
        public List<long> Done { get; } = new(16);

        /// <summary>How long after an event its misses count as its own.</summary>
        public long WithinNs { get; init; } = 120_000_000;

        public bool Skips(long refreshNs)
        {
            if (_base == long.MinValue)
            {
                _base = refreshNs;
            }

            if (_next < _at.Length && refreshNs - _base >= _at[_next])
            {
                _next++;
                Done.Add(refreshNs);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The run's misses near a scheduled event (within 120 ms after it) and the others: these tests run the present
        /// thread on the real clock on a desktop in use, where a thread woken a couple of milliseconds late now and then
        /// costs a slot on its own (Phase 2: 1 to 4% of wakes under heavy load).
        /// </summary>
        public (List<MissEvent> Near, List<MissEvent> Other) Split(PlaybackReport report)
        {
            var origin = report.Statistics.FirstMeasuredNs - report.Options.WarmupNs;
            var near = new List<MissEvent>();
            var other = new List<MissEvent>();
            foreach (var m in report.Statistics.MissEvents)
            {
                var at = origin + m.AtNs;
                (Done.Any(d => at >= d - 20_000_000 && at <= d + WithinNs) ? near : other).Add(m);
            }

            return (near, other);
        }
    }
}
