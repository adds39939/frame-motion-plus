using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Scheduling;

/// <summary>Configuration for <see cref="FrameScheduler"/>. Durations are nanoseconds.</summary>
public sealed record SchedulerOptions
{
    public ClockMode ClockMode { get; init; } = ClockMode.FixedRefresh;

    /// <summary>Fixed refresh: the panel refresh rate. VRR: the maximum refresh rate (minimum slot spacing).</summary>
    public FrameRate DisplayRate { get; init; } = FrameRate.FromHz(120);

    /// <summary>A vblank instant anchoring the fixed-refresh slot grid (refined later from present feedback).</summary>
    public long VblankAnchorNs { get; init; }

    /// <summary>Cap on output frames per source frame; VFG supports up to 8.</summary>
    public int MaxMultiplier { get; init; } = 8;

    /// <summary>A timestep within this fraction of 0 or 1 shows the real frame instead of generating. Whatever the
    /// value (including 0), a generated timestep is always strictly inside (0, 1), also after conversion to float.</summary>
    public double SnapEpsilon { get; init; } = 0.02;

    /// <summary>Safety margin added to the decision lead and to D.</summary>
    public long MarginNs { get; init; } = 500_000;

    /// <summary>Generation time assumed before any is measured (seed it from the warm-up run).</summary>
    public long InitialGenerationTimeNs { get; init; } = 2_000_000;

    public double GenerationTimePercentile { get; init; } = 0.99;

    public int GenerationTimeWindow { get; init; } = 256;

    /// <summary>Generation-time samples older than this are forgotten. When none are left (generation was off),
    /// the estimate falls back to <see cref="InitialGenerationTimeNs"/>, so a passing spike cannot disable
    /// generation for good. Each retry that finds d still too slow doubles the age (back-off, up to 32x).</summary>
    public long GenerationTimeMaxAgeNs { get; init; } = 5_000_000_000;

    public int SourceIntervalWindow { get; init; } = 256;

    /// <summary>Percentile of recent source intervals the hold must cover; uneven frame times above it starve.</summary>
    public double SourceIntervalPercentile { get; init; } = 0.9;

    /// <summary>Intervals longer than this many median intervals are dropped source frames while they are rare: they
    /// are still interpolated when inputs exist, but do not raise the hold (and so D) for every other frame.</summary>
    public double DropIntervalFactor { get; init; } = 1.75;

    /// <summary>A gap longer than this many median intervals is a hold: never interpolated across.
    /// It sits clearly above 2 so a single dropped frame (a gap of exactly 2T) is still interpolated.</summary>
    public double HoldIntervalFactor { get; init; } = 2.5;

    /// <summary>Gaps longer than this are always holds (menus, pauses), whatever the estimated rate.</summary>
    public long MaxInterpolatedIntervalNs { get; init; } = 100_000_000;

    /// <summary>Intervals required before any gap can be classified as a hold by the rate estimate.</summary>
    public int HoldMinSamples { get; init; } = 4;

    /// <summary>This many consecutive "hold" gaps below <see cref="MaxInterpolatedIntervalNs"/> mean the source rate
    /// dropped (to a steady or an alternating cadence): re-seed. No steady source under the cap can hold for ever.</summary>
    public int ReseedAfterConsecutiveHolds { get; init; } = 3;

    public long SlewUpNsPerSecond { get; init; } = 50_000_000;

    public long SlewDownNsPerSecond { get; init; } = 5_000_000;

    /// <summary>D only starts moving down once it exceeds its target by more than this; once moving it goes all the
    /// way. Increases are never deadbanded.</summary>
    public long DelayDeadbandNs { get; init; } = 100_000;

    /// <summary>VRR: relative tolerance on the maximum refresh rate when fitting slots between source frames, so clock
    /// skew or timestamp jitter does not halve the multiplier. Slots may then be up to this much closer than the
    /// panel's minimum refresh period.</summary>
    /// <remarks>Phase 6 obligation: a FIFO presenter would fall progressively behind such early presents. The VRR
    /// presenter must either drop a present the panel cannot take yet (newest wins) or feed the measured present lag
    /// back to the scheduler (via present feedback) so it is added to D.</remarks>
    public double VrrRateTolerance { get; init; } = 0.02;

    /// <summary>VRR: a generated slot whose next real frame comes too late for its planned time is shown up to this much
    /// later instead of being skipped (its content time moved by the same amount), if it still fits a shortest refresh
    /// period before that real frame's slot. 0 (the default here) skips it, as before.</summary>
    public long VrrLateGeneratedNs { get; init; }

    /// <summary>Fraction of the per-frame budget that must be spare before the multiplier is raised again.</summary>
    public double FallbackHeadroom { get; init; } = 0.1;

    /// <summary>How long the spare budget must last before the multiplier is raised again.</summary>
    public long FallbackDwellNs { get; init; } = 1_000_000_000;

    /// <summary>Unique frames required before the first slot is scheduled.</summary>
    public int StartupFrames { get; init; } = 8;

    public int TimelineCapacity { get; init; } = 64;

    public int CoherenceWindow { get; init; } = 128;

    public int CoherenceMinFrames { get; init; } = 8;

    /// <summary>Above this phase drift the source is treated as a non-integer ratio (59.94 on 120 drifts ~1000 ppm).</summary>
    public double CoherenceMaxDriftPpm { get; init; } = 500;

    /// <summary>Timestamp jitter tolerated while phase-locked (entering needs half of it). USB capture timestamps
    /// jitter by a few hundred microseconds; real frames still snap and the hold covers the jitter.</summary>
    public long CoherenceMaxJitterNs { get; init; } = 1_000_000;

    public void Validate()
    {
        if (!DisplayRate.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(DisplayRate), "Display rate must be set.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMultiplier, 1, nameof(MaxMultiplier));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxMultiplier, 64, nameof(MaxMultiplier));
        if (!(SnapEpsilon >= 0 && SnapEpsilon < 0.5))
        {
            throw new ArgumentOutOfRangeException(nameof(SnapEpsilon), SnapEpsilon, "Must be in [0, 0.5).");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(MarginNs, nameof(MarginNs));
        ArgumentOutOfRangeException.ThrowIfNegative(InitialGenerationTimeNs, nameof(InitialGenerationTimeNs));
        CheckFraction(GenerationTimePercentile, nameof(GenerationTimePercentile));
        CheckFraction(SourceIntervalPercentile, nameof(SourceIntervalPercentile));
        ArgumentOutOfRangeException.ThrowIfLessThan(GenerationTimeWindow, 1, nameof(GenerationTimeWindow));
        ArgumentOutOfRangeException.ThrowIfLessThan(SourceIntervalWindow, 2, nameof(SourceIntervalWindow));
        if (!(HoldIntervalFactor > 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(HoldIntervalFactor), HoldIntervalFactor, "Must be greater than 1.");
        }

        if (!(DropIntervalFactor > 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(DropIntervalFactor), DropIntervalFactor, "Must be greater than 1.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SlewUpNsPerSecond, nameof(SlewUpNsPerSecond));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SlewDownNsPerSecond, nameof(SlewDownNsPerSecond));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SlewUpNsPerSecond, PipelineDelayTuner.MaxSlewNsPerSecond, nameof(SlewUpNsPerSecond));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SlewDownNsPerSecond, PipelineDelayTuner.MaxSlewNsPerSecond, nameof(SlewDownNsPerSecond));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(GenerationTimeMaxAgeNs, nameof(GenerationTimeMaxAgeNs));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxInterpolatedIntervalNs, nameof(MaxInterpolatedIntervalNs));
        ArgumentOutOfRangeException.ThrowIfLessThan(HoldMinSamples, 1, nameof(HoldMinSamples));
        ArgumentOutOfRangeException.ThrowIfLessThan(ReseedAfterConsecutiveHolds, 2, nameof(ReseedAfterConsecutiveHolds));
        if (!(VrrRateTolerance >= 0 && VrrRateTolerance <= 0.1))
        {
            throw new ArgumentOutOfRangeException(nameof(VrrRateTolerance), VrrRateTolerance, "Must be in [0, 0.1].");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(DelayDeadbandNs, nameof(DelayDeadbandNs));
        ArgumentOutOfRangeException.ThrowIfNegative(VrrLateGeneratedNs, nameof(VrrLateGeneratedNs));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(VrrLateGeneratedNs, 100_000_000, nameof(VrrLateGeneratedNs));
        if (!(FallbackHeadroom >= 0 && FallbackHeadroom < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(FallbackHeadroom), FallbackHeadroom, "Must be in [0, 1).");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(FallbackDwellNs, nameof(FallbackDwellNs));
        ArgumentOutOfRangeException.ThrowIfLessThan(StartupFrames, 2, nameof(StartupFrames));
        ArgumentOutOfRangeException.ThrowIfLessThan(TimelineCapacity, 8, nameof(TimelineCapacity));
        if ((TimelineCapacity & (TimelineCapacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TimelineCapacity), TimelineCapacity, "Must be a power of two.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(CoherenceWindow, 2, nameof(CoherenceWindow));
        ArgumentOutOfRangeException.ThrowIfLessThan(CoherenceMinFrames, 2, nameof(CoherenceMinFrames));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CoherenceMinFrames, CoherenceWindow, nameof(CoherenceMinFrames));
        if (!(CoherenceMaxDriftPpm >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(CoherenceMaxDriftPpm), CoherenceMaxDriftPpm, "Must be non-negative.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(CoherenceMaxJitterNs, nameof(CoherenceMaxJitterNs));
    }

    private static void CheckFraction(double value, string name)
    {
        if (!(value > 0 && value <= 1))
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be in (0, 1].");
        }
    }
}
