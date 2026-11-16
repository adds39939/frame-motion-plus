using FrameMotionPlus.Core.Statistics;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Cadence;

/// <summary>
/// Thresholds on the GPU cadence metric: the maximum per-tile mean absolute luma difference between a
/// captured frame and the last unique frame, normalised to [0, 1] of full-scale code values.
/// </summary>
public readonly record struct CadenceThresholds(double Low, double High);

public sealed record CadenceOptions
{
    /// <summary>SDR thresholds (gamma-encoded code values).</summary>
    public CadenceThresholds Sdr { get; init; } = new(0.002, 0.010);

    /// <summary>PQ thresholds. PQ spends more code values on dark and highlight detail, so they differ from SDR.</summary>
    public CadenceThresholds Pq { get; init; } = new(0.003, 0.015);

    /// <summary>Unique-frame intervals averaged for the frame-rate estimate.</summary>
    public int RateWindow { get; init; } = 32;

    /// <summary>
    /// The reported frame rate only jumps when the estimate moves by more than this fraction. Smaller moves are
    /// followed once a whole window of new intervals has arrived, so no error of this size is left behind.
    /// </summary>
    public double RateHysteresis { get; init; } = 0.02;

    /// <summary>
    /// Gaps longer than this many estimated intervals are holds and do not feed the rate estimate, unless they recur:
    /// <see cref="ReseedAfterConsecutiveHolds"/> of them in a row that agree with each other, or held gaps making up a
    /// quarter of the recent intervals, re-seed the estimate.
    /// </summary>
    public double HoldIntervalFactor { get; init; } = 2.5;

    /// <summary>Gaps longer than this are always holds (menus, pauses, loading), whatever the estimated rate.</summary>
    public long MaxIntervalNs { get; init; } = 200_000_000;

    /// <summary>
    /// This many consecutive, mutually consistent gaps longer than the hold factor mean the game's rate dropped by more
    /// than <see cref="HoldIntervalFactor"/> (a 120 fps menu into 40 fps gameplay), not a pause: the estimate is
    /// re-seeded from them. The same number of intervals seeds the estimate at start, before the hold factor applies.
    /// </summary>
    public int ReseedAfterConsecutiveHolds { get; init; } = 3;

    public void Validate()
    {
        CheckThresholds(Sdr, nameof(Sdr));
        CheckThresholds(Pq, nameof(Pq));
        ArgumentOutOfRangeException.ThrowIfLessThan(RateWindow, 2, nameof(RateWindow));
        if (!(RateHysteresis >= 0 && RateHysteresis < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(RateHysteresis), RateHysteresis, "Must be in [0, 1).");
        }

        if (!(HoldIntervalFactor > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(HoldIntervalFactor), HoldIntervalFactor, "Must be greater than 1.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxIntervalNs, nameof(MaxIntervalNs));
        ArgumentOutOfRangeException.ThrowIfLessThan(ReseedAfterConsecutiveHolds, 2, nameof(ReseedAfterConsecutiveHolds));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReseedAfterConsecutiveHolds, Math.Min(16, RateWindow), nameof(ReseedAfterConsecutiveHolds));
    }

    private static void CheckThresholds(CadenceThresholds t, string name)
    {
        if (!(t.Low >= 0 && t.High > t.Low))
        {
            throw new ArgumentOutOfRangeException(name, t, "Thresholds need 0 <= Low < High.");
        }
    }
}

/// <summary>Classification of one captured frame.</summary>
public readonly record struct CadenceResult
{
    public bool IsUnique { get; init; }

    /// <summary>The metric fell between the low and high thresholds and the cadence decided.</summary>
    public bool Ambiguous { get; init; }

    /// <summary>For unique frames, the content timestamp (the capture time of its first appearance).</summary>
    public long ContentTimeNs { get; init; }

    /// <summary>For unique frames, the interval since the previous unique frame; 0 for the first.</summary>
    public long IntervalNs { get; init; }
}

/// <summary>
/// CPU half of cadence detection. A PS5 sends a fixed-refresh signal, so a 30 fps game arrives as a 60 Hz
/// signal with every picture repeated. Given the GPU's max-tile-difference metric per captured frame, this
/// classifies frames as unique or repeat and estimates the game's frame rate.
/// <para>
/// Hysteresis: a metric at or above <c>High</c> is always new content and at or below <c>Low</c> is
/// always a repeat. In between, the cadence decides: the frame counts as new only if a new frame is due,
/// i.e. at least the shortest recent unique interval less half a signal period has passed since the last unique
/// frame. The shortest interval, not the mean, because uneven cadences deliver new frames on their short interval:
/// 24 fps in 60 Hz alternates 3 and 2 signal periods, 40 fps alternates 2 and 1. Small HUD changes still exceed
/// <c>High</c> because the metric is a per-tile maximum.
/// </para>
/// <para>
/// Rate estimate: recent unique intervals whose both ends were decided by a clear metric (at or above <c>High</c>).
/// Intervals that start at the first capture (which may be the last repeat of a picture) or touch an in-band decision
/// never feed it, and neither do clear intervals until a run of clear frames has followed the last in-band decision:
/// in-band frames split long gaps, so the short intervals around them alone would bias the estimate. Gaps longer than
/// the hold factor are holds (pauses, hitches) unless they recur: a few in a row that agree, or a quarter of the
/// recent intervals, mean the cadence changed, and re-seed the estimate. The reported rate is the mean over whole
/// cycles of the cadence when the window repeats with a short cycle, so it is exact for steady cadences.
/// </para>
/// </summary>
public sealed class CadenceTracker
{
    /// <summary>Consecutive hold gaps agree with each other when the longest is at most this many times the shortest.
    /// For a steady game rate, gaps quantised to the signal differ by at most one period, and a hold gap is at least
    /// three periods long (over 2.5 intervals of at least one period), so they agree within 4/3 plus jitter.</summary>
    private const double HoldRunSpread = 1.5;

    /// <summary>Held gaps that make up at least 1/this of the recent intervals are part of the cadence, not pauses.</summary>
    private const int HeldShareDivisor = 4;

    /// <summary>After an in-band unique frame, clear intervals feed the estimate only once this many clear unique frames
    /// in a row have followed it (until then they wait, and another in-band frame discards them).</summary>
    private const int ClearRunAfterInBand = 8;

    /// <summary>Longest cadence cycle, in intervals, that the report averages over whole cycles.</summary>
    private const int MaxCadenceCycle = 16;

    private readonly CadenceOptions _options;
    private readonly RollingPercentile _uniqueIntervals;
    private readonly RollingPercentile _signalIntervals;

    // Recent clear intervals up to MaxIntervalNs, accepted or held, oldest first from _recentNext once full.
    private readonly long[] _recent;
    private readonly bool[] _recentHeld;
    private readonly int _reseedHeldCount;
    private int _recentNext;
    private int _recentCount;
    private int _recentHeldCount;
    private int _consecutiveHeld;

    private readonly long[] _pending = new long[ClearRunAfterInBand];
    private int _pendingCount;
    private int _clearSinceInBand = ClearRunAfterInBand;

    // The rate window's mean after each of the most recent accepted intervals.
    private readonly double[] _windowMeans;
    private int _windowMeansNext;
    private int _windowMeansCount;

    private long _lastCaptureNs = long.MinValue;
    private long _lastUniqueNs = long.MinValue;
    private bool _lastUniqueClear;
    private long _reportedIntervalNs;
    private int _samplesSinceReportChange;

    public CadenceTracker(CadenceOptions? options = null)
    {
        _options = options ?? new CadenceOptions();
        _options.Validate();
        _uniqueIntervals = new RollingPercentile(_options.RateWindow);
        _signalIntervals = new RollingPercentile(_options.RateWindow);
        _recent = new long[_options.RateWindow];
        _recentHeld = new bool[_options.RateWindow];
        _windowMeans = new double[_options.RateWindow];
        _reseedHeldCount = Math.Max(_options.ReseedAfterConsecutiveHolds, _options.RateWindow / HeldShareDivisor);
    }

    public CadenceOptions Options => _options;

    /// <summary>Estimated interval between unique game frames, ns (0 until known).</summary>
    public long EstimatedIntervalNs => _reportedIntervalNs;

    public double EstimatedFrameRateHz => _reportedIntervalNs > 0 ? Nanoseconds.PerSecond / (double)_reportedIntervalNs : 0;

    /// <summary>Median interval between captured frames (the signal period), ns.</summary>
    public long SignalIntervalNs => _signalIntervals.Percentile(0.5);

    public long UniqueFrames { get; private set; }

    public long RepeatFrames { get; private set; }

    public long AmbiguousFrames { get; private set; }

    /// <summary>Diagnostics: how often each estimator path ran (tests use them to prove coverage).</summary>
    internal CadenceTrackerCounters Counters;

    /// <summary>
    /// Time since the last unique frame after which an in-band metric counts as new content (0 until intervals are
    /// known): the shortest interval in the rate window less half a signal period.
    /// </summary>
    public long NewFrameDueAfterNs => _uniqueIntervals.IsEmpty ? 0 : Math.Max(0, _uniqueIntervals.Min - (SignalIntervalNs / 2));

    public CadenceResult Process(long captureTimeNs, double maxTileDifference, bool isPq)
    {
        if (_lastCaptureNs != long.MinValue && captureTimeNs > _lastCaptureNs)
        {
            _signalIntervals.Add(captureTimeNs - _lastCaptureNs);
        }

        _lastCaptureNs = captureTimeNs;

        var thresholds = isPq ? _options.Pq : _options.Sdr;
        bool unique;
        var clear = false;
        var ambiguous = false;
        if (_lastUniqueNs == long.MinValue)
        {
            // Nothing to compare against: new by definition, but it may be the last repeat of a picture.
            unique = true;
        }
        else if (maxTileDifference >= thresholds.High)
        {
            unique = true;
            clear = true;
        }
        else if (maxTileDifference <= thresholds.Low)
        {
            unique = false;
        }
        else
        {
            ambiguous = true;
            AmbiguousFrames++;
            unique = captureTimeNs - _lastUniqueNs >= NewFrameDueAfterNs;
        }

        if (!unique)
        {
            RepeatFrames++;
            return new CadenceResult { IsUnique = false, Ambiguous = ambiguous };
        }

        var interval = _lastUniqueNs == long.MinValue ? 0 : captureTimeNs - _lastUniqueNs;
        if (ambiguous)
        {
            // It splits a gap; the clear intervals around it are suspect until a clear run follows.
            _pendingCount = 0;
            _clearSinceInBand = 0;
        }
        else if (clear)
        {
            if (_lastUniqueClear && interval > 0)
            {
                if (_clearSinceInBand >= ClearRunAfterInBand)
                {
                    AddInterval(interval);
                }
                else
                {
                    _pending[_pendingCount++] = interval;
                }
            }

            if (_clearSinceInBand < ClearRunAfterInBand && ++_clearSinceInBand == ClearRunAfterInBand)
            {
                for (var k = 0; k < _pendingCount; k++)
                {
                    AddInterval(_pending[k]);
                    Counters.WaitingIntervalsFed++;
                }

                _pendingCount = 0;
            }
        }

        _lastUniqueNs = captureTimeNs;
        _lastUniqueClear = clear;
        UniqueFrames++;
        return new CadenceResult { IsUnique = true, Ambiguous = ambiguous, ContentTimeNs = captureTimeNs, IntervalNs = interval };
    }

    public void Reset()
    {
        _uniqueIntervals.Clear();
        _signalIntervals.Clear();
        Array.Clear(_recentHeld);
        _recentNext = 0;
        _recentCount = 0;
        _recentHeldCount = 0;
        _consecutiveHeld = 0;
        _pendingCount = 0;
        _clearSinceInBand = ClearRunAfterInBand;
        _windowMeansNext = 0;
        _windowMeansCount = 0;
        _lastCaptureNs = long.MinValue;
        _lastUniqueNs = long.MinValue;
        _lastUniqueClear = false;
        _reportedIntervalNs = 0;
        _samplesSinceReportChange = 0;
    }

    private void AddInterval(long interval)
    {
        if (interval > _options.MaxIntervalNs)
        {
            _consecutiveHeld = 0; // a pause, menu or loading screen: never a frame rate
            Counters.Pauses++;
            return;
        }

        // The first few intervals seed the estimate unfiltered, so no single one decides what counts as a hold.
        var held = _uniqueIntervals.Count >= _options.ReseedAfterConsecutiveHolds
            && interval > _options.HoldIntervalFactor * _uniqueIntervals.Mean;
        PushRecent(interval, held);
        if (!held)
        {
            _consecutiveHeld = 0;
            _uniqueIntervals.Add(interval);
            _samplesSinceReportChange++;
            UpdateReportedInterval();
            return;
        }

        // Longer than a hold. Usually a pause or a hitch; but if the last few agree, the rate dropped (120 fps menu into
        // 40 fps gameplay), and if long gaps keep recurring, agreeing or not (3 and 5 periods), they are the cadence.
        _consecutiveHeld++;
        if (_consecutiveHeld >= _options.ReseedAfterConsecutiveHolds && RecentHoldsAgree(_options.ReseedAfterConsecutiveHolds))
        {
            Counters.AgreeingReseeds++;
            Reseed(_options.ReseedAfterConsecutiveHolds);
        }
        else if (_recentHeldCount >= _reseedHeldCount)
        {
            Counters.RecurringReseeds++;
            Reseed(AgeOfOldestRecentHold() + 1);
        }
    }

    private void PushRecent(long interval, bool held)
    {
        if (_recentCount == _recent.Length && _recentHeld[_recentNext])
        {
            _recentHeldCount--;
        }

        _recent[_recentNext] = interval;
        _recentHeld[_recentNext] = held;
        _recentHeldCount += held ? 1 : 0;
        _recentNext = _recentNext + 1 == _recent.Length ? 0 : _recentNext + 1;
        _recentCount = Math.Min(_recentCount + 1, _recent.Length);
    }

    private int RecentIndex(int age)
    {
        var index = _recentNext - 1 - age;
        return index < 0 ? index + _recent.Length : index;
    }

    private bool RecentHoldsAgree(int count)
    {
        long min = long.MaxValue, max = long.MinValue;
        for (var age = 0; age < count; age++)
        {
            var v = _recent[RecentIndex(age)];
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        return max <= HoldRunSpread * min;
    }

    private int AgeOfOldestRecentHold()
    {
        for (var age = _recentCount - 1; age > 0; age--)
        {
            if (_recentHeld[RecentIndex(age)])
            {
                return age;
            }
        }

        return 0;
    }

    /// <summary>Restarts the estimate from the <paramref name="count"/> most recent intervals, held ones included.</summary>
    private void Reseed(int count)
    {
        _uniqueIntervals.Clear();
        for (var age = count - 1; age >= 0; age--)
        {
            _uniqueIntervals.Add(_recent[RecentIndex(age)]);
        }

        Array.Clear(_recentHeld);
        _recentHeldCount = 0;
        _consecutiveHeld = 0;
        _windowMeansNext = 0;
        _windowMeansCount = 0;
        SetReported(TryCycleMean(out var cycleMean, out _) ? cycleMean : _uniqueIntervals.Mean);
    }

    private void UpdateReportedInterval()
    {
        var mean = _uniqueIntervals.Mean;
        _windowMeans[_windowMeansNext] = mean;
        _windowMeansNext = _windowMeansNext + 1 == _windowMeans.Length ? 0 : _windowMeansNext + 1;
        _windowMeansCount = Math.Min(_windowMeansCount + 1, _windowMeans.Length);

        // A steady cadence repeats with a short cycle: report its exact mean over whole cycles, moving only by more
        // than the cycle mean's own uncertainty (timestamp jitter), so the report neither drifts nor flickers.
        if (TryCycleMean(out var cycleMean, out var uncertainty))
        {
            if (Math.Abs(cycleMean - _reportedIntervalNs) > uncertainty + 1.0)
            {
                SetReported(cycleMean);
                Counters.CycleMeanReports++;
            }

            return;
        }

        // Irregular intervals, or a cycle too long to see whole. Mean, not median: a 40 fps game in a 60 Hz signal
        // alternates 1 and 2 signal periods.
        if (_reportedIntervalNs <= 0
            || Math.Abs(mean - _reportedIntervalNs) > _options.RateHysteresis * _reportedIntervalNs)
        {
            SetReported(mean);
            return;
        }

        // Hysteresis alone would freeze the report anywhere inside the band, up to RateHysteresis off for good. Once
        // the whole window has been replaced since the report last changed, the report must lie within the range the
        // window's mean itself has covered over that window; a value left over from the transition is replaced.
        if (_samplesSinceReportChange >= _uniqueIntervals.Capacity)
        {
            Counters.SettleChecks++;
            double low = double.MaxValue, high = double.MinValue;
            for (var k = 0; k < _windowMeansCount; k++)
            {
                low = Math.Min(low, _windowMeans[k]);
                high = Math.Max(high, _windowMeans[k]);
            }

            if (_reportedIntervalNs < low - 1.0 || _reportedIntervalNs > high + 1.0)
            {
                SetReported(mean);
            }
        }
    }

    /// <summary>
    /// When the whole rate window repeats with a cycle of at most <see cref="MaxCadenceCycle"/> intervals (each
    /// interval within a quarter signal period of the one a cycle earlier), the mean of its newest whole cycles, and
    /// that mean's uncertainty from the cycle-to-cycle differences. A window that is not a whole number of cycles
    /// (32 intervals of 50 fps in 60 Hz, a cycle of 1, 1, 1, 1, 2 periods) would otherwise be up to 2.5 % off.
    /// </summary>
    private bool TryCycleMean(out double mean, out double uncertainty)
    {
        var n = _uniqueIntervals.Count;
        var tolerance = Math.Max(SignalIntervalNs / 4, 1_000);
        for (var c = 1; c <= Math.Min(MaxCadenceCycle, n / 2); c++)
        {
            long worst = 0;
            var periodic = true;
            for (var age = 0; age + c < n; age++)
            {
                var d = Math.Abs(_uniqueIntervals.Recent(age) - _uniqueIntervals.Recent(age + c));
                if (d > tolerance)
                {
                    periodic = false;
                    break;
                }

                worst = Math.Max(worst, d);
            }

            if (periodic)
            {
                var whole = n / c * c;
                long sum = 0;
                for (var age = 0; age < whole; age++)
                {
                    sum += _uniqueIntervals.Recent(age);
                }

                mean = (double)sum / whole;
                uncertainty = (double)worst / whole;
                return true;
            }
        }

        mean = 0;
        uncertainty = 0;
        return false;
    }

    private void SetReported(double meanNs)
    {
        _reportedIntervalNs = (long)Math.Round(meanNs);
        _samplesSinceReportChange = 0;
    }
}

/// <summary>How often each <see cref="CadenceTracker"/> estimator path ran. Plain counters, never reset.</summary>
internal struct CadenceTrackerCounters
{
    /// <summary>Gaps over <see cref="CadenceOptions.MaxIntervalNs"/> treated as pauses.</summary>
    public long Pauses;

    /// <summary>Re-seeds after consecutive hold gaps that agree.</summary>
    public long AgreeingReseeds;

    /// <summary>Re-seeds after hold gaps recurring in a quarter of the recent intervals.</summary>
    public long RecurringReseeds;

    /// <summary>Intervals fed from the waiting list once a clear run followed an in-band frame.</summary>
    public long WaitingIntervalsFed;

    /// <summary>Report changes from the whole-cycle mean.</summary>
    public long CycleMeanReports;

    /// <summary>Settle checks of an irregular window against the range of its own means.</summary>
    public long SettleChecks;
}
