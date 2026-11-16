using FrameMotionPlus.Core.Statistics;

namespace FrameMotionPlus.Core.Scheduling;

/// <summary>
/// Robust statistics of the gaps between unique source frames, and the decision whether a gap is a hold.
/// <list type="bullet">
/// <item>The nominal interval T is the (low) median, so dropped frames do not inflate it.</item>
/// <item>The hold interval is a high percentile of the cadence. Long gaps (over <c>dropFactor</c> · T) are dropped
/// frames and excluded while they are rare; once they are at least 40 % of the window (left again below 30 %)
/// they are the cadence itself, as for a 40 fps game in a 60 Hz signal, and are covered.</item>
/// <item>The hold interval rises immediately and falls only by more than 2 % of T at a time.</item>
/// <item>A gap over <c>holdFactor</c> · T is a hold, but only once a few samples exist, so one glitch at start-up
/// cannot lock the estimate. Gaps over the absolute cap are always holds.</item>
/// <item>Several consecutive "hold" gaps below the cap mean the source rate fell (120 to 40 fps, or to an alternating
/// two-valued cadence): the estimate is re-seeded from them instead of treating every later frame as a hold. Their
/// sizes need not agree, so no steady source under the cap can keep the estimator holding indefinitely.</item>
/// <item>A reset (source discontinuity) clears the samples but keeps both estimates as priors until new samples
/// replace them.</item>
/// </list>
/// </summary>
public sealed class SourceIntervalEstimator
{
    private const double CadenceEnterPercentile = 0.6; // ≥ 40 % long
    private const double CadenceLeavePercentile = 0.7; // < 30 % long

    // The hold percentile of a jittery source wanders by a fraction of a millisecond from sample to sample. It rises
    // at once (longer intervals must be covered) but only falls by more than this fraction of T, so D does not chase
    // estimator noise.
    private const double HoldDecreaseTolerance = 0.02;

    private readonly long[] _ring;
    private readonly RollingPercentile _sorted;
    private readonly long[] _pending;
    private readonly double _holdFactor;
    private readonly double _dropFactor;
    private readonly double _holdPercentile;
    private readonly long _maxInterpolatedNs;
    private readonly int _minSamples;
    private int _next;
    private int _count;
    private int _pendingCount;
    private bool _holdIsPrior;

    public SourceIntervalEstimator(
        int window,
        double holdFactor,
        double dropFactor,
        double holdPercentile,
        long maxInterpolatedIntervalNs,
        int minSamplesForHolds,
        int reseedAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(reseedAfter, 2);
        _ring = new long[window];
        _sorted = new RollingPercentile(window);
        _pending = new long[reseedAfter];
        _holdFactor = holdFactor;
        _dropFactor = dropFactor;
        _holdPercentile = holdPercentile;
        _maxInterpolatedNs = maxInterpolatedIntervalNs;
        _minSamples = minSamplesForHolds;
    }

    /// <summary>Median interval T, ns. Kept across <see cref="Reset"/> as a prior; 0 until the first sample.</summary>
    public long NominalNs { get; private set; }

    /// <summary>Interval the hold must cover, ns (at least <see cref="NominalNs"/>).</summary>
    public long HoldIntervalNs { get; private set; }

    /// <summary>True when long intervals are part of the cadence rather than rare drops.</summary>
    public bool LongIntervalsAreCadence { get; private set; }

    public int Count => _count;

    public long Reseeds { get; private set; }

    /// <summary>Records a gap; returns true when it is a hold and must not be interpolated across.</summary>
    public bool Add(long gapNs)
    {
        if (gapNs > _maxInterpolatedNs)
        {
            _pendingCount = 0;
            return true;
        }

        if (_count >= _minSamples && gapNs > _holdFactor * NominalNs)
        {
            _pending[_pendingCount++] = gapNs;
            if (_pendingCount < _pending.Length)
            {
                return true;
            }

            ClearSamples();
            foreach (var g in _pending)
            {
                Insert(g);
            }

            _pendingCount = 0;
            Reseeds++;
            Recompute();
            return false;
        }

        _pendingCount = 0;
        Insert(gapNs);
        Recompute();
        return false;
    }

    /// <summary>Forgets the samples (source switch or seek); the last estimates remain as priors.</summary>
    public void Reset()
    {
        ClearSamples();
        _pendingCount = 0;
    }

    /// <summary>
    /// Greatest common divisor of the cadence intervals measured in display periods, and the longest of them.
    /// A fixed-refresh stride that divides it lands generated frames at the same slot in every interval.
    /// </summary>
    public int SlotGcd(double periodNs, out int maxSlots)
    {
        maxSlots = 0;
        var gcd = 0;
        var limit = LongIntervalsAreCadence ? long.MaxValue : (long)(_dropFactor * NominalNs);
        for (var i = 0; i < _count; i++)
        {
            var v = _ring[i];
            if (v > limit)
            {
                continue;
            }

            var slots = Math.Max(1, (int)Math.Round(v / periodNs));
            maxSlots = Math.Max(maxSlots, slots);
            gcd = Gcd(gcd, slots);
        }

        return Math.Max(1, gcd);
    }

    private void Insert(long gapNs)
    {
        _ring[_next] = gapNs;
        _next = _next + 1 == _ring.Length ? 0 : _next + 1;
        _count = Math.Min(_count + 1, _ring.Length);
        _sorted.Add(gapNs);
    }

    private void ClearSamples()
    {
        _sorted.Clear();
        _count = 0;
        _next = 0;

        // Keep the last hold interval as a prior (a cut followed by a pause must not collapse D), but let the first
        // new samples replace it outright rather than only through the slow decrease path.
        _holdIsPrior = true;
    }

    private void Recompute()
    {
        NominalNs = _sorted.Percentile(0.5);
        var threshold = (long)(_dropFactor * NominalNs);
        if (!LongIntervalsAreCadence && _sorted.Percentile(CadenceEnterPercentile) > threshold)
        {
            LongIntervalsAreCadence = true;
        }
        else if (LongIntervalsAreCadence && _sorted.Percentile(CadenceLeavePercentile) <= threshold)
        {
            LongIntervalsAreCadence = false;
        }

        var covered = LongIntervalsAreCadence
            ? _sorted.Percentile(_holdPercentile)
            : _sorted.PercentileAtOrBelow(_holdPercentile, threshold);
        covered = Math.Max(covered, NominalNs);
        if (_holdIsPrior || covered > HoldIntervalNs || HoldIntervalNs - covered > HoldDecreaseTolerance * NominalNs)
        {
            HoldIntervalNs = covered;
            _holdIsPrior = false;
        }
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
