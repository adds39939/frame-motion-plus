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

    internal static PlaybackReport Run(Cuda.CudaContext cuda, SimulatedCompositor sim, double seconds, long leadNs = -1, string clip = "c60-long", RefreshMode refresh = RefreshMode.Auto)
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

            // FMP_TEST_CSV_DIR=<dir> keeps each run's telemetry (for fmp verify) when a run needs a closer look.
            CsvPath = Environment.GetEnvironmentVariable("FMP_TEST_CSV_DIR") is { Length: > 0 } dir
                ? Path.Combine(dir, $"sim-{TestContext.Current.Test?.TestDisplayName.Replace(' ', '_').Replace('"', '_') ?? clip}-{Environment.TickCount64}.csv")
                : null,
        };
        var report = Playback.Run(cuda, options, new PlaybackHooks { SimulatedDisplay = sim });
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
