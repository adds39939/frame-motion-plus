using System.Globalization;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Simulation;
using FrameMotionPlus.Core.Telemetry;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Support;

/// <summary>One sweep run: clock mode, source rate, display rate, disturbance and seed.</summary>
public sealed record SweepCase(string Mode, double SourceFps, long DisplayHz, string Disturbance, ulong Seed = 1)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Mode} {SourceFps}->{DisplayHz} {Disturbance} seed {Seed}");
}

/// <summary>Per-second aggregates of a run, collected without storing every slot.</summary>
internal sealed class WindowedSink : ITelemetrySink
{
    private long _lastD = long.MinValue;
    private int _lastDirection;

    public WindowedSink(int seconds)
    {
        Shown = new long[seconds + 1];
        Real = new long[seconds + 1];
        Generated = new long[seconds + 1];
        Missed = new long[seconds + 1];
        Reversals = new long[seconds + 1];
        MaxLatency = new long[seconds + 1];
        MinD = Enumerable.Repeat(long.MaxValue, seconds + 1).ToArray();
        MaxD = Enumerable.Repeat(long.MinValue, seconds + 1).ToArray();
    }

    public long[] Shown { get; }

    public long[] Real { get; }

    public long[] Generated { get; }

    public long[] Missed { get; }

    public long[] Reversals { get; }

    public long[] MaxLatency { get; }

    public long[] MinD { get; }

    public long[] MaxD { get; }

    public void Write(in FrameTimelineRecord r)
    {
        var w = (int)Math.Clamp(r.TargetDisplayNs / Nanoseconds.PerSecond, 0, Shown.Length - 1);
        if (r.Kind is SlotKind.Real or SlotKind.Generated)
        {
            Shown[w]++;
            MaxLatency[w] = Math.Max(MaxLatency[w], r.ActualDisplayNs - r.ContentNs);
            if (r.Kind == SlotKind.Real)
            {
                Real[w]++;
            }
            else
            {
                Generated[w]++;
            }
        }

        if (r.Missed)
        {
            Missed[w]++;
        }

        if (r.Kind != SlotKind.Empty)
        {
            MinD[w] = Math.Min(MinD[w], r.PipelineDelayNs);
            MaxD[w] = Math.Max(MaxD[w], r.PipelineDelayNs);
            if (_lastD != long.MinValue && r.PipelineDelayNs != _lastD)
            {
                var direction = Math.Sign(r.PipelineDelayNs - _lastD);
                if (_lastDirection != 0 && direction != _lastDirection)
                {
                    Reversals[w]++;
                }

                _lastDirection = direction;
            }

            _lastD = r.PipelineDelayNs;
        }
    }
}

/// <summary>
/// Disturbance sweep: builds a scenario, runs it through the real scheduler and simulator, and checks liveness and
/// bounds rather than exact numbers. One-shot disturbances happen at 40 % of the run and are over well before the
/// final 5 s, which must be back in steady state.
/// </summary>
internal static class DisturbanceSweep
{
    public const long Ms = Nanoseconds.PerMillisecond;
    public const long Second = Nanoseconds.PerSecond;

    public static readonly (double Fps, long Hz)[] Ratios =
    [
        (60, 60),     // 1:1
        (60, 120),    // 2x
        (40, 120),    // 3x
        (30, 120),    // 4x
        (59.94, 120), // non-integer, slow drift
        (30, 144),    // non-integer
        (144, 120),   // source faster than display
    ];

    public static readonly string[] Disturbances =
    [
        "none", "drops", "frame-jitter", "d-jitter", "skew+", "skew-", "ingest-jitter", "wake-late", "wake-jitter",
        "stall", "rate-half", "rate-double", "rate-alternating", "d-spike", "d-slow", "reanchor+50", "reanchor-50",
        "cut-pause", "combo",
    ];

    public static IEnumerable<SweepCase> Cases(params ulong[] seeds)
    {
        foreach (var seed in seeds)
        {
            foreach (var mode in new[] { "fixed", "vrr" })
            {
                foreach (var (fps, hz) in Ratios)
                {
                    foreach (var disturbance in Disturbances)
                    {
                        yield return new SweepCase(mode, fps, hz, disturbance, seed);
                    }
                }
            }
        }
    }

    public static List<string> Run(SweepCase c, int seconds = 30) => Run(c, seconds, out _);

    public static List<string> Run(SweepCase c, int seconds, out SimulationResult result)
    {
        var rate = FrameRate.FromDouble(c.SourceFps);
        var display = FrameRate.FromHz(c.DisplayHz);
        var at = seconds * Second * 2 / 5;
        long d = 2 * Ms, dJitter = 0, dFinal = 2 * Ms, wake = 0, lag = 0;
        var finalRate = rate;
        var events = 0;
        long pauseFrom = long.MaxValue, pauseUntil = long.MinValue;

        var source = new SimulatedSourceOptions { Rate = rate, Seed = c.Seed };
        var sim = new SimulationOptions { DurationNs = seconds * Second, StatsStartNs = 2 * Second, Seed = c.Seed + 100 };
        switch (c.Disturbance)
        {
            case "drops":
                source = source with { DropProbability = 0.05 };
                break;
            case "frame-jitter":
                source = source with { TimestampJitterNs = (long)Math.Min(Ms, rate.PeriodNs / 5) };
                break;
            case "d-jitter":
                dJitter = Ms / 2;
                break;
            case "skew+":
                source = source with { ClockSkewPpm = 80 };
                break;
            case "skew-":
                source = source with { ClockSkewPpm = -80 };
                break;
            case "ingest-jitter":
                lag = 4 * Ms;
                source = source with { IngestLatencyNs = Ms, IngestJitterNs = 3 * Ms };
                break;
            case "wake-late":
                wake = 600_000;
                sim = sim with { WakeLatenessNs = wake };
                break;
            case "wake-jitter":
                wake = 600_000;
                sim = sim with { WakeJitterNs = wake };
                break;
            case "stall":
                events = 1;
                pauseFrom = at;
                pauseUntil = at + (300 * Ms);
                sim = sim with { StallAtNs = at, StallDurationNs = 300 * Ms };
                break;
            case "rate-half":
                events = 1;
                finalRate = new FrameRate(rate.Numerator, rate.Denominator * 2);
                source = source with { RateChangeAtNs = at, RateAfterChange = finalRate };
                break;
            case "rate-double":
                events = 1;
                finalRate = new FrameRate(rate.Numerator * 2, rate.Denominator);
                source = source with { RateChangeAtNs = at, RateAfterChange = finalRate };
                break;
            case "rate-alternating":
                events = 1;
                finalRate = new FrameRate(rate.Numerator * 2, rate.Denominator * 7); // mean of 3 and 4 periods
                source = source with { RateChangeAtNs = at, CadenceAfterChange = new[] { 3, 4 } };
                break;
            case "d-spike":
                events = 2;
                sim = sim with { GenerationTimeSpikeFromNs = at, GenerationTimeSpikeUntilNs = at + Second, GenerationTimeSpikeNs = 9 * Ms };
                break;
            case "d-slow":
                events = 1;
                dFinal = 10 * Ms;
                sim = sim with { GenerationTimeChangeAtNs = at, GenerationTimeAfterChangeNs = 10 * Ms };
                break;
            case "reanchor+50":
            case "reanchor-50":
                events = 1;
                d = dFinal = 10 * Ms; // fallback stride > 1 where the ratio allows it
                sim = sim with { ReanchorAtNs = at, ReanchorShiftNs = c.Disturbance == "reanchor+50" ? 50_000 : -50_000 };
                break;
            case "cut-pause":
                events = 1;
                pauseFrom = at;
                pauseUntil = at + (2 * Second);
                source = source with { DiscontinuityAtNs = at, Holds = new[] { new SimulatedHold(at - Ms, 2 * Second) } };
                break;
            case "combo":
                dJitter = 300_000;
                wake = 300_000;
                lag = 2 * Ms;
                source = source with
                {
                    DropProbability = 0.02,
                    TimestampJitterNs = (long)Math.Min(500_000, rate.PeriodNs / 5),
                    IngestLatencyNs = Ms,
                    IngestJitterNs = Ms,
                    ClockSkewPpm = 40,
                };
                sim = sim with { WakeJitterNs = wake };
                break;
            default:
                break;
        }

        if (dFinal == 2 * Ms)
        {
            dFinal = d;
        }

        sim = sim with { Source = source, GenerationTimeNs = d, GenerationJitterNs = dJitter };
        var options = new SchedulerOptions
        {
            ClockMode = c.Mode == "vrr" ? ClockMode.Vrr : ClockMode.FixedRefresh,
            DisplayRate = display,
            InitialGenerationTimeNs = d + dJitter,
        };

        var sink = new WindowedSink(seconds);
        var scheduler = new FrameScheduler(options);
        using var generator = new NullFrameGenerator();
        using var presenter = new NullPresenter(display, 0, c.Mode == "vrr");
        result = new PipelineSimulator(sim, scheduler, generator, presenter, sink).Run();

        var violations = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok)
            {
                violations.Add(message);
            }
        }

        Check(result.CausalityViolations == 0, $"{result.CausalityViolations} causality violations");
        Check(result.ContentRegressions == 0, $"{result.ContentRegressions} content regressions");
        Check(result.TimestepInconsistencies == 0, $"{result.TimestepInconsistencies} timestep inconsistencies");
        Check(result.Missed <= 2 * events, $"{result.Missed} missed presents for {events} disturbance events");

        var finalFrom = seconds - 5;
        var finalMissed = 0L;
        long finalShown = 0, finalReal = 0, finalGenerated = 0, finalReversals = 0, finalMaxLatency = 0;
        long finalMinD = long.MaxValue, finalMaxD = long.MinValue;
        for (var w = finalFrom; w < seconds; w++)
        {
            finalMissed += sink.Missed[w];
            finalShown += sink.Shown[w];
            finalReal += sink.Real[w];
            finalGenerated += sink.Generated[w];
            finalReversals += sink.Reversals[w];
            finalMaxLatency = Math.Max(finalMaxLatency, sink.MaxLatency[w]);
            finalMinD = Math.Min(finalMinD, sink.MinD[w]);
            finalMaxD = Math.Max(finalMaxD, sink.MaxD[w]);
        }

        Check(finalMissed == 0, $"{finalMissed} missed presents in the final steady state");

        // Liveness: outside a pause or stall every second shows images at a rate near the source's.
        var minPerSecond = Math.Min(Math.Min(rate.Hz, finalRate.Hz), display.Hz) * 0.4;
        for (var w = 3; w < seconds; w++)
        {
            var windowStart = w * Second;
            var overlapsPause = windowStart + Second > pauseFrom - (200 * Ms) && windowStart < pauseUntil + Second;
            if (!overlapsPause)
            {
                Check(sink.Shown[w] >= minPerSecond, string.Create(CultureInfo.InvariantCulture, $"only {sink.Shown[w]} images shown in second {w}"));
            }
        }

        // Generation must be active again in the final state whenever the design permits it there: gaps up to the
        // 100 ms hold cap, d within some output interval, and on a fixed panel showing an integer number of refreshes
        // per frame, a stride that divides it (only then are generated frames evenly paced).
        var t = finalRate.PeriodNs;
        var p = display.PeriodNs;
        var need = dFinal + dJitter + 500_000 + wake;
        var longestGap = c.Disturbance == "rate-alternating" ? rate.PeriodNs * 4 : t;
        var slots = t / p;
        var integerSlots = Math.Abs(slots - Math.Round(slots)) < 1e-3 && c.Disturbance != "rate-alternating" ? (int)Math.Round(slots) : 0;
        var permitted = longestGap <= 100 * Ms && (c.Mode == "vrr"
            ? Math.Floor(t / p * 1.02) >= 2 && t / 2 >= need * 1.12
            : Enumerable.Range(1, 16).Any(f => f * p >= need * 1.12 && f * p <= 0.85 * t && (integerSlots == 0 || (integerSlots % f == 0 && f < integerSlots))));
        if (permitted)
        {
            Check(finalGenerated >= 0.3 * finalReal, $"generation not active in the final state ({finalGenerated} generated, {finalReal} real)");
        }

        // Latency back in its band: at most T + d + margin + wake + lag + one refresh (plus jitter allowances).
        var upper = (long)(t + p) + need + lag + (long)source.TimestampJitterNs * 2 + (2 * Ms);
        Check(finalShown > 0 && finalMaxLatency <= upper,
            string.Create(CultureInfo.InvariantCulture, $"final latency {finalMaxLatency / 1e6:0.000} ms above {upper / 1e6:0.000} ms"));

        // D steady: no oscillation and a bounded range in the final 5 s.
        Check(finalReversals <= 4, $"D reversed direction {finalReversals} times in the final 5 s");
        Check(finalMaxD - finalMinD <= (long)p + (3 * Ms),
            string.Create(CultureInfo.InvariantCulture, $"D ranged {(finalMaxD - finalMinD) / 1e6:0.000} ms in the final 5 s"));

        return violations;
    }
}
