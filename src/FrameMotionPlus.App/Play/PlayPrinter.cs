using System.Globalization;
using FrameMotionPlus.App.Capture;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Pipeline;

namespace FrameMotionPlus.App.Play;

/// <summary>What <c>fmp play</c> prints: what it resolved at the start, and the summary and verdict at the end.</summary>
internal static class PlayPrinter
{
    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public static void PrintSetup(TextWriter o, PlayOptions options, PlaybackOptions playback, PlaybackSetup s, string why, CudaContext cuda)
    {
        var info = s.Stream ?? throw new InvalidOperationException("fmp play prints a file's setup.");
        o.WriteLine($"fmp play: {info.Path}{(options.Gate ? "  [gate: milestone 1, 10 minutes VFG Medium 2x, zero missed slots, zero gen0 collections]" : "")}");
        o.WriteLine(string.Create(Ic,
            $"  file        {info.Describe()}; {(info.DurationNs > 0 ? $"{info.DurationNs / 1e9:0.###} s" : "length unknown")}" +
            $"{(info.DeclaredFrames > 0 ? $", {info.DeclaredFrames} frames" : "")}; colour {info.Color.Transfer}/{info.Color.Primaries} → {info.OutputFormat}" +
            $"{(playback.Loop ? "; looping" : "")}"));
        o.WriteLine($"  generator   {s.GeneratorName}: {why}{(s.GeneratorMayBlock ? "; its Enqueue blocks for the generation, so it runs on the generator thread" : "")}");
        if (s.VfgLoad is { } load)
        {
            o.WriteLine(string.Create(Ic,
                $"              {s.VfxSdk}; loaded in {load.LoadNs / 1e6:0.0} ms, warmed up in {load.WarmupNs / 1e6:0.0} ms, {load.DeviceBytes / 1048576.0:0} MiB; " +
                $"automatic shot-change detection on; output written straight into the ring slots"));
        }

        o.WriteLine($"  device      {s.DeviceName}, Vulkan driver {s.VulkanDriver}; CUDA context scheduling {options.CudaSync}");
        o.WriteLine($"  display     {s.Display}; transform {s.Transform}");
        o.WriteLine(string.Create(Ic,
            $"  feedback    {s.FeedbackMode}; presents in flight capped at {(s.PresentsInFlightCap == 0 ? "nothing (cannot cap)" : s.PresentsInFlightCap.ToString(Ic))}"));
        o.WriteLine(string.Create(Ic,
            $"  ring        {s.RingSlots} slots, pitch {s.RingPitchBytes} B; bridge budget {s.OperationsPerWrite} CUDA operations per write, lead at most {s.MaxLead}"));
        var cap = options.OutputHz is { } hz ? string.Create(Ic, $"--output-hz {hz:0.###} → cap {options.MultiplierFor(info.NominalRate)}") : $"cap {playback.Multiplier}";
        o.WriteLine(string.Create(Ic,
            $"  rates       source {(info.NominalRate.IsValid ? info.NominalRate.Hz.ToString("0.###", Ic) + " fps" : "unknown")}, multiplier {cap}; " +
            $"warm-up {playback.WarmupNs / 1e9:0.#} s{(playback.DurationNs > 0 ? $", duration {playback.DurationNs / 1e9:0.###} s" : "")}" +
            $"{(playback.MaxSlots > 0 ? $", {playback.MaxSlots} slots" : "")}"));
    }

    /// <summary>Prints the summary; returns the verdict (true: pass).</summary>
    public static bool PrintSummary(TextWriter o, PlayOptions options, PlaybackReport r) => PrintSummary(o, options.Gate, r);

    /// <summary>Prints the summary of a file or capture run (with <paramref name="gate"/>, the Phase 4 gate's verdict too).</summary>
    public static bool PrintSummary(TextWriter o, bool gate, PlaybackReport r)
    {
        var s = r.Statistics;
        var d = r.Diagnostics;
        var period = r.DisplayRate.IsValid ? r.DisplayRate.PeriodNs : 0;
        o.WriteLine(string.Create(Ic,
            $"  timing      display {(r.DisplayRate.IsValid ? r.DisplayRate.Hz.ToString("0.###", Ic) : "?")} Hz{(r.DisplayRateKnown ? "" : " (never confirmed by the output: a guess)")}" +
            $"{(r.DisplayRateChanges > 1 ? $", rate changed {r.DisplayRateChanges - 1}x during the run" : "")}; multiplier cap {r.Multiplier}, " +
            $"effective {d.Multiplier} (stride {d.Stride}: a new image every {d.Stride} refresh{(d.Stride == 1 ? "" : "es")}); " +
            $"source interval T {Ms(d.SourceIntervalNs)} ms; present lead {Ms(r.PresentLeadNs)} ms at the end{LeadHistory(r)}; scheduler margin {Ms(r.MarginNs)} ms; " +
            $"{(d.PhaseCoherent ? "phase-locked to the refreshes" : "not phase-locked")}"));
        o.WriteLine($"  threads     pacing: {r.PacingThread}; generator: {r.WriterThread}; present: {r.PresentThread}");
        o.WriteLine(string.Create(Ic,
            $"  duration    {r.PlaybackNs / 1e9:0.00} s played, {s.MeasuredSeconds:0.00} s measured after {r.WarmupNs / 1e9:0.#} s of warm-up; stopped: {StopName(r.StopReason)}"));
        o.WriteLine(string.Create(Ic,
            $"  slots       {s.Slots} measured: {s.Real} real, {s.Generated} generated, {s.Repeated} repeated, {s.Empty} empty; " +
            $"late (real frame instead of a generated one) {s.LateDecisions}, starved {s.StarvedSlots}, hold {s.HoldSlots}, " +
            $"discontinuity {s.DiscontinuitySlots}, fallback/stride {s.FallbackSlots}"));
        var rate = s.MeasuredSeconds > 0 ? s.Presents / s.MeasuredSeconds : 0;
        o.WriteLine(string.Create(Ic,
            $"  presents    {s.Presents} shown ({rate:0.0}/s); fillers {r.Fillers}, dropped late {r.DroppedLate}, dropped refused {r.DroppedRefused} " +
            $"(whole run); max in flight {r.MaxPresentsInFlight}; presenter refusals {r.PresentRefusals}, rejected {r.PresenterStats.RejectedRequests}; " +
            $"{r.TimedShows} of {r.Shown} shows timed by their first pixel, {s.UntimedShows} untimed in the window (kept out of the grid and the intervals; " +
            $"whole run: the compositor's report came without a time for {r.PresenterStats.ReportsWithoutTime}, " +
            $"{r.PresenterStats.UntimedPresents - r.PresenterStats.ReportsWithoutTime} report(s) never came)"));
        var iv = s.OutputIntervals;
        var offGrid = s.OffGridIntervals * 50 > s.TimedIntervals && s.TimedIntervals > 0
            ? " (the output is not refreshing on a fixed grid: VRR, or a compositor missing its own deadlines; no slot can be placed on a refresh there)"
            : "";
        o.WriteLine(iv.Count == 0
            ? "  intervals   none (no present feedback in the measured window)"
            : string.Create(Ic,
                $"  intervals   from present feedback: mean {Ms(iv.Mean)} ms, sd {Ms(iv.StdDev)} ms, p99 {Ms(iv.Percentile(0.99))} ms, max {Ms(iv.Max)} ms, " +
                $"min {Ms(iv.Min)} ms over {iv.Count}{(period > 0 ? $" (refresh period {Ms(period)} ms)" : "")}; " +
                $"{s.OffGridIntervals} of {s.TimedIntervals} timed intervals more than a quarter refresh off the grid{offGrid}"));
        var total = s.TotalMisses;
        o.WriteLine(string.Create(Ic,
            $"  missed      {total} slot(s); the pipeline's {s.PipelineMisses}: late present {s.MissCount(MissKind.LatePresent)}, early {s.MissCount(MissKind.Early)}, " +
            $"dropped {s.MissCount(MissKind.Dropped)}, untimed and its refresh taken {s.MissCount(MissKind.NotShown)}, refresh without a new present {s.MissCount(MissKind.MissedRefresh)}, " +
            $"refused {s.MissCount(MissKind.Refused)}, skipped vblank {s.MissCount(MissKind.SkippedVblank)}, back-pressure {s.MissCount(MissKind.BackPressure)}, " +
            $"presenter backlog {s.MissCount(MissKind.PresenterBacklog)}; the display's {s.EnvironmentMisses}: shown late although handed over in time " +
            $"{s.MissCount(MissKind.DisplayLate)}, given up catching up after those {s.MissCount(MissKind.CatchUp)}; during a refresh-rate change {s.RateChangeMisses}; " +
            $"unjudged {s.UnjudgedSlots}; refreshes without a new present {s.RefreshesWithoutPresent}"));
        if (r.Verification is { } v)
        {
            o.WriteLine(string.Create(Ic,
                $"  verifier    from the telemetry alone: {v.Judged} slots judged, {v.Misses} missed (early {v.EarlySlots}, late {v.Count(VerifierFinding.Late)}, " +
                $"not shown {v.Count(VerifierFinding.NotShown)}, untimed with its refresh taken {v.Count(VerifierFinding.RefreshTaken)}, wrong image {v.Count(VerifierFinding.WrongImage)}, " +
                $"out of order {v.Count(VerifierFinding.OutOfOrder)}), {v.Unjudged} unjudged, {v.Gaps.Sum(g => g.Refreshes)} refresh(es) without a new present " +
                $"({v.UnexplainedRefreshes} unexplained); {(v.Disagreements == 0 ? "agrees with the live counters" : $"DISAGREES with the live counters on {v.Disagreements}")}" +
                $"{(r.VerifierRowsDropped > 0 ? $"; {r.VerifierRowsDropped} slot(s) beyond its buffer not judged" : "")}"));
            if (v.Slots.Count > 0)
            {
                o.WriteLine($"              {v.Describe(10)}");
            }
        }
        else if (r.VerificationSkipped is { } why)
        {
            o.WriteLine($"  verifier    not run: {why}");
        }
        if (s.HandoverLeads.Count > 0)
        {
            o.WriteLine(string.Create(Ic,
                $"  hand-over   each present handed over (submitted and written) before its refresh: p1 {Ms(s.HandoverLeads.Percentile(0.01))} ms, " +
                $"p50 {Ms(s.HandoverLeads.Percentile(0.5))} ms (a late show is the display's only above the measured deadline: what has made its refresh in the " +
                $"last 10 s, plus 0.25 ms); fillers {s.Fillers}"));
        }
        if (s.MissEventCount > 0)
        {
            var shown = Math.Min(s.MissEventCount, 12);
            var list = new List<string>(shown);
            foreach (var m in s.MissEvents[..shown])
            {
                list.Add(string.Create(Ic, $"{m.AtNs / 1e9:0.000} s {MissName(m.Kind)}{(m.Refreshes > 1 ? $" x{m.Refreshes}" : "")}"));
            }

            o.WriteLine($"              at {string.Join(", ", list)}{(s.MissEventCount > shown ? $" ... ({s.MissEventCount} events)" : "")}");
        }

        var g = s.GenerationTimes;
        o.WriteLine(g.Count == 0
            ? "  d           no generated frames timed"
            : string.Create(Ic, $"  d           generation GPU time p50 {Ms(g.Percentile(0.5))} ms, p99 {Ms(g.Percentile(0.99))} ms, max {Ms(g.Max)} ms over {g.Count}; " +
                $"generator thread per write p50 {Ms(s.WriteHostTimes.Percentile(0.5))} ms, p99 {Ms(s.WriteHostTimes.Percentile(0.99))} ms"));
        if (s.CopyTimes.Count > 0)
        {
            o.WriteLine(string.Create(Ic, $"              real-frame copy into the ring p50 {Ms(s.CopyTimes.Percentile(0.5))} ms, p99 {Ms(s.CopyTimes.Percentile(0.99))} ms over {s.CopyTimes.Count}"));
        }

        var dd = s.PipelineDelays;
        var stride = s.StrideDelay;
        o.WriteLine(string.Create(Ic,
            $"  D           the scheduler's D {Ms(d.PipelineDelayNs)} ms at the end (target {Ms(d.DelayTargetNs)}: hold {Ms(d.HoldNs)} + lead {Ms(d.LeadNs)} " +
            $"[p99 d {Ms(d.GenerationTimeNs)} + wake {Ms(d.WakeLatenessNs)} + margin {Ms(r.MarginNs)}] + arrival {Ms(d.ArrivalLagNs)} + alignment {Ms(d.AlignmentNs)}); " +
            $"over the window min {Ms(dd.Min)}, p50 {Ms(dd.Percentile(0.5))}, max {Ms(dd.Max)} ms"));
        if (stride.Count > 0)
        {
            o.WriteLine(string.Create(Ic,
                $"  to light    a real frame is shown present lead {Ms(s.PresentLeads.Percentile(0.5))} + D {Ms(dd.Percentile(0.5))} + the scheduler's rounding " +
                $"{Ms(stride.Percentile(0.5))} ms (p50, measured) after its content time: at stride s a new image lands on the next refresh of its stride " +
                $"(up to s − 1 refreshes after τ); not phase-locked, a real frame within the snap of τ is shown up to half an interval early"));
        }
        var model = LatencyModel.Evaluate(d.SourceIntervalNs, Math.Max(1, d.Multiplier), g.Count > 0 ? g.Percentile(0.99) : d.GenerationTimeNs);
        var lat = s.RealFrameLatency;
        if (r.Capture is null)
        {
            o.WriteLine(lat.Count == 0
                ? "  latency     no real frame shown in the measured window"
                : string.Create(Ic,
                    $"  latency     real frames shown {Ms(lat.Mean)} ms after their content time (p99 {Ms(lat.Percentile(0.99))}); latency model ΔL = d + (M−1)/M·T = " +
                    $"{Ms(model.GenerationTimeNs)} + {Ms(model.HoldNs)} = {Ms(model.AddedLatencyNs)} ms (M {model.Multiplier}, T {Ms(model.SourceIntervalNs)}); " +
                    $"the rest is the present lead, the scheduler's margin and alignment, and the stride's rounding (a file is not latency-bound)"));
        }
        else
        {
            CapturePrinter.PrintLatency(o, r, model);
        }

        o.WriteLine(string.Create(Ic,
            $"  pipeline    bridge lead at most {r.MaxOutstanding} (limit {r.Setup.MaxLead}); slots skipped for back-pressure {r.SkippedBackPressure}, for presenter backlog " +
            $"{r.SkippedPresenterBacklog}; refresh-grid updates {r.DisplayUpdates}; ring writes: {r.Copies} copies, {r.GeneratedWrites} generated, " +
            $"{r.Fallbacks} fallbacks; writes out of order {r.OutOfOrderSignals}"));
        o.WriteLine(r.GeneratorFaults == 0
            ? "  faults      generator: none"
            : $"  faults      generator: {r.GeneratorFaults} (faulted: {r.GeneratorFaulted}; playback carried on with real frames); first: {r.FirstGeneratorFault}");
        var src = r.SourceStatistics;
        if (r.Capture is { } capture)
        {
            CapturePrinter.PrintCapture(o, r, capture);
        }
        else
        {
            o.WriteLine(string.Create(Ic,
                $"  source      {src.FramesDecoded} decoded, {src.FramesDelivered} delivered, {r.FramesRead} read; loops {src.Loops}, discontinuities {r.Discontinuities} " +
                $"(scheduler {r.Counters.Discontinuities}); frame-pool growth {src.PoolGrowth}; decode thread waited for the pipeline {src.BackPressureWaits}x"));
        }
        foreach (var (name, cost) in r.Threads)
        {
            o.WriteLine(cost.AllocatedBytes < 0
                ? $"  {name,-11} not measured"
                : string.Create(Ic,
                    $"  {"cpu/" + name,-11} {cost.AllocatedBytes} B allocated, {(s.Slots > 0 && cost.CpuNs >= 0 ? cost.CpuNs / 1e3 / s.Slots : 0):0.0} µs CPU per slot " +
                    $"({cost.CpuNs / 1e9:0.000} s over {s.Slots} slots, {cost.Turns} loop turns)"));
        }

        o.WriteLine(string.Create(Ic,
            $"  gc          measured window gen0/1/2 {r.MeasuredGc.Gen0Collections}/{r.MeasuredGc.Gen1Collections}/{r.MeasuredGc.Gen2Collections}; whole run " +
            $"{r.RunGc.Gen0Collections}/{r.RunGc.Gen1Collections}/{r.RunGc.Gen2Collections}; " +
            $"{(r.Capture is { } cap ? $"capture thread {cap.Counters.DequeueThreadAllocatedBytes} B" : $"decode thread {src.DecodeThreadAllocatedBytes} B")}; {r.GcMode}"));
        o.WriteLine(r.CsvPath is null
            ? $"  telemetry   {r.TelemetryRecords} records (--csv writes them)"
            : $"  telemetry   {r.TelemetryRecords} records written to {r.CsvPath}{(r.TelemetryDropped > 0 ? $" ({r.TelemetryDropped} dropped)" : "")}");
        o.WriteLine(r.GpuAbandoned
            ? $"  teardown    ABANDONED: {r.Abandoned}"
            : r.AbandonedImports == 0 ? "  teardown    clean: ring drained, imports freed" : $"  teardown    {r.AbandonedImports} import(s) went to the reaper");
        o.WriteLine(r.ValidationEnabled
            ? string.Create(Ic, $"  validation  VK_LAYER_KHRONOS_validation with synchronization checks: {r.ValidationProblems} message(s)")
            : "  validation  off (--validate turns it on)");
        if (r.ValidationReport is { } report)
        {
            foreach (var line in report.Split('\n'))
            {
                o.WriteLine($"              {line.TrimEnd()}");
            }
        }

        var failures = r.Failures();
        var warnings = new List<string>();
        if (r.MeasuredGc.Gen0Collections > 0)
        {
            warnings.Add($"{r.MeasuredGc.Gen0Collections} gen0 collection(s) in the measured window");
        }

        if (total > 0)
        {
            warnings.Add($"{total} missed slot(s): {s.PipelineMisses} the pipeline's, {s.EnvironmentMisses} the display's, {s.RateChangeMisses} during a rate change");
        }

        if (r.GeneratorFaults > 0)
        {
            warnings.Add($"the generator failed {r.GeneratorFaults}x and was switched off");
        }

        var pass = failures.Count == 0;
        if (gate && r.Setup.Stream is { } stream)
        {
            var gateFailures = new List<string>();
            if (r.StopReason != StopReason.Duration)
            {
                gateFailures.Add($"the run did not reach its duration (stopped: {StopName(r.StopReason)})");
            }

            if (!r.Measured)
            {
                gateFailures.Add("nothing was measured");
            }

            if (s.UnjudgedSlots > 0 || r.Verification is { Unjudged: > 0 })
            {
                gateFailures.Add($"{Math.Max(s.UnjudgedSlots, r.Verification?.Unjudged ?? 0)} slot(s) without a first-pixel time that nothing can judge");
            }

            if (r.Verification is null)
            {
                gateFailures.Add($"the telemetry verifier did not run ({r.VerificationSkipped ?? "no telemetry"})");
            }

            // The Phase 4 gate (relaxed by the owner, 2026-11-18): zero missed slots caused by the pipeline; the display's
            // and the rate change's are reported apart.
            if (s.PipelineMisses > 0)
            {
                gateFailures.Add($"{s.PipelineMisses} missed slot(s) caused by the pipeline");
            }

            if (r.MeasuredGc.Gen0Collections > 0)
            {
                gateFailures.Add($"{r.MeasuredGc.Gen0Collections} gen0 collection(s) in the measured window");
            }

            if (stream.Width != 2560 || stream.Height != 1440 || !stream.NominalRate.IsValid || Math.Abs(stream.NominalRate.Hz - 60) > 0.01)
            {
                warnings.Add($"the gate is defined for a 1440p60 file; this is {stream.Width}x{stream.Height} at {stream.NominalRate}");
            }

            if (Math.Abs((r.DisplayRate.Hz / Math.Max(1, d.Stride)) - 120) > 1)
            {
                warnings.Add(string.Create(Ic, $"the gate is defined for 120 Hz output; this run's output was {r.DisplayRate.Hz / Math.Max(1, d.Stride):0.#} Hz"));
            }

            o.WriteLine(gateFailures.Count == 0 && pass
                ? string.Create(Ic,
                    $"  gate        PASS: {s.Slots} slots over {s.MeasuredSeconds:0.0} s, zero missed slots caused by the pipeline, zero gen0 collections " +
                    $"(the display's: {s.EnvironmentMisses}; during a rate change: {s.RateChangeMisses})")
                : "  gate        FAIL");
            foreach (var g2 in gateFailures)
            {
                o.WriteLine($"              - {g2}");
            }

            pass &= gateFailures.Count == 0;
        }

        foreach (var w in warnings)
        {
            o.WriteLine($"  WARNING     {w}");
        }

        if (failures.Count == 0 && r.StopReason == StopReason.User && !r.Measured && !gate)
        {
            o.WriteLine("  verdict     STOPPED by the user before anything was measured (no verdict)");
        }
        else if (failures.Count == 0)
        {
            o.WriteLine(pass ? "  verdict     PASS" : "  verdict     FAIL (the gate)");
        }
        else
        {
            o.WriteLine("  verdict     FAIL");
            foreach (var f in failures)
            {
                o.WriteLine($"              - {f}");
            }
        }

        return pass;
    }

    public static string StopName(StopReason reason) => reason switch
    {
        StopReason.Duration => "duration reached",
        StopReason.SlotLimit => "slot count reached",
        StopReason.EndOfFile => "end of file",
        StopReason.User => "by the user",
        StopReason.Failure => "failure",
        _ => "not stopped",
    };

    private static string MissName(MissKind kind) => kind switch
    {
        MissKind.Early => "early",
        MissKind.NotShown => "untimed, refresh taken",
        MissKind.MissedRefresh => "refresh without a new present",
        MissKind.DisplayLate => "display late",
        MissKind.CatchUp => "display catch-up",
        MissKind.RateChange => "rate change",
        MissKind.LatePresent => "late present",
        MissKind.Dropped => "dropped",
        MissKind.Refused => "refused",
        MissKind.SkippedVblank => "skipped vblank",
        MissKind.BackPressure => "back-pressure",
        _ => "presenter backlog",
    };

    private static string LeadHistory(PlaybackReport r) => r.LeadRaises == 0
        ? " (it did not need to adapt)"
        : string.Create(Ic, $" (raised {r.LeadRaises}x when a frame's content missed the compositor's latch; peak {Ms(r.PeakLeadNs)} ms)");

    private static string Ms(double ns) => (ns / 1e6).ToString("0.000", Ic);
}
