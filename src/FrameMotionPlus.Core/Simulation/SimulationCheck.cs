using System.Globalization;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Simulation;

public sealed record SimulationVerdict
{
    public bool Passed => Failures.Count == 0;

    public IReadOnlyList<string> Failures { get; init; } = [];

    /// <summary>True when the inputs are ideal (no drops, jitter, skew, holds, rate changes or stalls) and the
    /// pattern, multiplier and latency band were checked as well as the correctness counters.</summary>
    public bool Strict { get; init; }

    public bool IntegerRatio { get; init; }

    public double SlotsPerSourceFrame { get; init; }

    /// <summary>Multipliers the design doc allows for this configuration: largest M with d ≤ T/M (high) and the
    /// same with the scheduler's margin and measured wake allowance (low).</summary>
    public int ExpectedMultiplierLow { get; init; }

    public int ExpectedMultiplierHigh { get; init; }

    /// <summary>Multiplier observed in the output (from the set of timesteps).</summary>
    public int ObservedMultiplier { get; init; }

    public long ModelAddedLatencyNs { get; init; }

    public long LatencyLowerNs { get; init; }

    public long LatencyUpperNs { get; init; }

    public string LatencyRule { get; init; } = "";
}

/// <summary>
/// Judges a simulation against the design doc, from the configuration alone. It does not reuse any of the
/// scheduler's formulas or measurements: the multiplier comes from "d ≤ T/M" (and, on a fixed panel, M must divide
/// the refresh slots per source frame to be even), the latency band from ΔL = d + (M − 1)/M · T plus the margin,
/// plus up to one refresh on a fixed panel ("presentation adds up to one refresh") or up to T + d for a non-integer
/// ratio ("non-integer ratios push D towards T + d"). The pattern is checked from the output: the set of timesteps
/// must be exactly {k/M}, and real, generated and repeated slots must come in the ratio M implies. Correctness
/// counters (missed, regressions, causality, timestep consistency) are enforced on every run.
/// </summary>
public static class SimulationCheck
{
    private const double RateTolerance = 0.005;
    private const long LatencyToleranceNs = 50_000;

    public static SimulationVerdict Evaluate(SchedulerOptions options, SimulationOptions simulation, SimulationResult result)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(result);
        var failures = new List<string>();
        var src = simulation.Source;

        Require(failures, result.Missed == 0, $"{result.Missed} slots missed their target time");
        Require(failures, result.ContentRegressions == 0, $"{result.ContentRegressions} content regressions");
        Require(failures, result.CausalityViolations == 0, $"{result.CausalityViolations} decisions used frames before they arrived");
        Require(failures, result.TimestepInconsistencies == 0, $"{result.TimestepInconsistencies} decisions inconsistent with their frames and t");

        var t = src.Rate.PeriodNs;
        var p = options.DisplayRate.PeriodNs;
        var ratio = t / p;
        var num = (Int128)options.DisplayRate.Numerator * src.Rate.Denominator;
        var den = (Int128)options.DisplayRate.Denominator * src.Rate.Numerator;
        var integer = num % den == 0 && num >= den;
        var vrr = options.ClockMode == ClockMode.Vrr;
        var ideal = src.DropProbability == 0 && src.TimestampJitterNs == 0 && src.ClockSkewPpm == 0
            && src.Holds.IsEmpty && src.ExplicitTimestamps.IsEmpty && src.RateChangeAtNs == long.MaxValue
            && simulation.StallAtNs == long.MaxValue && simulation.GenerationTimeChangeAtNs == long.MaxValue
            && simulation.GenerationTimeSpikeUntilNs == long.MinValue;

        var d = simulation.GenerationTimeNs;
        var dHi = d + simulation.GenerationJitterNs;
        var lag = src.IngestLatencyNs;
        var lagHi = lag + src.IngestJitterNs;
        var wakeHi = simulation.WakeLatenessNs + simulation.WakeJitterNs;
        var margin = options.MarginNs;
        var tNs = (long)Math.Round(t);

        // Multipliers a display can show evenly: VRR any M up to the maximum rate; fixed only divisors of the slots.
        var slotsPerFrame = integer ? (int)(num / den) : 0;
        var mCap = vrr
            ? Math.Clamp((int)Math.Floor(ratio * (1 + RateTolerance)), 1, options.MaxMultiplier)
            : integer ? Math.Min(slotsPerFrame, options.MaxMultiplier) : 1;
        var mHigh = 1;
        var mLow = 1;
        for (var m = 2; m <= mCap; m++)
        {
            if (!vrr && slotsPerFrame % m != 0)
            {
                continue;
            }

            if (d <= t / m)
            {
                mHigh = m;
            }

            if (dHi + margin + wakeHi <= t / m)
            {
                mLow = m;
            }
        }

        var observed = ObservedMultiplier(result, failures, ideal && (vrr || integer));
        var verdict = new SimulationVerdict
        {
            Strict = ideal,
            IntegerRatio = integer,
            SlotsPerSourceFrame = ratio,
            ExpectedMultiplierLow = mLow,
            ExpectedMultiplierHigh = mHigh,
            ObservedMultiplier = observed,
        };

        if (!ideal)
        {
            return verdict with { Failures = failures, LatencyRule = "not checked: inputs are not ideal" };
        }

        Require(failures, result.StarvedSlots == 0, $"{result.StarvedSlots} starved slots on ideal inputs");
        Require(failures, result.HoldSlots == 0 && result.DiscontinuitySlots == 0, "hold or discontinuity slots on ideal inputs");
        Require(failures, result.Empty == 0, $"{result.Empty} empty slots after warm-up");
        Require(failures, result.Real + result.Generated > 0, "nothing was shown");

        long lower, upper;
        string rule;
        if (ratio < 1)
        {
            Require(failures, result.Generated == 0, "generated frames although the source is faster than the display");
            lower = lag;
            upper = lagHi + margin + wakeHi + (long)Math.Ceiling(Math.Max(p, t)) + LatencyToleranceNs;
            rule = "source faster than display: newest frame, no generation";
        }
        else if (vrr || integer)
        {
            Require(failures, observed >= mLow && observed <= mHigh,
                Invariant($"multiplier {observed} outside the {mLow}..{mHigh} the latency model allows"));
            var gen = result.Generated;
            var expectedGen = (observed - 1) * result.Real;
            Require(failures, Math.Abs(gen - expectedGen) <= 2 + (expectedGen / 100),
                Invariant($"{gen} generated for {result.Real} real frames; M = {observed} implies {expectedGen}"));
            var shown = result.Real + result.Generated;
            var expectedRepeats = vrr ? 0 : (long)Math.Round(result.Slots * (1.0 - (observed / (double)slotsPerFrame)));
            Require(failures, Math.Abs(result.Repeated - expectedRepeats) <= 2 + (result.Slots / 100),
                Invariant($"{result.Repeated} repeated slots; expected {expectedRepeats} with {shown} images shown"));
            Require(failures, vrr || result.SkippedSlots == 0, $"{result.SkippedSlots} skipped vblanks");
            Require(failures, !vrr || result.SkippedSlots == 0, $"{result.SkippedSlots} skipped VRR slots");

            var model = observed >= 2 ? LatencyModel.AddedLatencyNs(tNs, observed, d) : 0;
            var modelHi = observed >= 2 ? LatencyModel.AddedLatencyNs(tNs, observed, dHi) : 0;
            lower = model + lag;
            upper = vrr
                ? modelHi + lagHi + margin + wakeHi + LatencyToleranceNs
                : modelHi + lagHi + (3 * margin / 2) + wakeHi + (long)Math.Ceiling(p) + LatencyToleranceNs;
            rule = vrr
                ? "dL + margin (VRR presents at V)"
                : "dL + margin, plus up to one refresh to land real frames on vblanks";
            verdict = verdict with { ModelAddedLatencyNs = model };
        }
        else
        {
            // A non-integer ratio has no single M; the smallest generating one (2) bounds the hold from below.
            var mFloor = Math.Min(2, Math.Max(1, (int)Math.Floor(ratio)));
            var epsT = (long)Math.Ceiling(options.SnapEpsilon * t);
            var generationFits = dHi + margin + wakeHi <= p;
            if (generationFits)
            {
                Require(failures, result.Repeated == 0, $"{result.Repeated} repeated slots with generation in budget");
                Require(failures, result.Generated > 0, "no generated frames");
            }

            lower = (mFloor >= 2 ? LatencyModel.AddedLatencyNs(tNs, mFloor, d) : 0) + lag - epsT;
            upper = tNs + dHi + margin + wakeHi + lagHi + epsT + (generationFits ? 0 : (long)Math.Ceiling(p)) + LatencyToleranceNs;
            rule = "non-integer ratio: between dL(M = 2) and T + d + margin";
            verdict = verdict with { ModelAddedLatencyNs = mFloor >= 2 ? LatencyModel.AddedLatencyNs(tNs, mFloor, d) : 0 };
        }

        // Every shown image (real or generated) waits display time − content time; the band applies to all of them.
        Require(failures, result.ShownLatencyMinNs >= lower - LatencyToleranceNs && result.ShownLatencyMaxNs <= upper,
            Invariant($"latency {Ms(result.ShownLatencyMinNs)}..{Ms(result.ShownLatencyMaxNs)} ms outside {Ms(lower)}..{Ms(upper)} ms"));

        return verdict with { Failures = failures, LatencyLowerNs = lower, LatencyUpperNs = upper, LatencyRule = rule };
    }

    /// <summary>Multiplier implied by the generated timesteps; when <paramref name="exact"/> they must be exactly {k/M}.</summary>
    private static int ObservedMultiplier(SimulationResult result, List<string> failures, bool exact)
    {
        if (result.Generated == 0 || result.DistinctTimesteps.Count == 0)
        {
            return 1;
        }

        var m = (int)Math.Round(1.0 / result.DistinctTimesteps[0]);
        if (!exact)
        {
            return m;
        }

        var matches = !result.DistinctTimestepsOverflow && m >= 2 && result.DistinctTimesteps.Count == m - 1;
        for (var k = 1; matches && k < m; k++)
        {
            matches = Math.Abs(result.DistinctTimesteps[k - 1] - (k / (double)m)) < 1e-5;
        }

        Require(failures, matches,
            Invariant($"timesteps {{{string.Join(", ", result.DistinctTimesteps.Select(x => x.ToString("0.####", CultureInfo.InvariantCulture)))}}} are not {{k/{m}}}"));
        return m;
    }

    private static void Require(List<string> failures, bool condition, string message)
    {
        if (!condition)
        {
            failures.Add(message);
        }
    }

    private static string Ms(long ns) => Nanoseconds.ToMilliseconds(ns).ToString("0.000", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
