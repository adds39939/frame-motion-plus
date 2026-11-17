using System.Globalization;
using System.Runtime.InteropServices;
using FrameMotionPlus.App.Bench;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Threading;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.App.Cli;

/// <summary><c>fmp bench &lt;name&gt;</c>: measurements that gates are judged against.</summary>
internal static class BenchCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0)
        {
            throw new UsageException("'fmp bench' needs a benchmark name: jitter, vfg or vfg-quality.");
        }

        var rest = args.AsSpan(1).ToArray();
        return args[0] switch
        {
            "jitter" => RunJitter(rest, output),
            "vfg" => VfgBenchCommand.Run(rest, output, error),
            "vfg-quality" => VfgQualityCommand.Run(rest, output, error),
            _ => throw new UsageException($"Unknown benchmark '{args[0]}'."),
        };
    }

    private static int RunJitter(string[] args, TextWriter o)
    {
        var options = JitterBenchOptions.Parse(args);
        if (options.CsvPath is { } csvPath)
        {
            // Fail on an unwritable path before measuring for minutes, not after.
            TryCreate(csvPath).Dispose();
        }

        PrintHeader(o, options);
        o.Flush();
        var result = new JitterBench(options).Run();
        PrintResult(o, result);

        if (options.CsvPath is { } path)
        {
            var rows = WriteCsv(path, result);
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  csv         {rows} samples written to {path}"));
        }

        return ExitCode(result);
    }

    /// <summary>1 when either check fails: the runtime-jitter gate or the missed-slot budget.</summary>
    internal static int ExitCode(JitterBenchResult r) => r.GatePassed ? CommandDispatcher.ExitOk : CommandDispatcher.ExitCheckFailed;

    /// <summary>
    /// The two checks on separate lines: the design doc's runtime-jitter gate (p99 wake lateness, no collection, no
    /// hot-thread allocation) and the missed-slot budget, with the timer-only loop's misses beside the full loop's so
    /// misses that come from the OS are recognisable as such. Then the overall verdict and exit code.
    /// </summary>
    internal static void PrintVerdict(TextWriter o, JitterBenchResult r)
    {
        var ic = CultureInfo.InvariantCulture;
        var f = r.FullStats;
        if (r.RuntimeGatePassed)
        {
            o.WriteLine(string.Create(ic,
                $"  jitter gate PASS: p99 wake lateness {JitterBenchResult.Us(f.WakeLateness.P99)} us <= {JitterBenchResult.Ms(r.Options.GateNs)} ms, no GC, no allocation on the hot threads (design doc gate)"));
        }
        else
        {
            o.WriteLine("  jitter gate FAIL (design doc gate):");
            foreach (var failure in r.RuntimeGateFailures)
            {
                o.WriteLine($"              - {failure}");
            }
        }

        var timer = r.BaselineStats is { } b
            ? string.Create(ic, $"; the timer-only loop missed {b.Missed} in the same run")
            : "; timer-only loop not run";
        o.WriteLine(string.Create(ic,
            $"  missed      {(r.MissedBudgetPassed ? "PASS" : "FAIL")}: full loop missed {f.Missed} slot(s) (skipped {f.Skipped}, done after display time {f.AfterTarget}), " +
            $"budget {r.Options.MaxMissed}{timer}"));
        o.WriteLine(string.Create(ic,
            $"  verdict     {(r.GatePassed ? "PASS" : "FAIL")} (exit code {ExitCode(r)})"));
    }

    private static void PrintHeader(TextWriter o, JitterBenchOptions options)
    {
        var ic = CultureInfo.InvariantCulture;
        var blocks = JitterBench.PlanBlocks(0, options);
        var totalS = Nanoseconds.ToSeconds(blocks[^1].StopNs);
        o.WriteLine(string.Create(ic,
            $"fmp bench jitter: {options.Rate} Hz slots, {options.SourceRate} fps source, wait {options.WaitDescription}, " +
            $"{Nanoseconds.ToSeconds(options.DurationNs):0.0} s measured per loop after {Nanoseconds.ToSeconds(options.WarmupNs):0.0} s warm-up, " +
            $"gate p99 wake lateness <= {JitterBenchResult.Ms(options.GateNs)} ms and at most {options.MaxMissed} missed slot(s)"));
        o.WriteLine(options.Baseline
            ? string.Create(ic,
                $"  running     full loop and timer-only loop in {blocks.Length} alternating blocks of up to {Nanoseconds.ToSeconds(options.BlockNs):0.0} s " +
                $"({Nanoseconds.ToMilliseconds(JitterBenchOptions.BlockSettleNs):0} ms settling after each switch), {totalS:0.0} s in all ...")
            : string.Create(ic, $"  running     full loop for {totalS:0.0} s ..."));
    }

    private static void PrintResult(TextWriter o, JitterBenchResult r)
    {
        var ic = CultureInfo.InvariantCulture;
        o.WriteLine(string.Create(ic,
            $"  machine     {MachineInfo.RuntimeKind()} .NET {Environment.Version}, {RuntimeInformation.OSDescription} " +
            $"{MachineInfo.KernelRelease() ?? ""} {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} CPUs, governor {MachineInfo.CpuGovernor() ?? "n/a"}"));
        o.WriteLine(string.Create(ic, $"              load average {r.LoadBefore ?? "n/a"} before, {r.LoadAfter ?? "n/a"} after"));
        o.WriteLine(string.Create(ic,
            $"  gc          {r.GcConfiguration}; playback scope requested {r.GcModeRequested}, obtained {r.GcModeObtained}, restored {r.GcModeAfter}"));
        o.WriteLine(string.Create(ic,
            $"  clock       {Monotonic.Source}, resolution {Monotonic.ResolutionNs} ns, {r.ClockReadNs} ns per read; " +
            $"sleeps {(DeadlineWaiter.IsPrecise ? "clock_nanosleep TIMER_ABSTIME" : "Thread.Sleep fallback")}"));
        PrintThread(o, "pacing", r.PacingThread, advice: true);
        PrintThread(o, "source", r.SourceThread, advice: false);
        o.WriteLine();

        o.WriteLine("  wake lateness (us)    samples      p50      p90      p99    p99.9      max     mean   (one per slot, skipped slots included)");
        PrintRow(o, r.BaselineStats, r.BaselineStats?.WakeLateness);
        PrintRow(o, r.FullStats, r.FullStats.WakeLateness);
        if (r.BaselineStats is { } b)
        {
            PrintDelta(o, b.WakeLateness, r.FullStats.WakeLateness);
        }

        o.WriteLine("  per-slot work (us)      slots");
        PrintRow(o, r.BaselineStats, r.BaselineStats?.Work);
        PrintRow(o, r.FullStats, r.FullStats.Work);
        o.WriteLine();

        var f = r.FullStats;
        o.WriteLine(string.Create(ic,
            $"  slots       full loop: {f.Slots} ({f.Real} real, {f.Generated} generated, {f.Repeated} repeated, {f.Empty} empty); " +
            $"missed {f.Missed} (skipped {f.Skipped}, done after display time {f.AfterTarget}), late for generation {f.Late}, over gate {f.OverGate}"));
        if (r.BaselineStats is { } bs)
        {
            o.WriteLine(string.Create(ic,
                $"              timer only: {bs.Slots}; missed {bs.Missed} (skipped {bs.Skipped}, done after display time {bs.AfterTarget}), over gate {bs.OverGate}"));
        }

        var d = r.Diagnostics;
        o.WriteLine(string.Create(ic,
            $"  scheduler   M = {d.Multiplier}, D = {JitterBenchResult.Ms(d.PipelineDelayNs)} ms, lead {JitterBenchResult.Ms(d.LeadNs)} ms, " +
            $"learnt wake lateness p99 {JitterBenchResult.Ms(d.WakeLatenessNs)} ms, generation {(d.GenerationEnabled ? "on" : "off")}"));
        o.WriteLine(string.Create(ic,
            $"  pipeline    {r.SourceFrames} source frames ({r.SourceDropped} found the ring full), " +
            $"{r.TelemetryRecords} telemetry records for {r.Full.Count} decisions drained ({r.TelemetryDropped} dropped)"));
        var g = r.MeasuredGc;
        o.WriteLine(string.Create(ic,
            $"  gc counts   measured time ({Nanoseconds.ToSeconds(r.MeasuredWindowNs):0.0} s, both loops): {g.Collections} (gen0/1/2 {g.Gen0Collections}/{g.Gen1Collections}/{g.Gen2Collections}); " +
            $"whole run incl. start-up: {r.RunGc.Collections}, {r.RunGc.TotalAllocatedBytes} B allocated process-wide (estimate)"));
        o.WriteLine(string.Create(ic,
            $"  allocated   pacing thread {r.PacingAllocatedBytes} B during the measured time; source thread {r.SourceAllocatedBytes} B"));

        PrintAttribution(o, r);
        PrintVerdict(o, r);
    }

    private static void PrintThread(TextWriter o, string label, ThreadSchedulingReport t, bool advice)
    {
        var ic = CultureInfo.InvariantCulture;
        o.WriteLine(string.Create(ic, $"  {label,-10}  {t.Name} (tid {t.OsThreadId}): requested {t.RequestSummary}"));
        o.WriteLine($"              obtained {t.Summary}");
        foreach (var problem in t.Problems)
        {
            o.WriteLine($"              refused {problem}");
        }

        if (!advice)
        {
            return;
        }

        if (!t.FullyGranted || t.Requested.RealTime != RealTimePolicy.None)
        {
            o.WriteLine($"              limits {t.LimitsSummary}");
        }

        foreach (var line in t.Advice)
        {
            o.WriteLine($"              to allow: {line}");
        }
    }

    private static void PrintRow(TextWriter o, PhaseStats? phase, Distribution? dist)
    {
        if (phase is null || dist is not { } x)
        {
            return;
        }

        o.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    {phase.Name,-18} {x.Count,9} {JitterBenchResult.Us(x.P50),8} {JitterBenchResult.Us(x.P90),8} {JitterBenchResult.Us(x.P99),8} " +
            $"{JitterBenchResult.Us(x.P999),8} {JitterBenchResult.Us(x.Max),8} {JitterBenchResult.Us(x.Mean),8}"));
    }

    private static void PrintDelta(TextWriter o, Distribution timer, Distribution full)
    {
        static string S(double ns) => JitterBenchResult.SignedUs(ns);
        o.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    {"full - timer only",-18} {"",9} {S(full.P50 - timer.P50),8} {S(full.P90 - timer.P90),8} {S(full.P99 - timer.P99),8} " +
            $"{S(full.P999 - timer.P999),8} {S(full.Max - timer.Max),8} {S(full.Mean - timer.Mean),8}"));
    }

    private static void PrintAttribution(TextWriter o, JitterBenchResult r)
    {
        var ic = CultureInfo.InvariantCulture;
        var gate = r.Options.GateNs;
        var full = r.FullStats.WakeLateness;
        if (r.BaselineStats is not { } b)
        {
            o.WriteLine("  attribution not measured (--no-baseline): the timer-only loop separates OS jitter from ours");
            return;
        }

        var timer = b.WakeLateness;
        o.WriteLine(string.Create(ic,
            $"  attribution OS timer and scheduling (timer-only loop): p99 {JitterBenchResult.Us(timer.P99)} us, p99.9 {JitterBenchResult.Us(timer.P999)} us"));
        o.WriteLine(string.Create(ic,
            $"              difference with the managed per-slot work: p99 {JitterBenchResult.SignedUs(full.P99 - timer.P99)} us, " +
            $"p99.9 {JitterBenchResult.SignedUs(full.P999 - timer.P999)} us (the work itself: p99 {JitterBenchResult.Us(r.FullStats.Work.P99)} us, " +
            $"max {JitterBenchResult.Us(r.FullStats.Work.Max)} us)"));
        if (full.P99 > gate)
        {
            o.WriteLine(timer.P99 > gate
                ? "              the timer-only loop misses the gate as well: the jitter is OS wake-up latency, not the runtime's"
                : "              the timer-only loop meets the gate and the full loop does not: the excess comes with our per-slot work");
        }
    }

    private static StreamWriter TryCreate(string path)
    {
        try
        {
            return new StreamWriter(path, append: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new UsageException($"Cannot write '{path}': {e.Message}", e);
        }
    }

    private static long WriteCsv(string path, JitterBenchResult r)
    {
        using var w = TryCreate(path);
        w.WriteLine("phase,block,index,measured,deadline_ns,wake_ns,lateness_ns,work_ns,target_ns,kind,reason,skipped");
        long rows = 0;
        if (r.Baseline is { } b)
        {
            rows += WritePhase(w, "timer", b, timerOnly: true);
        }

        rows += WritePhase(w, "full", r.Full, timerOnly: false);
        return rows;
    }

    private static long WritePhase(StreamWriter w, string phase, JitterSamples s, bool timerOnly)
    {
        var ic = CultureInfo.InvariantCulture;
        for (var i = 0; i < s.Count; i++)
        {
            var kind = timerOnly ? "timer" : ((SlotKind)s.Kind[i]).ToString();
            var reason = timerOnly ? "" : ((SlotReason)s.Reason[i]).ToString();
            w.WriteLine(string.Create(ic,
                $"{phase},{s.Block[i]},{i},{s.Measured[i]},{s.Deadline[i]},{s.Wake[i]},{s.Wake[i] - s.Deadline[i]}," +
                $"{s.End[i] - s.Wake[i]},{s.Target[i]},{kind},{reason},{s.Skipped[i]}"));
        }

        return s.Count;
    }
}
