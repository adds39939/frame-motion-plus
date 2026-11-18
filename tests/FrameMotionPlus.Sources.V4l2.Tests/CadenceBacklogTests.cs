using FrameMotionPlus.Core.Cadence;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// The "Cadence tracker backlog" of docs/implementation-plan.md (Phase 5), accepted as non-blocking in Phase 1 and to
/// become acceptance cases once the real metric and thresholds existed. Each runs Core's tracker with the capture
/// thresholds (<see cref="CaptureSourceOptions.CaptureCadence"/>) and metric values as the kernel reports them on the
/// card: clear new pictures well above High, exact-ish repeats below Low, in-band frames between the two. Where the
/// behaviour is the backlog's known limit rather than the wanted one, the test pins the limit (its name says so) and
/// docs/benchmarks/phase5-capture.md lists it under "Known issues".
/// </summary>
public class CadenceBacklogTests
{
    private static readonly FrameRate Signal60 = FrameRate.FromHz(60);
    private static readonly CadenceThresholds Pq = CaptureSourceOptions.CaptureCadence.Pq;
    private static readonly double Clear = 0.012;   // a typical new picture (P010 at 60: p50 0.0122)
    private static readonly double Repeat = 0.00015; // a typical true duplicate (P010 1440p60: p50 0.00014, max 0.00021)
    private static readonly double InBand = (Pq.Low + Pq.High) / 2;

    private static CadenceTracker Tracker() => new(CaptureSourceOptions.CaptureCadence);

    /// <summary>Feeds captures of a 60 Hz signal; <paramref name="metric"/> gives each one's metric.</summary>
    private static void Feed(CadenceTracker t, ref int capture, int count, Func<int, double> metric)
    {
        for (var i = 0; i < count; i++, capture++)
        {
            t.Process(Signal60.TickTimeNs(capture), metric(capture), isPq: true);
        }
    }

    /// <summary>Backlog 1a: 60 fps, then 30 fps with every 8th new picture in-band. Wanted: 30. Known limit: in-band frames
    /// at least once per 8 unique frames keep their neighbours' intervals out of the estimate, so it never leaves 60.</summary>
    [Fact]
    public void Known_limit_1a_in_band_frames_every_8th_picture_hold_the_estimate_after_a_drop_to_30()
    {
        var t = Tracker();
        var capture = 0;
        Feed(t, ref capture, 300, _ => Clear);
        Assert.InRange(t.EstimatedFrameRateHz, 59.99, 60.01);
        var picture = 0;
        Feed(t, ref capture, 1200, c => c % 2 == 0 ? (++picture % 8 == 0 ? InBand : Clear) : Repeat);
        Assert.True(t.EstimatedFrameRateHz > 31, $"the limit no longer holds: {t.EstimatedFrameRateHz:F2} fps (update the test and the known issues)");
    }

    /// <summary>Backlog 1b: a learned 60 fps followed by 30 fps whose repeats are in-band (dither on the repeats): wanted,
    /// the repeats are repeats and the estimate goes to 30. Known limit: due at 60 fps, every in-band repeat is taken as new.</summary>
    [Fact]
    public void Known_limit_1b_in_band_repeats_after_a_learned_60_are_taken_as_new()
    {
        var t = Tracker();
        var capture = 0;
        Feed(t, ref capture, 300, _ => Clear);
        var wrong = 0;
        for (var i = 0; i < 600; i++, capture++)
        {
            var r = t.Process(Signal60.TickTimeNs(capture), capture % 2 == 0 ? Clear : InBand, isPq: true);
            wrong += capture % 2 == 1 && r.IsUnique ? 1 : 0;
        }

        Assert.True(wrong > 250, $"the limit no longer holds: {wrong} in-band repeats taken as new (update the test and the known issues)");
    }

    /// <summary>Backlog 1c: a source that starts with in-band repeats. Measured duplicates sit below Low (P010 1440p60: at
    /// most 0.00021 against Low 0.0004), so on this card a 30-in-60 source learns its rate: passes with the capture
    /// thresholds.</summary>
    [Fact]
    public void A_source_whose_repeats_are_the_cards_noise_learns_30_in_60_from_the_start()
    {
        var t = Tracker();
        var capture = 0;
        Feed(t, ref capture, 600, c => c % 2 == 0 ? Clear : Repeat);
        Assert.InRange(t.EstimatedFrameRateHz, 29.99, 30.01);
    }

    /// <summary>Backlog 1c as written (repeats in-band from the first capture): known limit, no rate is ever learned.</summary>
    [Fact]
    public void Known_limit_1c_a_source_starting_with_in_band_repeats_learns_no_30()
    {
        var t = Tracker();
        var capture = 0;
        Feed(t, ref capture, 600, c => c % 2 == 0 ? Clear : InBand);
        Assert.True(Math.Abs(t.EstimatedFrameRateHz - 30) > 1, $"the limit no longer holds: {t.EstimatedFrameRateHz:F2} fps (update the test and the known issues)");
    }

    /// <summary>Backlog 2: 60 fps with an 83 ms hitch every 4th frame: the estimate saw-tooths. Pinned: it stays within the
    /// range the backlog measured (30 to 48 fps) rather than settling; a fix would make it settle.</summary>
    [Fact]
    public void Known_limit_2_a_long_gap_every_4th_frame_makes_the_estimate_saw_tooth()
    {
        var t = Tracker();
        var capture = 0;
        var seen = new List<double>();
        for (var cycle = 0; cycle < 200; cycle++)
        {
            // Three frames one period apart, then a gap of five periods (83 ms).
            foreach (var gap in new[] { 1, 1, 1, 5 })
            {
                t.Process(Signal60.TickTimeNs(capture), Clear, isPq: true);
                for (var k = 1; k < gap; k++)
                {
                    t.Process(Signal60.TickTimeNs(capture + k), Repeat, isPq: true);
                }

                capture += gap;
                seen.Add(t.EstimatedFrameRateHz);
            }
        }

        var tail = seen.Skip(seen.Count / 2).ToList();
        Assert.True(tail.Max() - tail.Min() > 5, $"the limit no longer holds: the estimate stays within {tail.Min():F1}..{tail.Max():F1} fps (update the test and the known issues)");
    }

    /// <summary>Backlog 3: long gaps under a quarter of the intervals are hitches, not cadence: gaps 1,1,1,1,3 read 60
    /// against a true mean of 42.86. Kept as the meaning of "game frame rate" here (the rate between hitches), as the
    /// scheduler's holds treat those gaps; pinned.</summary>
    [Fact]
    public void Decided_3_long_gaps_under_a_quarter_are_hitches_and_the_rate_is_the_one_between_them()
    {
        var t = Tracker();
        var capture = 0;
        for (var cycle = 0; cycle < 200; cycle++)
        {
            foreach (var gap in new[] { 1, 1, 1, 1, 3 })
            {
                t.Process(Signal60.TickTimeNs(capture), Clear, isPq: true);
                for (var k = 1; k < gap; k++)
                {
                    t.Process(Signal60.TickTimeNs(capture + k), Repeat, isPq: true);
                }

                capture += gap;
            }
        }

        Assert.InRange(t.EstimatedFrameRateHz, 59.9, 60.1);
    }

    /// <summary>Backlog 4: an animated menu (irregular sub-200 ms gaps) can re-seed the estimate; it recovers within a rate
    /// window (32 intervals: 33 unique frames) after play resumes at a steady 30.</summary>
    [Fact]
    public void After_an_animated_menu_the_estimate_recovers_within_32_unique_frames()
    {
        var t = Tracker();
        var capture = 0;
        Feed(t, ref capture, 600, c => c % 2 == 0 ? Clear : Repeat);
        var random = new Random(3);
        for (var i = 0; i < 40; i++)
        {
            var gap = 2 + random.Next(10); // 33 to 183 ms, irregular
            t.Process(Signal60.TickTimeNs(capture), Clear, isPq: true);
            for (var k = 1; k < gap; k++)
            {
                t.Process(Signal60.TickTimeNs(capture + k), Repeat, isPq: true);
            }

            capture += gap;
        }

        capture += capture % 2;
        var uniqueAfter = 0;
        var settledAt = -1;
        for (var i = 0; i < 200; i++, capture++)
        {
            var r = t.Process(Signal60.TickTimeNs(capture), capture % 2 == 0 ? Clear : Repeat, isPq: true);
            uniqueAfter += r.IsUnique ? 1 : 0;
            if (settledAt < 0 && Math.Abs(t.EstimatedFrameRateHz - 30) < 0.01)
            {
                settledAt = uniqueAfter;
            }
        }

        Assert.InRange(settledAt, 0, 33);
        Assert.InRange(t.EstimatedFrameRateHz, 29.99, 30.01);
    }

    /// <summary>Backlog 5: a cadence whose cycle is longer than 16 frames (57 fps in 60 Hz) ripples by about 3%: pinned
    /// as a bound.</summary>
    [Fact]
    public void Known_limit_5_long_cycles_ripple_within_3_percent()
    {
        var t = Tracker();
        var capture = 0;
        var seen = new List<double>();
        for (var i = 0; i < 2400; i++, capture++)
        {
            var isNew = (i * 19 / 20) != ((i - 1) * 19 / 20) || i == 0; // 19 new pictures in every 20 captures
            t.Process(Signal60.TickTimeNs(capture), isNew ? Clear : Repeat, isPq: true);
            if (i > 600)
            {
                seen.Add(t.EstimatedFrameRateHz);
            }
        }

        Assert.All(seen, hz => Assert.InRange(hz, 57 * 0.97, 57 * 1.03));
    }

    /// <summary>Backlog 6: a cadence slower than the 200 ms cap (under 5 fps) reports 0, by design (pauses, menus).</summary>
    [Fact]
    public void Decided_6_a_cadence_under_5_fps_reports_no_rate()
    {
        var t = Tracker();
        var capture = 0;
        Feed(t, ref capture, 1200, c => c % 15 == 0 ? Clear : Repeat); // 4 fps
        Assert.Equal(0, t.EstimatedFrameRateHz);
    }
}
