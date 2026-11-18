using System.Globalization;
using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// Short windowed runs (a small window for a few seconds, on whichever output the compositor puts it, under the validation
/// layer): one new present per refresh at the output's own rate, at most two in flight, no validation message, no
/// allocation. When present feedback shows the compositor skipping refreshes with a present queued (the desktop is busy,
/// docs/benchmarks/phase2-interop.md), the run reports "environment" (skips) instead of failing; the pipeline's own misses
/// fail it. So does an output that does not refresh on a fixed grid at all (measured: more than one in fifty intervals
/// between timed shows a quarter refresh or more off a whole number of refreshes; VRR, or a compositor missing its own
/// deadlines, as KWin on this machine's 240 Hz output at times), where no slot can be placed on a refresh.
/// </summary>
[Trait(Category.Name, Category.Display)]
public class WindowedTests
{
    [Theory]
    [InlineData(GeneratorKind.Blend)]
    [InlineData(GeneratorKind.None)]
    public void A_short_windowed_run_presents_one_frame_per_refresh(GeneratorKind generator)
    {
        var cuda = Hardware.RequireDisplay();
        Windowed(cuda, generator);
    }

    [Fact]
    [Trait(Category.Name, Category.Vfx)]
    public void A_short_windowed_vfg_run_presents_one_frame_per_refresh()
    {
        var cuda = Hardware.RequireDisplay();
        Hardware.RequireVfx();
        var report = Windowed(cuda, GeneratorKind.Vfg);
        Assert.True(report.GeneratedWrites > 50);
    }

    /// <summary>
    /// No slot is shown a refresh before its own (the review found every slot of a 29.97 fps clip early at 119.88 Hz, and
    /// the first seconds of both gate runs: a present handed over a lead of 1.5 refreshes before its refresh into an empty
    /// queue was taken for the refresh before). Judged by the telemetry verifier from first-pixel times, which must also
    /// agree with the live counters slot for slot. A 29.97 fps file (MP4: exact timestamps) and a 60 fps one.
    /// </summary>
    [Theory]
    [InlineData("c2997-mp4")]
    [InlineData("c60-mp4")]
    public void No_slot_is_shown_before_its_refresh(string clip)
    {
        var cuda = Hardware.RequireDisplay();
        var report = Run(cuda, GeneratorKind.Blend, -1, adaptive: true, 6_000_000_000, 1_000_000_000, clip);
        Log.WriteLine($"{clip}: {Playback.Describe(report)}");
        RequireFixedGrid(report);
        var v = Assert.IsType<VerificationResult>(report.Verification);
        Log.WriteLine($"verifier: {v.Judged} judged, {v.Misses} missed, early {v.EarlySlots}, gaps {v.Gaps.Count}; {v.Describe()}");
        Assert.True(v.Judged > 300, $"{v.Judged} slots judged");
        Assert.Equal(0, v.EarlySlots);
        Assert.Equal(0, report.Statistics.MissCount(MissKind.Early));
        Assert.Equal(0, v.Disagreements);
    }

    /// <summary>
    /// The review's mutation of the default (a lead far too short for this compositor, fixed): presents are handed over after
    /// its latch and shown late. Judged from first-pixel times and host-clock hand-over times, those misses are the
    /// pipeline's (they used to count as "environment" and the windowed tests skipped instead of failing).
    /// </summary>
    [Fact]
    public void A_present_lead_too_short_for_this_compositor_is_counted_as_the_pipelines()
    {
        var cuda = Hardware.RequireDisplay();
        var report = Run(cuda, GeneratorKind.Blend, 1_500_000, adaptive: false, 4_000_000_000, 500_000_000);
        var s = report.Statistics;
        Log.WriteLine(Playback.Describe(report));
        RequireFixedGrid(report);
        if (s.TotalMisses == 0)
        {
            Assert.Skip("this compositor took presents handed over 1.5 ms before their refresh: nothing late to judge");
        }

        Assert.True(s.PipelineMisses > s.EnvironmentMisses, $"{s.PipelineMisses} of {s.TotalMisses} misses counted as the pipeline's");
    }

    /// <summary>
    /// The same short lead to start from, adaptive: the first frames whose content misses the latch raise the lead a step
    /// each, at once, and the misses stop; none of the pipeline's in the second half of the run (with the lead fixed they go
    /// on for as long as it runs). On a desktop where the compositor itself skips refreshes (seen: KWin missing one refresh
    /// every second on DP-3 with a browser open, presents handed over 7.8 ms ahead shown a refresh late too), a skipped
    /// refresh close to another can still count as the pipeline's by the conservative rule. So a pipeline-attributed miss in
    /// the second half is excused only when its own timestamps clear it: handed over at least 1 ms before presents that
    /// made their refresh (the latch's upper bound from this run's on-time shows), or within four refreshes of a miss the
    /// display is proven to own. If every one is excused, the run skips as "environment"; any other fails it. The lead logic
    /// itself is tested against a simulated compositor
    /// (<c>SimulatedDisplayTests.A_short_starting_lead_rises_past_the_latch_and_the_misses_stop</c>).
    /// </summary>
    [Fact]
    public void A_short_starting_lead_adapts_and_the_misses_stop()
    {
        var cuda = Hardware.RequireDisplay();
        var csv = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fmp-short-lead-{Environment.ProcessId}-{Environment.TickCount64}.csv");
        try
        {
            var report = Run(cuda, GeneratorKind.Blend, 1_500_000, adaptive: true, 5_000_000_000, 300_000_000, csvPath: csv);
            var s = report.Statistics;
            var half = s.LastMeasuredNs > s.FirstMeasuredNs ? (s.LastMeasuredNs - s.FirstMeasuredNs) / 2 : 0;
            var judgedFrom = half + 300_000_000;
            var period = report.DisplayRate.IsValid ? (long)report.DisplayRate.PeriodNs : 0;

            // The live misses and the CSV's missed slots are the same set, in the same order: pair them, and read each
            // miss's hand-over lead (refresh − the later of submission and content written) from its slot.
            var rows = ShortLeadRow.Read(csv);
            var missedRows = rows.Where(r => r.Missed).OrderBy(r => r.TargetNs).ToList();
            var events = s.MissEvents.ToArray().OrderBy(m => m.AtNs).ToList();
            var paired = missedRows.Count == events.Count;
            var latchUpper = rows.Where(r => !r.Missed && r.ActualNs > 0 && r.SubmitNs > 0).Select(r => r.HandoverLeadNs).Order().ToList() is { Count: > 0 } ok
                ? ok[ok.Count / 20]
                : long.MaxValue;
            var display = events.Where(m => m.Kind is MissKind.DisplayLate or MissKind.Discarded or MissKind.CatchUp).Select(m => m.AtNs).ToList();
            var pipeline = 0;
            var excused = 0;
            for (var i = 0; i < events.Count; i++)
            {
                var m = events[i];
                if (m.AtNs <= judgedFrom || m.Kind is not (MissKind.LatePresent or MissKind.Dropped or MissKind.NotShown or MissKind.SkippedVblank or MissKind.Early))
                {
                    continue;
                }

                pipeline++;
                var aheadOfLatch = paired && missedRows[i].HandoverLeadNs >= latchUpper + 1_000_000;
                var nearDisplay = period > 0 && display.Any(t => Math.Abs(t - m.AtNs) <= 4 * period);
                excused += aheadOfLatch || nearDisplay ? 1 : 0;
            }

            Log.WriteLine($"{Playback.Describe(report)}; lead raised {report.LeadRaises}x to {report.FinalLeadNs / 1e6:0.000} ms; latch at most {latchUpper / 1e6:0.000} ms " +
                          $"(on-time hand-overs, p5); second half: pipeline misses {pipeline}, of them cleared by their timestamps {excused}; misses paired with slots {paired}");
            RequireFixedGrid(report);
            if (report.LeadRaises == 0 && s.TotalMisses == 0)
            {
                Assert.Skip("this compositor took presents handed over 1.5 ms before their refresh: nothing to adapt to");
            }

            Assert.True(report.LeadRaises >= 1, "the lead never adapted");
            if (pipeline > 0 && excused == pipeline)
            {
                Assert.Skip($"the compositor skipped refreshes in the second half: all {pipeline} misses counted as the pipeline's were handed over well before the latch or next to a miss of the display's: environment");
            }

            Assert.True(pipeline == 0, $"{pipeline} misses of the pipeline's in the second half, {pipeline - excused} not cleared by their timestamps");
        }
        finally
        {
            File.Delete(csv);
        }
    }

    /// <summary>One slot of the telemetry CSV, as much as the short-lead test needs.</summary>
    private sealed record ShortLeadRow(long TargetNs, long ActualNs, long SubmitNs, long ReadyNs, bool Missed)
    {
        public long HandoverLeadNs => TargetNs - Math.Max(SubmitNs, ReadyNs);

        public static List<ShortLeadRow> Read(string path)
        {
            var lines = File.ReadAllLines(path);
            var head = lines[0].Split(',');
            int Col(string name) => Array.IndexOf(head, name);
            int slot = Col("slot"), target = Col("target_display_ns"), actual = Col("actual_display_ns"), submit = Col("present_submit_ns"), ready = Col("generation_end_ns"), missed = Col("missed");
            var rows = new List<ShortLeadRow>();
            foreach (var line in lines.Skip(1))
            {
                var f = line.Split(',');
                if (long.Parse(f[slot], CultureInfo.InvariantCulture) < 0)
                {
                    continue;
                }

                rows.Add(new ShortLeadRow(long.Parse(f[target], CultureInfo.InvariantCulture), long.Parse(f[actual], CultureInfo.InvariantCulture),
                    long.Parse(f[submit], CultureInfo.InvariantCulture), long.Parse(f[ready], CultureInfo.InvariantCulture), f[missed] == "1"));
            }

            return rows;
        }
    }

    private static PlaybackReport Windowed(Cuda.CudaContext cuda, GeneratorKind generator)
    {
        var report = Run(cuda, generator, -1, adaptive: true, 3_500_000_000, 1_000_000_000);
        Log.WriteLine(Playback.Describe(report));
        RequireFixedGrid(report);

        var s = report.Statistics;
        Assert.Empty(report.Failures());
        Assert.Equal(0, report.ValidationProblems);
        Assert.True(report.MaxPresentsInFlight <= 2, $"{report.MaxPresentsInFlight} presents in flight");
        Assert.True(report.DisplayRateKnown, "the window's refresh rate never became known");
        var rate = report.DisplayRate.Hz;
        var shown = s.MeasuredSeconds > 0 ? s.Presents / s.MeasuredSeconds : 0;
        var environment = s.EnvironmentMisses;
        Log.WriteLine($"{generator}: {shown:0.0} presents/s on a {rate:0.###} Hz output; misses {s.TotalMisses} (environment {environment}, pipeline {s.PipelineMisses}); " +
                      $"intervals sd {s.OutputIntervals.StdDev / 1e6:0.000} ms, max {s.OutputIntervals.Max / 1e6:0.000} ms");
        // The verifier, from the telemetry alone, agrees with the live counters (before any skip: a disagreement is never
        // the environment's).
        Log.WriteLine($"misses: {string.Join(", ", s.MissEvents.ToArray().Take(40).Select(m => $"{m.AtNs / 1e9:0.000} s {m.Kind}"))}");
        var v = Assert.IsType<VerificationResult>(report.Verification);
        Log.WriteLine($"verifier: {v.Judged} judged, {v.Misses} missed, early {v.EarlySlots}; {v.Describe()}");
        Assert.Equal(0, v.Disagreements);
        Assert.Equal(0, v.EarlySlots);
        if (environment > 0 && s.PipelineMisses == 0)
        {
            Assert.Skip($"environment: the compositor skipped {environment} refresh(es) with a present queued (pipeline misses {s.PipelineMisses}); " +
                        $"{shown:0.0} presents/s on a {rate:0.###} Hz output");
        }

        Assert.Equal(0, s.PipelineMisses);
        Assert.InRange(shown, rate * 0.98, rate * 1.02);
        Assert.True(report.TimedShows > 0 || report.Shown > 0);

        // The delay to light is the lead, D and the scheduler's rounding to real frames (a new image lands on the next refresh
        // of its stride, up to stride − 1 refreshes after τ; not phase-locked, up to half an interval before it); the review
        // found the summary a refresh short at stride 2.
        if (s.StrideDelay.Count > 0)
        {
            var stride = Math.Max(1, report.Diagnostics.Stride);
            var period = report.DisplayRate.PeriodNs;
            var rounding = s.StrideDelay.Percentile(0.5);
            Log.WriteLine($"stride {stride}: real frames {s.RealFrameLatency.Percentile(0.5) / 1e6:0.000} ms after content = lead {s.PresentLeads.Percentile(0.5) / 1e6:0.000} + D {s.PipelineDelays.Percentile(0.5) / 1e6:0.000} + rounding {rounding / 1e6:0.000} ms");
            Assert.InRange(rounding, -(report.Diagnostics.SourceIntervalNs / 2) - 1_000_000, ((stride - 1) * period) + 1_500_000);
        }

        return report;
    }

    /// <summary>Skips (environment) when the output did not refresh on a fixed grid in the run (see the class remarks).</summary>
    private static void RequireFixedGrid(PlaybackReport report)
    {
        var s = report.Statistics;
        Log.WriteLine($"{s.OffGridIntervals} of {s.TimedIntervals} timed intervals off the refresh grid; untimed shows {s.UntimedShows}; " +
                      $"the compositor's reports without a time {report.PresenterStats.ReportsWithoutTime}");
        if (s.TimedIntervals > 0 && s.OffGridIntervals * 50 > s.TimedIntervals)
        {
            Assert.Skip($"environment: the output did not refresh on a fixed grid: {s.OffGridIntervals} of {s.TimedIntervals} intervals between timed shows " +
                        $"more than a quarter refresh off a whole number of refreshes ({report.DisplayRate.Hz:0.###} Hz output; VRR, or a compositor missing " +
                        $"its own deadlines); the compositor left the time out of {report.PresenterStats.ReportsWithoutTime} report(s)");
        }
    }

    private static PlaybackReport Run(Cuda.CudaContext cuda, GeneratorKind generator, long leadNs, bool adaptive, long durationNs, long warmupNs, string clip = "c60-long", string? csvPath = null)
    {
        var options = new PlaybackOptions
        {
            Path = Clips.Get(clip),
            Generator = generator,
            Multiplier = 2,
            Display = DisplayKind.Window,
            WindowWidth = 480,
            WindowHeight = 270,
            DurationNs = durationNs,
            WarmupNs = warmupNs,
            Loop = true,
            Validate = true,
            PresentLeadNs = leadNs,
            AdaptiveLead = adaptive,

            // FMP_TEST_CSV_DIR=<dir> keeps each run's telemetry (for fmp verify) when a run needs a closer look.
            CsvPath = csvPath ?? (Environment.GetEnvironmentVariable("FMP_TEST_CSV_DIR") is { Length: > 0 } dir
                ? System.IO.Path.Combine(dir, $"{TestContext.Current.Test?.TestDisplayName.Replace(' ', '_').Replace('"', '_') ?? clip}-{Environment.TickCount64}.csv")
                : null),
        };
        try
        {
            return Playback.Run(cuda, options);
        }
        catch (PlaybackUnavailableException e) when (e.Message.Contains("Vulkan driver", StringComparison.Ordinal))
        {
            Assert.Skip($"no validation layer or Vulkan driver: {e.Message}");
            throw;
        }
    }
}
