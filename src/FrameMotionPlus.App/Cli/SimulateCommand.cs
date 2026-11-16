using System.Globalization;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Settings;
using FrameMotionPlus.Core.Simulation;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.App.Cli;

/// <summary>
/// <c>fmp simulate</c>: drives the real scheduler from a simulated source and display with the null generator
/// and presenter, prints a summary and judges the run against the design doc with <see cref="SimulationCheck"/>.
/// </summary>
internal static class SimulateCommand
{
    private static readonly HashSet<string> FlagNames = new(StringComparer.Ordinal) { "check" };

    public static int Run(string[] args, TextWriter output)
    {
        var r = new ArgumentReader(args, FlagNames);
        var settingsPath = r.String("settings");
        var settings = settingsPath is null ? new FmpSettings() : SettingsCommand.Load(settingsPath);

        var sourceRate = r.Rate("source-fps", FrameRate.FromHz(60));
        var displayRate = r.Rate("display-hz", FrameRate.FromDouble(settings.Display.RefreshHz));
        var clock = r.String("clock") switch
        {
            null => settings.Scheduler.ClockMode,
            "fixed" => ClockMode.FixedRefresh,
            "vrr" => ClockMode.Vrr,
            var other => throw new UsageException($"Option '--clock' expects 'fixed' or 'vrr', got '{other}'."),
        };
        var dMs = r.Double("d-ms", 2.0, 0, 1000);
        var dJitterMs = r.Double("d-jitter-ms", 0, 0, 1000);
        var durationS = r.Double("duration-s", 60, 0.1, 86_400);
        var warmupS = r.Double("warmup-s", 1, 0, 86_400);
        var marginMs = r.Double("margin-ms", settings.Scheduler.MarginMs, 0, 100);
        var maxMultiplier = (int)r.Int64("max-multiplier", settings.Scheduler.MaxMultiplier, 1, 8);
        var dropRate = r.Double("drop-rate", 0, 0, 0.9);
        var frameJitterMs = r.Double("frame-jitter-ms", 0, 0, 1000);
        var skewPpm = r.Double("skew-ppm", 0, -10_000, 10_000);
        var phaseMs = r.Double("phase-ms", 0, 0, 10_000);
        var holdAtS = r.Double("hold-at-s", double.NaN, 0, 86_400);
        var holdForS = r.Double("hold-for-s", 2, 0, 86_400);
        var ingestMs = r.Double("ingest-ms", 0, 0, 1000);
        var ingestJitterMs = r.Double("ingest-jitter-ms", 0, 0, 1000);
        var wakeLateUs = r.Double("wake-late-us", 0, 0, 100_000);
        var wakeJitterUs = r.Double("wake-jitter-us", 0, 0, 100_000);
        var stallAtS = r.Double("stall-at-s", double.NaN, 0, 86_400);
        var stallMs = r.Double("stall-ms", 400, 0, 86_400_000);
        var rateChangeAtS = r.Double("rate-change-at-s", double.NaN, 0, 86_400);
        var rateAfter = r.Rate("rate-after", default);
        var deliverAheadMs = r.Double("deliver-ahead-ms", 0, 0, 500);
        var seed = r.Int64("seed", 1, 0, long.MaxValue);
        var csvPath = r.String("csv");
        var check = r.Flag("check");
        r.EnsureAllConsumed();

        if (warmupS >= durationS)
        {
            throw new UsageException("--warmup-s must be shorter than --duration-s.");
        }

        if (frameJitterMs * 2 >= Nanoseconds.ToMilliseconds((long)sourceRate.PeriodNs))
        {
            throw new UsageException("--frame-jitter-ms must stay below half the source frame interval.");
        }

        if (!double.IsNaN(rateChangeAtS) && !rateAfter.IsValid)
        {
            throw new UsageException("--rate-change-at-s needs --rate-after.");
        }

        settings.Scheduler.ClockMode = clock;
        settings.Scheduler.MarginMs = marginMs;
        settings.Scheduler.MaxMultiplier = maxMultiplier;
        settings.Scheduler.InitialGenerationTimeMs = dMs + dJitterMs; // as if seeded from the generator warm-up run

        SchedulerOptions options;
        try
        {
            options = settings.Scheduler.ToOptions(settings.Display) with { DisplayRate = displayRate, VblankAnchorNs = 0 };
        }
        catch (ArgumentException e)
        {
            throw new UsageException($"Invalid scheduler settings: {e.Message}", e);
        }

        var holds = double.IsNaN(holdAtS)
            ? ReadOnlyMemory<SimulatedHold>.Empty
            : new[] { new SimulatedHold(Nanoseconds.FromSeconds(holdAtS), Nanoseconds.FromSeconds(holdForS)) };

        var sim = new SimulationOptions
        {
            DurationNs = Nanoseconds.FromSeconds(durationS),
            StatsStartNs = Nanoseconds.FromSeconds(warmupS),
            GenerationTimeNs = Nanoseconds.FromMilliseconds(dMs),
            GenerationJitterNs = Nanoseconds.FromMilliseconds(dJitterMs),
            WakeLatenessNs = Nanoseconds.FromMicroseconds(wakeLateUs),
            WakeJitterNs = Nanoseconds.FromMicroseconds(wakeJitterUs),
            StallAtNs = double.IsNaN(stallAtS) ? long.MaxValue : Nanoseconds.FromSeconds(stallAtS),
            StallDurationNs = Nanoseconds.FromMilliseconds(stallMs),
            DeliverAheadNs = Nanoseconds.FromMilliseconds(deliverAheadMs),
            Seed = (ulong)seed + 1000,
            Source = new SimulatedSourceOptions
            {
                Rate = sourceRate,
                StartNs = Nanoseconds.FromMilliseconds(phaseMs),
                IngestLatencyNs = Nanoseconds.FromMilliseconds(ingestMs),
                IngestJitterNs = Nanoseconds.FromMilliseconds(ingestJitterMs),
                DropProbability = dropRate,
                TimestampJitterNs = Nanoseconds.FromMilliseconds(frameJitterMs),
                ClockSkewPpm = skewPpm,
                Holds = holds,
                RateChangeAtNs = double.IsNaN(rateChangeAtS) ? long.MaxValue : Nanoseconds.FromSeconds(rateChangeAtS),
                RateAfterChange = rateAfter,
                Seed = (ulong)seed,
            },
        };

        var scheduler = new FrameScheduler(options);
        using var generator = new NullFrameGenerator();
        using var presenter = new NullPresenter(displayRate, 0, clock == ClockMode.Vrr);
        SimulationResult result;
        long csvRecords = 0;
        if (csvPath is null)
        {
            result = new PipelineSimulator(sim, scheduler, generator, presenter).Run();
        }
        else
        {
            using var sink = OpenCsv(csvPath);
            result = new PipelineSimulator(sim, scheduler, generator, presenter, sink).Run();
            sink.Dispose();
            csvRecords = sink.RecordsWritten;
        }

        var verdict = SimulationCheck.Evaluate(options, sim, result);
        Print(output, options, sim, result, verdict, csvPath, csvRecords);
        return check && !verdict.Passed ? CommandDispatcher.ExitCheckFailed : CommandDispatcher.ExitOk;
    }

    private static CsvTelemetrySink OpenCsv(string path)
    {
        try
        {
            return new CsvTelemetrySink(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UsageException($"Cannot write '{path}': {e.Message}", e);
        }
    }

    private static void Print(
        TextWriter o,
        SchedulerOptions options,
        SimulationOptions sim,
        SimulationResult r,
        SimulationVerdict verdict,
        string? csvPath,
        long csvRecords)
    {
        var diag = r.Diagnostics;
        var fixedRefresh = options.ClockMode == ClockMode.FixedRefresh;
        var ic = CultureInfo.InvariantCulture;
        var tNs = (long)Math.Round(sim.Source.Rate.PeriodNs);

        o.WriteLine(string.Create(ic,
            $"fmp simulate: {sim.Source.Rate} fps -> {options.DisplayRate} Hz, {(fixedRefresh ? "fixed refresh" : "VRR")}, " +
            $"d = {Ms(sim.GenerationTimeNs)} ms (jitter {Ms(sim.GenerationJitterNs)}), {Nanoseconds.ToSeconds(sim.DurationNs):0.0} s " +
            $"(stats after {Nanoseconds.ToSeconds(sim.StatsStartNs):0.0} s)"));
        o.WriteLine();

        var ratioKind = fixedRefresh
            ? (diag.PhaseCoherent ? "integer ratio, phase-locked to vblanks" : "non-integer ratio (free-running timesteps)")
            : "slots follow the source";
        o.WriteLine(string.Create(ic,
            $"  scheduler   {ratioKind}; M = {diag.Multiplier}{(fixedRefresh ? $", stride {diag.Stride}" : "")}, generation {(diag.GenerationEnabled ? "on" : "off")}"));
        o.WriteLine(string.Create(ic,
            $"              T = {Ms(diag.SourceIntervalNs)} ms, hold H = {Ms(diag.HoldNs)} ms, arrival lag {Ms(diag.ArrivalLagNs)} ms, " +
            $"wake lateness {Ms(diag.WakeLatenessNs)} ms, lead {Ms(diag.LeadNs)} ms, " +
            $"D = {Ms(diag.PipelineDelayNs)} ms{(fixedRefresh && diag.PhaseCoherent ? $" (vblank alignment {SignedMs(diag.AlignmentNs)} ms)" : "")}"));
        o.WriteLine(string.Create(ic,
            $"  slots       {r.Slots} total: {r.Real} real, {r.Generated} generated, {r.Repeated} repeated, {r.Empty} empty"));
        o.WriteLine(string.Create(ic,
            $"              missed {r.Missed}, starved {r.StarvedSlots}, late {r.LateSlots}, hold {r.HoldSlots}, fallback {r.FallbackSlots}, skipped {r.SkippedSlots}, " +
            $"dropped late {r.Counters.FramesDroppedLate}, causality violations {r.CausalityViolations}, " +
            $"timestep inconsistencies {r.TimestepInconsistencies}, content regressions {r.ContentRegressions}"));
        o.WriteLine(string.Create(ic,
            $"  source      {r.SourceFramesDelivered} unique frames delivered, {r.SourceFramesDropped} dropped; " +
            $"holds detected {r.Counters.HoldsDetected}, re-seeds {r.Counters.Reseeds}"));
        var ts = r.DistinctTimestepsOverflow
            ? string.Create(ic, $"more than 32 distinct values in [{r.MinTimestep:0.0000}, {r.MaxTimestep:0.0000}]")
            : r.DistinctTimesteps.Count == 0 ? "none" : string.Join(", ", r.DistinctTimesteps.Select(x => x.ToString("0.####", ic)));
        o.WriteLine(string.Create(ic, $"  timesteps   {ts}"));
        o.WriteLine(string.Create(ic,
            $"  content     step per new image: min {Ms(r.ContentStepMinNs)} ms, max {Ms(r.ContentStepMaxNs)} ms, std dev {Ms(r.ContentStepStdDevNs)} ms"));
        o.WriteLine(string.Create(ic,
            $"  latency     real-frame added latency: mean {Ms(r.RealLatencyMeanNs)} ms, p99 {Ms(r.RealLatencyP99Ns)} ms, " +
            $"min {Ms(r.RealLatencyMinNs)} ms, max {Ms(r.RealLatencyMaxNs)} ms (n = {r.RealLatencySamples}); " +
            $"all shown images {Ms(r.ShownLatencyMinNs)}..{Ms(r.ShownLatencyMaxNs)} ms"));

        var m = Math.Max(verdict.ObservedMultiplier, 1);
        o.WriteLine(string.Create(ic,
            $"  model       dL = d + (M-1)/M*T = {Ms(sim.GenerationTimeNs)} + {Ms(m >= 2 ? LatencyModel.HoldNs(tNs, m) : 0)} = " +
            $"{Ms(m >= 2 ? LatencyModel.AddedLatencyNs(tNs, m, sim.GenerationTimeNs) : 0)} ms at the observed M = {m} " +
            $"(design doc allows M {verdict.ExpectedMultiplierLow}..{verdict.ExpectedMultiplierHigh}; budget T/M = {Ms(LatencyModel.PerFrameBudgetNs(tNs, m))} ms)"));
        if (verdict.Strict)
        {
            o.WriteLine(string.Create(ic,
                $"  expected    {verdict.LatencyRule}: latency within {Ms(verdict.LatencyLowerNs)}..{Ms(verdict.LatencyUpperNs)} ms"));
        }
        else
        {
            o.WriteLine("  expected    pattern and latency band not checked (drops, jitter, skew, holds, rate changes or stalls);");
            o.WriteLine("              correctness counters still enforced");
        }

        if (verdict.Passed)
        {
            o.WriteLine(verdict.Strict ? "  check       OK" : "  check       OK (correctness counters only)");
        }
        else
        {
            o.WriteLine("  check       FAIL");
            foreach (var f in verdict.Failures)
            {
                o.WriteLine($"              - {f}");
            }
        }

        if (csvPath is not null)
        {
            o.WriteLine(string.Create(ic, $"  telemetry   {csvRecords} records written to {csvPath}"));
        }
    }

    private static string Ms(double ns) => Nanoseconds.ToMilliseconds(ns).ToString("0.000", CultureInfo.InvariantCulture);

    private static string Ms(long ns) => Ms((double)ns);

    private static string SignedMs(double ns) => Nanoseconds.ToMilliseconds(ns).ToString("+0.000;-0.000;+0.000", CultureInfo.InvariantCulture);

    private static string SignedMs(long ns) => SignedMs((double)ns);
}
