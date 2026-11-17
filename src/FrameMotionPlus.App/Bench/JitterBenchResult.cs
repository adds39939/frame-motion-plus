using System.Globalization;
using System.Runtime;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Platform.Runtime;
using FrameMotionPlus.Platform.Threading;

namespace FrameMotionPlus.App.Bench;

/// <summary>Exact order statistics of one series, ns. Percentiles are nearest-rank.</summary>
internal readonly record struct Distribution(long Count, long Min, long P50, long P90, long P99, long P999, long Max, double Mean)
{
    /// <summary>Sorts <paramref name="values"/> in place.</summary>
    public static Distribution Of(Span<long> values)
    {
        if (values.IsEmpty)
        {
            return default;
        }

        values.Sort();
        double sum = 0;
        foreach (var v in values)
        {
            sum += v;
        }

        return new Distribution(
            values.Length,
            values[0],
            Rank(values, 0.50),
            Rank(values, 0.90),
            Rank(values, 0.99),
            Rank(values, 0.999),
            values[^1],
            sum / values.Length);
    }

    private static long Rank(ReadOnlySpan<long> sorted, double fraction)
    {
        var rank = (long)Math.Ceiling(fraction * sorted.Length);
        return sorted[(int)Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}

/// <summary>Statistics of one loop kind over its measured slots (warm-up and block settling excluded).</summary>
internal sealed record PhaseStats
{
    public required string Name { get; init; }

    /// <summary>Slots the loop served.</summary>
    public long Slots { get; init; }

    /// <summary>
    /// Actual wake time minus the requested deadline, one sample per slot: served slots, and also every slot the loop
    /// came too late for and skipped (the wake that ended the gap minus that slot's own deadline), so a stall weighs
    /// in the percentiles with all the slots it cost rather than as a single late wake.
    /// </summary>
    public Distribution WakeLateness { get; init; }

    /// <summary>Per-slot CPU work after waking: from the wake to the telemetry record written (served slots).</summary>
    public Distribution Work { get; init; }

    /// <summary>Slots whose display time passed before the loop got to them (the scheduler's skipped vblanks).</summary>
    public long Skipped { get; init; }

    /// <summary>Decisions the scheduler judged too late for generation to finish by the vblank.</summary>
    public long Late { get; init; }

    /// <summary>Served slots whose CPU work finished after their target display time.</summary>
    public long AfterTarget { get; init; }

    public long OverGate { get; init; }

    public long Real { get; init; }

    public long Generated { get; init; }

    public long Repeated { get; init; }

    public long Empty { get; init; }

    public long Overflow { get; init; }

    /// <summary>Missed deadlines: skipped slots plus served slots completed after their display time.</summary>
    public long Missed => Skipped + AfterTarget;

    public static PhaseStats From(JitterSamples s, long gateNs, double periodNs)
    {
        ArgumentNullException.ThrowIfNull(s);
        long served = 0, skipped = 0;
        for (var i = 0; i < s.Count; i++)
        {
            if (s.Measured[i] == 1)
            {
                served++;
                skipped += s.Skipped[i];
            }
        }

        var lateness = new long[served + skipped];
        var work = new long[served];
        int n = 0, w = 0;
        long late = 0, afterTarget = 0, real = 0, generated = 0, repeated = 0, empty = 0;
        for (var i = 0; i < s.Count; i++)
        {
            if (s.Measured[i] != 1)
            {
                continue;
            }

            // The recorded deadline belongs to the first slot the loop was due for. When it came too late, that slot and
            // the following ones up to the served slot were each due a period apart; each gets its own sample.
            for (var j = 0; j <= s.Skipped[i]; j++)
            {
                lateness[n++] = Math.Max(0, s.Wake[i] - (s.Deadline[i] + (long)Math.Round(j * periodNs)));
            }

            work[w++] = s.End[i] - s.Wake[i];
            if (s.Reason[i] == (byte)SlotReason.Late)
            {
                late++;
            }

            if (s.End[i] > s.Target[i])
            {
                afterTarget++;
            }

            switch ((SlotKind)s.Kind[i])
            {
                case SlotKind.Real:
                    real++;
                    break;
                case SlotKind.Generated:
                    generated++;
                    break;
                case SlotKind.Repeat:
                    repeated++;
                    break;
                default:
                    empty++;
                    break;
            }
        }

        long overGate = 0;
        foreach (var x in lateness)
        {
            if (x > gateNs)
            {
                overGate++;
            }
        }

        return new PhaseStats
        {
            Name = s.Name,
            Slots = served,
            WakeLateness = Distribution.Of(lateness),
            Work = Distribution.Of(work),
            Skipped = skipped,
            Late = late,
            AfterTarget = afterTarget,
            OverGate = overGate,
            Real = real,
            Generated = generated,
            Repeated = repeated,
            Empty = empty,
            Overflow = s.Overflow,
        };
    }
}

internal sealed record JitterBenchResult
{
    public required JitterBenchOptions Options { get; init; }

    public required IReadOnlyList<BenchBlock> Blocks { get; init; }

    public JitterSamples? Baseline { get; init; }

    public required JitterSamples Full { get; init; }

    public PhaseStats? BaselineStats { get; init; }

    public required PhaseStats FullStats { get; init; }

    public required ThreadSchedulingReport PacingThread { get; init; }

    public required ThreadSchedulingReport SourceThread { get; init; }

    public required string GcConfiguration { get; init; }

    public GCLatencyMode GcModeRequested { get; init; }

    public GCLatencyMode GcModeObtained { get; init; }

    /// <summary>The latency mode after the playback scope was disposed (the restored one).</summary>
    public GCLatencyMode GcModeAfter { get; init; }

    /// <summary>Collections over the whole run, thread start-up and warm-up included (main thread's view).</summary>
    public GcSnapshot RunGc { get; init; }

    /// <summary>Collections from the first measured slot to the end of the run, read by the pacing thread.</summary>
    public GcSnapshot MeasuredGc { get; init; }

    public long MeasuredWindowNs { get; init; }

    /// <summary>Bytes the pacing thread allocated from the first measured slot to the end of the run.</summary>
    public long PacingAllocatedBytes { get; init; }

    public long SourceFrames { get; init; }

    public long SourceDropped { get; init; }

    public long SourceAllocatedBytes { get; init; }

    public long TelemetryRecords { get; init; }

    public long TelemetryDropped { get; init; }

    public SchedulerCounters Counters { get; init; }

    public SchedulerDiagnostics Diagnostics { get; init; }

    public long ClockReadNs { get; init; }

    public string? LoadBefore { get; init; }

    public string? LoadAfter { get; init; }

    public int FullBlocks => Blocks.Count(static b => b.Full);

    /// <summary>Both checks passed: the runtime-jitter gate and the missed-slot budget.</summary>
    public bool GatePassed => RuntimeGatePassed && MissedBudgetPassed;

    /// <summary>The design doc's gate: p99 wake lateness, no collection, no hot-thread allocation.</summary>
    public bool RuntimeGatePassed => RuntimeGateFailures.Count == 0;

    /// <summary>The full loop missed no more slots than <c>MaxMissed</c>.</summary>
    public bool MissedBudgetPassed => FullStats.Missed <= Options.MaxMissed;

    public IReadOnlyList<string> RuntimeGateFailures
    {
        get
        {
            var ic = CultureInfo.InvariantCulture;
            var failures = new List<string>();
            var f = FullStats;
            if (f.Slots == 0)
            {
                failures.Add("the full loop recorded no measured slots");
            }

            if (f.WakeLateness.P99 > Options.GateNs)
            {
                failures.Add(string.Create(ic,
                    $"p99 wake lateness {Us(f.WakeLateness.P99)} us exceeds the {Ms(Options.GateNs)} ms gate"));
            }

            if (MeasuredGc.Collections != 0)
            {
                failures.Add(string.Create(ic, $"{MeasuredGc.Collections} GC collection(s) during the measured time"));
            }

            if (PacingAllocatedBytes != 0)
            {
                failures.Add(string.Create(ic, $"the pacing thread allocated {PacingAllocatedBytes} B during the measured time"));
            }

            if (SourceAllocatedBytes != 0)
            {
                failures.Add(string.Create(ic, $"the source thread allocated {SourceAllocatedBytes} B"));
            }

            if (f.Overflow != 0 || (BaselineStats?.Overflow ?? 0) != 0)
            {
                failures.Add("sample storage overflowed");
            }

            return failures;
        }
    }

    /// <summary>Every reason the run fails, runtime gate first.</summary>
    public IReadOnlyList<string> Failures
    {
        get
        {
            var failures = new List<string>(RuntimeGateFailures);
            if (!MissedBudgetPassed)
            {
                var f = FullStats;
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{f.Missed} missed slot(s) (skipped {f.Skipped}, done after display time {f.AfterTarget}) exceed the budget of {Options.MaxMissed}"));
            }

            return failures;
        }
    }

    internal static string Ms(long ns) => (ns / 1e6).ToString("0.000", CultureInfo.InvariantCulture);

    internal static string Us(long ns) => (ns / 1e3).ToString("0.0", CultureInfo.InvariantCulture);

    internal static string Us(double ns) => (ns / 1e3).ToString("0.0", CultureInfo.InvariantCulture);

    internal static string SignedUs(double ns) => (ns / 1e3).ToString("+0.0;-0.0;+0.0", CultureInfo.InvariantCulture);
}

/// <summary>Machine conditions worth recording next to timing numbers (Linux; null elsewhere).</summary>
internal static class MachineInfo
{
    public static string? LoadAverage() => FirstFields("/proc/loadavg", 3);

    public static string? CpuGovernor() => FirstFields("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor", 1);

    public static string? KernelRelease() => FirstFields("/proc/sys/kernel/osrelease", 1);

    /// <summary>NativeAOT has no JIT, so it reports no JIT-compiled methods. (RuntimeFeature.IsDynamicCodeSupported
    /// is false under the JIT too when the project sets PublishAot.)</summary>
    public static string RuntimeKind() =>
        System.Runtime.JitInfo.GetCompiledMethodCount() == 0 ? "NativeAOT" : "JIT (CoreCLR, not the published binary)";

    private static string? FirstFields(string path, int count)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var parts = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join(' ', parts.Take(count));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
