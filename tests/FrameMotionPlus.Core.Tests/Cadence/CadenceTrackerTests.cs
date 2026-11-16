using FrameMotionPlus.Core.Cadence;
using FrameMotionPlus.Core.Statistics;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Cadence;

public class CadenceTrackerTests
{
    private static readonly FrameRate Signal60 = FrameRate.FromHz(60);
    private static readonly FrameRate Signal120 = FrameRate.FromHz(120);

    /// <summary>Feeds a fixed-refresh signal where <paramref name="isNew"/> says which captures carry a new picture.</summary>
    private static List<long> Feed(CadenceTracker tracker, FrameRate signal, int captures, Func<int, bool> isNew,
        double uniqueMetric = 0.05, double repeatMetric = 0.0, bool pq = false)
    {
        var unique = new List<long>();
        for (var i = 0; i < captures; i++)
        {
            var r = tracker.Process(signal.TickTimeNs(i), isNew(i) ? uniqueMetric : repeatMetric, pq);
            if (r.IsUnique)
            {
                unique.Add(r.ContentTimeNs);
            }
        }

        return unique;
    }

    [Fact]
    public void Thirty_in_60_keeps_every_other_capture_at_its_first_appearance()
    {
        var tracker = new CadenceTracker();
        var unique = Feed(tracker, Signal60, 600, i => i % 2 == 0);

        Assert.Equal(300, unique.Count);
        Assert.Equal(Enumerable.Range(0, 300).Select(k => Signal60.TickTimeNs(2 * k)), unique);
        Assert.InRange(tracker.EstimatedFrameRateHz, 29.99, 30.01);
        Assert.InRange(tracker.SignalIntervalNs, 16_666_666, 16_666_667);
    }

    [Fact]
    public void Forty_in_120_keeps_every_third_capture()
    {
        var tracker = new CadenceTracker();
        var unique = Feed(tracker, Signal120, 1200, i => i % 3 == 0);

        Assert.Equal(400, unique.Count);
        Assert.InRange(tracker.EstimatedFrameRateHz, 39.99, 40.01);
    }

    [Fact]
    public void Dropped_frames_in_a_60_fps_game_appear_as_double_intervals()
    {
        var tracker = new CadenceTracker();
        var dropped = new HashSet<int> { 50, 51, 120, 300 };
        var unique = Feed(tracker, Signal60, 600, i => !dropped.Contains(i));

        Assert.Equal(600 - dropped.Count, unique.Count);
        var intervals = unique.Zip(unique.Skip(1), (a, b) => b - a).ToList();
        Assert.Equal(1, intervals.Count(x => x > 40_000_000)); // 50-51 leaves a triple interval
        Assert.Equal(3, intervals.Count(x => x > 20_000_000)); // plus doubles at 120 and 300
        Assert.InRange(tracker.EstimatedFrameRateHz, 58, 60.01);
    }

    [Fact]
    public void Ambiguous_metrics_are_resolved_by_the_cadence()
    {
        // 30 fps slow pan: new pictures barely differ (between Low and High), repeats are noise below Low.
        var tracker = new CadenceTracker();
        Feed(tracker, Signal60, 20, i => i % 2 == 0); // establish 30 fps
        var results = new List<CadenceResult>();
        for (var i = 20; i < 220; i++)
        {
            var metric = i % 2 == 0 ? 0.006 : 0.0015;
            results.Add(tracker.Process(Signal60.TickTimeNs(i), metric, isPq: false));
        }

        Assert.All(results.Where((_, k) => k % 2 == 0), r => Assert.True(r.IsUnique && r.Ambiguous));
        Assert.All(results.Where((_, k) => k % 2 == 1), r => Assert.False(r.IsUnique));

        // In the band but not yet due (one signal period after the last unique frame at 218): a repeat.
        var early = tracker.Process(Signal60.TickTimeNs(219) + 1000, 0.006, isPq: false);
        Assert.False(early.IsUnique);
        Assert.True(early.Ambiguous);
    }

    [Fact]
    public void Above_high_is_always_new_even_when_not_due()
    {
        var tracker = new CadenceTracker();
        Feed(tracker, Signal60, 20, i => i % 2 == 0);
        var r = tracker.Process(Signal60.TickTimeNs(19) + 1000, 0.5, isPq: false);
        Assert.True(r.IsUnique);
        Assert.False(r.Ambiguous);
    }

    [Fact]
    public void Sdr_and_pq_use_separate_thresholds()
    {
        var tracker = new CadenceTracker(new CadenceOptions
        {
            Sdr = new CadenceThresholds(0.002, 0.010),
            Pq = new CadenceThresholds(0.020, 0.040),
        });
        Feed(tracker, Signal60, 20, i => i % 2 == 0);

        // 0.012 is a clear change in SDR code values but below the PQ low threshold.
        Assert.True(tracker.Process(Signal60.TickTimeNs(21), 0.012, isPq: false).IsUnique);
        Assert.False(tracker.Process(Signal60.TickTimeNs(22), 0.012, isPq: true).IsUnique);
    }

    [Fact]
    public void Holds_do_not_pollute_the_frame_rate_estimate()
    {
        var tracker = new CadenceTracker();
        Feed(tracker, Signal60, 600, i => i % 2 == 0 && (i < 200 || i > 500)); // 5 s static menu in the middle

        Assert.InRange(tracker.EstimatedFrameRateHz, 29.99, 30.01);
    }

    [Fact]
    public void Reported_rate_has_hysteresis()
    {
        var tracker = new CadenceTracker(new CadenceOptions { RateWindow = 8, RateHysteresis = 0.05 });
        Feed(tracker, Signal60, 100, i => i % 2 == 0);
        var before = tracker.EstimatedIntervalNs;

        // A single 3-period interval moves the 8-sample mean by about 6%: reported once it exceeds 5%.
        var r = tracker.Process(Signal60.TickTimeNs(101), 0.05, false);
        Assert.True(r.IsUnique);
        Assert.NotEqual(before, tracker.EstimatedIntervalNs);

        tracker.Reset();
        Assert.Equal(0, tracker.EstimatedIntervalNs);
        Assert.Equal(0, tracker.EstimatedFrameRateHz);
    }

    [Fact]
    public void Options_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CadenceTracker(new CadenceOptions { Sdr = new CadenceThresholds(0.02, 0.01) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CadenceTracker(new CadenceOptions { RateWindow = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CadenceTracker(new CadenceOptions { MaxIntervalNs = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CadenceTracker(new CadenceOptions { ReseedAfterConsecutiveHolds = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CadenceTracker(new CadenceOptions { RateWindow = 4, ReseedAfterConsecutiveHolds = 5 }));
    }

    // ---- Review regressions: the rate estimate must recover whatever the start phase or rate change (finding 2) ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)] // capture starts on the last repeat of a picture: first interval 8.33 ms, old estimate stuck at 120 fps
    [InlineData(2)] // capture starts on the middle repeat: first interval 16.67 ms, old estimate left at 40.56 fps
    public void Forty_in_120_is_detected_from_every_start_phase(int phase)
    {
        var tracker = new CadenceTracker();
        var unique = Feed(tracker, Signal120, 1200, i => i % 3 == phase);

        // Capture 0 always counts (nothing to compare against); after it, exactly the new pictures.
        var expected = Enumerable.Range(0, 1200).Where(i => i == 0 || i % 3 == phase).Select(i => Signal120.TickTimeNs(i));
        Assert.Equal(expected, unique);
        Assert.InRange(tracker.EstimatedFrameRateHz, 39.999, 40.001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Thirty_in_60_is_detected_from_either_start_phase(int phase)
    {
        var tracker = new CadenceTracker();
        Feed(tracker, Signal60, 600, i => i % 2 == phase);
        Assert.InRange(tracker.EstimatedFrameRateHz, 29.999, 30.001);
    }

    [Theory]
    [InlineData(120, 1, 3)] // 120 fps menu into 40 fps gameplay: intervals 3x the estimate, beyond the hold factor
    [InlineData(120, 1, 4)] // 120 -> 30
    [InlineData(60, 1, 3)] // 60 -> 20
    [InlineData(120, 1, 2)] // 120 -> 60, inside the hold factor
    [InlineData(120, 3, 1)] // 40 -> 120, a rise
    [InlineData(60, 3, 2)] // 20 -> 30
    public void Rate_changes_are_followed_and_exact_within_one_window(long signalHz, int periodsBefore, int periodsAfter)
    {
        var signal = FrameRate.FromHz(signalHz);
        var tracker = new CadenceTracker();
        var window = tracker.Options.RateWindow;
        var change = (int)(5 * signalHz);
        var end = (int)(50 * signalHz);
        Feed(tracker, signal, change, i => i % periodsBefore == 0);
        Assert.InRange(tracker.EstimatedFrameRateHz, (signalHz / (double)periodsBefore) - 0.001, (signalHz / (double)periodsBefore) + 0.001);

        var expected = signalHz / (double)periodsAfter;
        int lastOutsideBand = change, lastInexact = change;
        for (var i = change; i < end; i++)
        {
            tracker.Process(signal.TickTimeNs(i), i % periodsAfter == 0 ? 0.05 : 0.0, isPq: false);
            var error = Math.Abs(tracker.EstimatedFrameRateHz - expected) / expected;
            lastOutsideBand = error > 0.025 ? i : lastOutsideBand;
            lastInexact = error > 0.0005 ? i : lastInexact;
        }

        // Exact once a window of new intervals is in (the report snaps to the cycle mean of a steady window).
        var inBandAfter = (lastOutsideBand - change) / (double)periodsAfter;
        var exactAfter = (lastInexact - change) / (double)periodsAfter;
        Assert.True(inBandAfter <= window + 2, $"estimate outside 2.5% of {expected} fps until {inBandAfter} intervals after the change");
        Assert.True(exactAfter <= window + 2, $"estimate not exact until {exactAfter} intervals after the change");
        Assert.InRange(tracker.EstimatedFrameRateHz, expected - 0.001, expected + 0.001);
    }

    [Fact]
    public void Disagreeing_long_gaps_in_a_row_are_holds_not_a_new_rate()
    {
        var tracker = new CadenceTracker();
        var stalls = new HashSet<int>();
        for (var i = 301; i < 305; i++)
        {
            stalls.Add(i); // gap of 5 periods (83 ms) ending at 305
        }

        for (var i = 306; i < 314; i++)
        {
            stalls.Add(i); // gap of 9 periods (150 ms)
        }

        for (var i = 315; i < 318; i++)
        {
            stalls.Add(i); // gap of 4 periods (67 ms)
        }

        var seen = new List<double>();
        for (var i = 0; i < 400; i++)
        {
            tracker.Process(Signal60.TickTimeNs(i), stalls.Contains(i) ? 0.0 : 0.05, isPq: false);
            seen.Add(tracker.EstimatedFrameRateHz);
        }

        Assert.All(seen.Skip(10), hz => Assert.InRange(hz, 58, 60.001));
        Assert.InRange(tracker.EstimatedFrameRateHz, 59.999, 60.001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void In_band_repeats_stay_repeats_from_every_start_phase(int phase)
    {
        // 40 fps in 120 Hz, then repeats whose metric sits inside the hysteresis band (dither, chroma noise). With a
        // stuck 120 fps estimate every one of them was "due" and counted as new.
        var tracker = new CadenceTracker();
        Feed(tracker, Signal120, 600, i => i % 3 == phase);

        int wrong = 0, missed = 0;
        for (var i = 600; i < 1200; i++)
        {
            var isNew = i % 3 == phase;
            var r = tracker.Process(Signal120.TickTimeNs(i), isNew ? 0.05 : 0.006, isPq: false);
            wrong += !isNew && r.IsUnique ? 1 : 0;
            missed += isNew && !r.IsUnique ? 1 : 0;
        }

        Assert.True(wrong == 0, $"{wrong} of 400 in-band repeats counted as new; estimate {tracker.EstimatedFrameRateHz:F2} fps");
        Assert.Equal(0, missed);
        Assert.InRange(tracker.EstimatedFrameRateHz, 39.999, 40.001);
    }

    /// <summary>Feeds whole cycles of a cadence given as gaps in signal periods; each gap starts with a clear new
    /// picture followed by repeats with <paramref name="repeatMetric"/>.</summary>
    private static void FeedGaps(CadenceTracker tracker, FrameRate signal, ref long capture, int[] gaps, int cycles, double repeatMetric = 0.0)
    {
        for (var c = 0; c < cycles; c++)
        {
            foreach (var g in gaps)
            {
                for (var k = 0; k < g; k++)
                {
                    tracker.Process(signal.TickTimeNs(capture++), k == 0 ? 0.05 : repeatMetric, isPq: false);
                }
            }
        }
    }

    // ---- Review round 2 (N3): no steady cadence can keep the estimate stuck ----

    [Fact]
    public void No_steady_cadence_under_the_cap_keeps_the_estimate_stuck()
    {
        // Every two-gap cycle a/b and three-gap cycle a,a,b with gaps of 2 to 8 signal periods, at 60 and 120 Hz,
        // arriving fresh, from the fastest cadence (every capture new) and from the slowest (8 periods). The estimate
        // must reach the cadence's true mean rate within two rate windows of intervals and stay there (the worst case
        // measured is 44 intervals: 60 Hz, from every capture new, gaps 2,3).
        var boundIntervals = 2 * new CadenceOptions().RateWindow;
        var failures = new List<string>();
        var worst = 0;
        var worstCase = "";
        var patterns = new List<int[]>();
        for (var a = 2; a <= 8; a++)
        {
            for (var b = 2; b <= 8; b++)
            {
                patterns.Add([a, b]);
                patterns.Add([a, a, b]);
            }
        }

        foreach (var hz in new long[] { 60, 120 })
        {
            var signal = FrameRate.FromHz(hz);
            foreach (var before in new[] { 0, 1, 8 })
            {
                foreach (var gaps in patterns)
                {
                    var tracker = new CadenceTracker();
                    long capture = 0;
                    if (before > 0)
                    {
                        FeedGaps(tracker, signal, ref capture, [before], (int)(5 * hz / before));
                    }

                    var expected = hz * gaps.Length / (double)gaps.Sum();
                    var cycles = (int)(20 * hz / gaps.Sum());
                    int lastOff = -1, intervals = 0;
                    for (var c = 0; c < cycles; c++)
                    {
                        FeedGaps(tracker, signal, ref capture, gaps, 1);
                        intervals += gaps.Length;
                        if (Math.Abs(tracker.EstimatedFrameRateHz - expected) > 0.005 * expected)
                        {
                            lastOff = intervals;
                        }
                    }

                    var name = $"{hz} Hz, from {(before == 0 ? "fresh" : before + " periods")}, gaps {string.Join(",", gaps)}";
                    if (lastOff > worst)
                    {
                        worst = lastOff;
                        worstCase = name;
                    }

                    if (lastOff > boundIntervals || Math.Abs(tracker.EstimatedFrameRateHz - expected) > 0.0001 * expected)
                    {
                        failures.Add($"{name}: expected {expected:F3} fps, got {tracker.EstimatedFrameRateHz:F3}, last off by >0.5% after {lastOff} intervals");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} cadences stuck or slow (worst {worst} intervals: {worstCase}): {string.Join("; ", failures.Take(12))}");
        Assert.True(worst > 0, "the grid never changed the estimate");
    }

    // ---- Review round 2 (N4): in-band repeats must not bias the estimate ----

    [Theory]
    [InlineData(new[] { 1, 2 }, 40.0)]
    [InlineData(new[] { 1, 2, 2 }, 36.0)]
    [InlineData(new[] { 3, 2 }, 24.0)]
    [InlineData(new[] { 1, 1, 1, 2 }, 48.0)]
    public void In_band_repeats_do_not_bias_the_estimate_on_uneven_cadences(int[] gaps, double fps)
    {
        // Clear new pictures; repeats carry an in-band metric (dither, chroma noise). In-band repeats that come due split
        // the long gaps, so counting only the clear intervals around them read 60, 60, 30 and 60 fps.
        var tracker = new CadenceTracker();
        long capture = 0;
        FeedGaps(tracker, Signal60, ref capture, gaps, 600 / gaps.Sum());
        Assert.InRange(tracker.EstimatedFrameRateHz, fps - 0.01, fps + 0.01);
        var learnt = tracker.EstimatedIntervalNs;

        var lowest = double.MaxValue;
        var highest = 0.0;
        for (var c = 0; c < 3000 / gaps.Sum(); c++)
        {
            FeedGaps(tracker, Signal60, ref capture, gaps, 1, repeatMetric: 0.006);
            lowest = Math.Min(lowest, tracker.EstimatedFrameRateHz);
            highest = Math.Max(highest, tracker.EstimatedFrameRateHz);
        }

        Assert.True(tracker.EstimatedIntervalNs == learnt, $"estimate moved from {fps} to {lowest:F2}..{highest:F2} fps");
    }

    [Fact]
    public void Clear_intervals_feed_the_estimate_again_once_in_band_frames_stop()
    {
        var tracker = new CadenceTracker();
        long capture = 0;
        FeedGaps(tracker, Signal60, ref capture, [1, 2], 200);
        FeedGaps(tracker, Signal60, ref capture, [1, 2], 600, repeatMetric: 0.006); // noisy repeats: estimate held
        Assert.InRange(tracker.EstimatedFrameRateHz, 39.99, 40.01);

        FeedGaps(tracker, Signal60, ref capture, [2], 60); // repeats exact again, and the game now runs at 30 fps
        Assert.InRange(tracker.EstimatedFrameRateHz, 29.99, 30.01);
    }

    [Fact]
    public void Waiting_intervals_are_fed_once_a_clear_run_follows_the_last_in_band_frame()
    {
        // 40 fps with in-band repeats, ending on an accepted in-band repeat; then exact repeats at 30 fps. The intervals
        // between the next clear pictures wait until eight clear pictures in a row have arrived, then all feed at once.
        var tracker = new CadenceTracker();
        long capture = 0;
        FeedGaps(tracker, Signal60, ref capture, [1, 2], 200);
        FeedGaps(tracker, Signal60, ref capture, [1, 2], 100, repeatMetric: 0.006);
        var learnt = tracker.EstimatedIntervalNs;
        var fedBefore = tracker.Counters.WaitingIntervalsFed;

        FeedGaps(tracker, Signal60, ref capture, [2], 7); // clear pictures 1 to 7: six intervals waiting
        Assert.Equal(learnt, tracker.EstimatedIntervalNs);

        FeedGaps(tracker, Signal60, ref capture, [2], 1); // the eighth: seven 33 ms intervals feed together
        Assert.Equal(7, tracker.Counters.WaitingIntervalsFed - fedBefore);
        Assert.True(tracker.EstimatedIntervalNs > learnt, $"estimate {tracker.EstimatedIntervalNs} ns did not take the waiting 33 ms intervals (was {learnt} ns)");
    }

    // ---- Review round 2 (N5): uneven cadences reached from another rate report their exact rate ----

    [Theory]
    [InlineData(new[] { 3, 2 }, 24.0)]
    [InlineData(new[] { 2, 3 }, 24.0)]
    [InlineData(new[] { 1, 1, 1, 1, 2 }, 50.0)]
    [InlineData(new[] { 1, 2, 2 }, 36.0)]
    public void Uneven_cadence_reached_from_60_fps_reports_the_exact_rate(int[] gaps, double fps)
    {
        // A 32-interval window is not a whole number of 3:2 or 1,1,1,1,2 cycles; the report read 24.30 and 50.53.
        var tracker = new CadenceTracker();
        long capture = 0;
        FeedGaps(tracker, Signal60, ref capture, [1], 300);
        FeedGaps(tracker, Signal60, ref capture, gaps, 1200 / gaps.Sum());
        Assert.InRange(tracker.EstimatedFrameRateHz, fps - 0.001, fps + 0.001);
    }

    [Fact]
    public void Long_cycle_cadences_report_within_the_range_of_their_own_window_means()
    {
        // Cadences whose cycle is too long to see whole in a 32-interval window (f new pictures spread evenly over p
        // signal periods, f from 17 to 31, e.g. 57 fps in 60 Hz is 19 in 20), reached from 1 to 5 periods per picture.
        // Without the settle rule, slow ones (about 2.5 to 4 periods per picture) kept a transitional value 0.2-1 % off.
        // The window mean ripples by one long-versus-short interval; the report must end within the range the window
        // mean itself covered over the last window, never on a value left over from the transition.
        var window = new CadenceOptions().RateWindow;
        var failures = new List<string>();
        var cases = 0;
        foreach (var hz in new long[] { 60, 120 })
        {
            var signal = FrameRate.FromHz(hz);
            foreach (var f in new[] { 17, 19, 23, 29, 31 })
            {
                foreach (var p in new[] { f + 1, f + 2, (3 * f / 2) + 1, (2 * f) - 1, (2 * f) + 1, (5 * f / 2) + 1, (5 * f / 2) + 2, (3 * f) - 1, (3 * f) + 1, (3 * f) + 2, (7 * f / 2) + 1, (4 * f) - 1 })
                {
                    var gaps = Enumerable.Range(0, f).Select(i => ((i + 1) * p / f) - (i * p / f)).ToArray();
                    foreach (var before in new[] { 1, 2, 3, 4, 5 })
                    {
                        cases++;
                        var tracker = new CadenceTracker();
                        long capture = 0;
                        FeedGaps(tracker, signal, ref capture, [before], (int)(3 * hz / before));
                        var intervals = new List<long>();
                        long last = -1;
                        for (var c = 0; c < 8; c++)
                        {
                            foreach (var g in gaps)
                            {
                                var t = signal.TickTimeNs(capture);
                                tracker.Process(t, 0.05, isPq: false);
                                if (last >= 0)
                                {
                                    intervals.Add(t - last);
                                }

                                last = t;
                                for (var k = 1; k <= g; k++)
                                {
                                    if (k < g)
                                    {
                                        tracker.Process(signal.TickTimeNs(capture + k), 0.0, isPq: false);
                                    }
                                }

                                capture += g;
                            }
                        }

                        var means = Enumerable.Range(intervals.Count - window, window)
                            .Select(end => intervals.Skip(end - window + 1).Take(window).Average())
                            .ToList();
                        if (tracker.EstimatedIntervalNs < means.Min() - 1 || tracker.EstimatedIntervalNs > means.Max() + 1)
                        {
                            failures.Add($"{hz} Hz, {f} in {p} from {before}: report {tracker.EstimatedIntervalNs} ns outside {means.Min():F0}..{means.Max():F0}");
                        }
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {cases}: {string.Join("; ", failures.Take(10))}");
    }

    // ---- Review round 2 (N6): one test per rule ----

    [Fact]
    public void Interval_from_the_first_capture_does_not_feed_the_estimate()
    {
        // 40 fps in 120 Hz, capture starting on the last repeat of a picture: the first "interval" is one period.
        var tracker = new CadenceTracker();
        for (var i = 0; i <= 7; i++)
        {
            tracker.Process(Signal120.TickTimeNs(i), i % 3 == 1 ? 0.05 : 0.0, isPq: false);
        }

        // Unique at 0 (first capture), 1, 4, 7: only 1 -> 4 and 4 -> 7 count.
        Assert.Equal(25_000_000, tracker.EstimatedIntervalNs);
    }

    [Fact]
    public void First_intervals_seed_the_estimate_together()
    {
        // A stray change one period after a new picture, then gaps of 3 and 5 periods. Seeded together, the three
        // intervals give their mean at once; judged against the first alone, the next two were holds and the
        // estimate stayed at 120 fps.
        var tracker = new CadenceTracker();
        foreach (var i in new[] { 0, 1, 2, 5, 10 })
        {
            tracker.Process(Signal120.TickTimeNs(i), 0.05, isPq: false);
        }

        // Intervals 1, 3 and 5 periods (1 -> 2, 2 -> 5, 5 -> 10): mean 3 periods, 25 ms.
        Assert.InRange(tracker.EstimatedIntervalNs, 24_999_999, 25_000_001);
    }

    [Fact]
    public void Recurring_pauses_longer_than_the_cap_never_become_a_rate()
    {
        // 30 fps, then a static screen whose clock ticks once a second: one new picture per second, many times. The
        // gaps agree with each other, but they are pauses, not a frame rate.
        var tracker = new CadenceTracker();
        long capture = 0;
        FeedGaps(tracker, Signal60, ref capture, [2], 150);
        FeedGaps(tracker, Signal60, ref capture, [60], 12);
        Assert.InRange(tracker.EstimatedFrameRateHz, 29.99, 30.01);
    }

    [Fact]
    public void Rate_drop_beyond_the_hold_factor_reseeds_after_three_agreeing_gaps()
    {
        var tracker = new CadenceTracker();
        long capture = 0;
        FeedGaps(tracker, Signal120, ref capture, [1], 600);
        FeedGaps(tracker, Signal120, ref capture, [3], 3);
        tracker.Process(Signal120.TickTimeNs(capture), 0.05, isPq: false); // ends the third 25 ms gap
        Assert.InRange(tracker.EstimatedFrameRateHz, 39.999, 40.001);
    }

    // ---- Review regressions: in-band new frames on uneven cadences (finding 3) ----

    [Theory]
    [InlineData(5, new[] { 0, 1, 3 }, 36.0)] // intervals 1, 2, 2 signal periods
    [InlineData(3, new[] { 0, 1 }, 40.0)] // 1, 2
    [InlineData(4, new[] { 0, 1, 2 }, 45.0)] // 1, 1, 2
    [InlineData(5, new[] { 0, 3 }, 24.0)] // 3:2 pulldown: 3, 2
    [InlineData(2, new[] { 0 }, 30.0)] // 2
    public void Slow_pan_keeps_every_in_band_new_frame_on_uneven_cadences(int cycle, int[] newAt, double fps)
    {
        // New pictures differ only slightly (between Low and High); repeats are exact. In a 60 Hz signal.
        bool IsNew(int i) => Array.IndexOf(newAt, i % cycle) >= 0;
        var tracker = new CadenceTracker();
        Feed(tracker, Signal60, 600, IsNew);
        var before = tracker.EstimatedIntervalNs;
        Assert.InRange(tracker.EstimatedFrameRateHz, fps * 0.985, fps * 1.015);

        int newTotal = 0, missed = 0, repeatsWrong = 0;
        for (var i = 600; i < 3600; i++)
        {
            var r = tracker.Process(Signal60.TickTimeNs(i), IsNew(i) ? 0.006 : 0.0, isPq: false);
            if (IsNew(i))
            {
                newTotal++;
                Assert.True(r.Ambiguous);
                missed += r.IsUnique ? 0 : 1;
            }
            else
            {
                repeatsWrong += r.IsUnique ? 1 : 0;
            }
        }

        Assert.True(missed == 0, $"{missed} of {newTotal} in-band new frames dropped as repeats; estimate {tracker.EstimatedFrameRateHz:F2} fps");
        Assert.Equal(0, repeatsWrong);

        // In-band decisions never feed the estimate, so a wrong one cannot drag it (and the due rule) along.
        Assert.Equal(before, tracker.EstimatedIntervalNs);
    }

    [Fact]
    public void Forty_in_60_slow_pan_with_timestamp_jitter_keeps_new_frames_and_repeats_apart()
    {
        // New pictures at captures 0, 2, 3, 5, 6, 8 ... (intervals 2, 1 periods), USB-style timestamp jitter.
        static bool IsNew(int i) => i % 3 != 1;
        var tracker = new CadenceTracker();
        var random = new DeterministicRandom(7);
        for (var i = 0; i < 300; i++)
        {
            tracker.Process(Signal60.TickTimeNs(i) + random.NextSymmetric(20_000), IsNew(i) ? 0.05 : 0.0, false);
        }

        int missed = 0, repeatsWrong = 0;
        for (var i = 300; i < 3300; i++)
        {
            var r = tracker.Process(Signal60.TickTimeNs(i) + random.NextSymmetric(20_000), IsNew(i) ? 0.006 : 0.0, false);
            if (IsNew(i))
            {
                missed += r.IsUnique ? 0 : 1;
            }
            else
            {
                repeatsWrong += r.IsUnique ? 1 : 0;
            }
        }

        Assert.Equal(0, missed);
        Assert.Equal(0, repeatsWrong);
        Assert.InRange(tracker.EstimatedFrameRateHz, 39.5, 40.5);
    }

    [Fact]
    public void In_band_frame_is_due_after_the_shortest_recent_interval_less_half_a_period()
    {
        var tracker = new CadenceTracker();
        Feed(tracker, Signal60, 600, i => i % 5 is 0 or 3); // 24 fps: intervals of 3 and 2 periods
        Assert.InRange(tracker.NewFrameDueAfterNs, 24_999_000, 25_001_000); // 2 periods less half a period
    }

    // ---- Review regressions: hysteresis must not leave a permanent error (finding 6) ----

    [Fact]
    public void Reported_rate_settles_on_the_mean_after_a_change_inside_the_hysteresis_band()
    {
        // 120 -> 60 moves the mean in small steps; hysteresis alone stopped the report at 60.95 fps.
        var tracker = new CadenceTracker();
        Feed(tracker, Signal120, 600, _ => true);
        for (var i = 600; i < 6000; i++)
        {
            tracker.Process(Signal120.TickTimeNs(i), i % 2 == 0 ? 0.05 : 0.0, isPq: false);
        }

        Assert.InRange(tracker.EstimatedFrameRateHz, 59.999, 60.001);
    }

    [Fact]
    public void Reported_rate_does_not_flicker_on_an_uneven_cadence()
    {
        // 36 fps in 60 Hz (1, 2, 2 periods): a 32-interval window holds 10 or 11 short intervals, so its mean
        // alternates by 1.9%. The report must stay put instead of following that ripple.
        var tracker = new CadenceTracker();
        Feed(tracker, Signal60, 600, i => i % 5 is 0 or 1 or 3);
        var reported = tracker.EstimatedIntervalNs;
        var changes = 0;
        for (var i = 600; i < 6000; i++)
        {
            tracker.Process(Signal60.TickTimeNs(i), i % 5 is 0 or 1 or 3 ? 0.05 : 0.0, isPq: false);
            if (tracker.EstimatedIntervalNs != reported)
            {
                changes++;
                reported = tracker.EstimatedIntervalNs;
            }
        }

        Assert.InRange(changes, 0, 1);
        Assert.InRange(tracker.EstimatedFrameRateHz, 35.999, 36.001);
    }
}
