using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Scheduling;

/// <summary>
/// Owns the pipeline delay D. Measures generation time d with a rolling percentile (p99 by default) over recent
/// samples, and slews D towards whatever target the scheduler computes. D rises quickly, because a late frame is
/// a visible miss, and falls slowly, because each change bends the content clock and must stay invisible.
/// Increases are never held back; a deadband only stops estimate noise from starting a decrease, and once a
/// decrease starts it runs to the target.
/// <para>
/// Samples also age out by time. With generation switched off no new samples arrive, and without ageing a
/// single spike would keep generation off for ever; once every sample has expired the estimate returns to the
/// initial (warm-up) value and the scheduler tries again. Each retry whose first sample shows d still well above
/// that value doubles the age before the next retry (up to 32x), so a persistently slow generator is probed ever
/// more rarely. While a retry has no sample yet, <see cref="GenerationLeadNs"/> stays at the last known estimate so
/// the probing generation is decided early enough to finish in time.
/// </para>
/// <para>
/// With few samples the p99 is just the maximum of a handful, which underestimates the tail; until
/// <see cref="ConfidentSampleCount"/> samples exist the estimate is never below the initial value. A step change in d
/// (quality switch, thermal throttling) would take about 1 % of the window to show in the p99, a late frame per
/// sample; the estimate is therefore also never below the median of the last three samples, which ignores single
/// outliers but follows a step after two samples.
/// </para>
/// </summary>
public sealed class PipelineDelayTuner
{
    /// <summary>D may move at most one second per second.</summary>
    public const long MaxSlewNsPerSecond = Nanoseconds.PerSecond;

    /// <summary>Samples needed before the percentile alone is trusted.</summary>
    public const int ConfidentSampleCount = 32;

    private const long MaxElapsedNs = 10 * Nanoseconds.PerSecond;
    private const int MaxBackoffFactor = 32;
    private const double FailedProbeRatio = 1.25;

    private readonly AgedPercentile _generationTimes;
    private readonly double _percentile;
    private readonly long _initialGenerationTimeNs;
    private readonly long _slewUpNsPerSecond;
    private readonly long _slewDownNsPerSecond;
    private readonly long _deadbandNs;
    private readonly long _maxSampleAgeNs;
    private bool _fallingToTarget;
    private long _clockNs;
    private long _currentMaxAgeNs;
    private long _lastKnownNs;
    private bool _probing;

    public PipelineDelayTuner(
        int window,
        double percentile,
        long initialGenerationTimeNs,
        long marginNs,
        long slewUpNsPerSecond,
        long slewDownNsPerSecond,
        long deadbandNs,
        long maxSampleAgeNs = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(marginNs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slewUpNsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slewDownNsPerSecond);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slewUpNsPerSecond, MaxSlewNsPerSecond);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slewDownNsPerSecond, MaxSlewNsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(deadbandNs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSampleAgeNs);
        _generationTimes = new AgedPercentile(window);
        _percentile = percentile;
        _initialGenerationTimeNs = initialGenerationTimeNs;
        MarginNs = marginNs;
        _slewUpNsPerSecond = slewUpNsPerSecond;
        _slewDownNsPerSecond = slewDownNsPerSecond;
        _deadbandNs = deadbandNs;
        _maxSampleAgeNs = maxSampleAgeNs;
        _currentMaxAgeNs = maxSampleAgeNs;
    }

    public long MarginNs { get; }

    public bool IsInitialized { get; private set; }

    public long CurrentNs { get; private set; }

    public long TargetNs { get; private set; }

    public int SampleCount => _generationTimes.Count;

    /// <summary>
    /// The configured percentile of recent d (never below the initial estimate until enough samples exist), or the
    /// initial estimate when no sample is recent enough. This optimistic value decides whether generation fits.
    /// </summary>
    public long GenerationTimeNs
    {
        get
        {
            var n = _generationTimes.Count;
            if (n == 0)
            {
                return _initialGenerationTimeNs;
            }

            var p = Math.Max(_generationTimes.Percentile(_percentile), _generationTimes.RecentMedianOfThree());
            return n < ConfidentSampleCount ? Math.Max(p, _initialGenerationTimeNs) : p;
        }
    }

    /// <summary>
    /// Generation time to budget the decision lead and D with. Equal to <see cref="GenerationTimeNs"/>, except
    /// during a retry before its first sample: then the last known estimate, so the probe finishes in time.
    /// </summary>
    public long GenerationLeadNs =>
        _generationTimes.Count == 0 ? Math.Max(_initialGenerationTimeNs, _lastKnownNs) : GenerationTimeNs;

    /// <summary>Current sample age limit, including retry back-off.</summary>
    public long MaxSampleAgeNs => _currentMaxAgeNs;

    public double MeanGenerationTimeNs =>
        _generationTimes.Count == 0 ? _initialGenerationTimeNs : _generationTimes.Mean;

    /// <summary>Adds a sample stamped with the latest time seen by <see cref="Expire"/>.</summary>
    public void AddGenerationSample(long durationNs) => AddGenerationSample(durationNs, _clockNs);

    public void AddGenerationSample(long durationNs, long atNs)
    {
        if (durationNs < 0)
        {
            return;
        }

        if (_probing)
        {
            // First sample of a retry: still well above the value the retry assumed means back off further.
            _probing = false;
            _currentMaxAgeNs = durationNs > FailedProbeRatio * _initialGenerationTimeNs
                ? Math.Min(_currentMaxAgeNs * 2, SaturatingMultiply(_maxSampleAgeNs, MaxBackoffFactor))
                : _maxSampleAgeNs;
        }

        _clockNs = Math.Max(_clockNs, atNs);
        _generationTimes.Add(atNs, durationNs);
    }

    /// <summary>Forgets samples older than the (backed-off) maximum age at <paramref name="nowNs"/>.</summary>
    public void Expire(long nowNs)
    {
        _clockNs = Math.Max(_clockNs, nowNs);
        if (_currentMaxAgeNs == long.MaxValue || _generationTimes.Count == 0)
        {
            return;
        }

        var before = GenerationTimeNs;
        _generationTimes.RemoveOlderThan(nowNs - _currentMaxAgeNs);
        if (_generationTimes.Count == 0)
        {
            _lastKnownNs = before;
            _probing = true;
        }
    }

    public void ResetGenerationStats() => _generationTimes.Clear();

    /// <summary>Jumps D straight to <paramref name="valueNs"/> (start-up, or after a source change).</summary>
    public void Reset(long valueNs)
    {
        CurrentNs = valueNs;
        TargetNs = valueNs;
        IsInitialized = true;
        _fallingToTarget = false;
    }

    /// <summary>Moves D towards <paramref name="targetNs"/> by at most the slew allowed in <paramref name="elapsedNs"/>.</summary>
    public long Step(long targetNs, long elapsedNs)
    {
        if (!IsInitialized)
        {
            Reset(targetNs);
            return CurrentNs;
        }

        TargetNs = targetNs;
        elapsedNs = Math.Clamp(elapsedNs, 0, MaxElapsedNs);
        if (targetNs > CurrentNs)
        {
            _fallingToTarget = false;
            CurrentNs += Math.Min(targetNs - CurrentNs, SlewStep(_slewUpNsPerSecond, elapsedNs));
        }
        else if (targetNs < CurrentNs && (_fallingToTarget || CurrentNs - targetNs > _deadbandNs))
        {
            _fallingToTarget = true;
            CurrentNs -= Math.Min(CurrentNs - targetNs, SlewStep(_slewDownNsPerSecond, elapsedNs));
        }

        if (CurrentNs == targetNs)
        {
            _fallingToTarget = false;
        }

        return CurrentNs;
    }

    private static long SaturatingMultiply(long value, int factor) =>
        value > long.MaxValue / factor ? long.MaxValue : value * factor;

    // Both factors are bounded (slew ≤ 1e9 ns/s, elapsed ≤ 1e10 ns), so the 128-bit product cannot overflow.
    private static long SlewStep(long nsPerSecond, long elapsedNs) =>
        (long)((Int128)nsPerSecond * elapsedNs / Nanoseconds.PerSecond);
}
