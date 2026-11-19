using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// The Phase 5 review's 180 s 4K capture run (r4k.csv, slots 19299 to 19305): the compositor missed a refresh with a
/// repeat queued in time (19300 shown a refresh late), then gave the refresh back itself by discarding a queued present
/// (19301, reported without a first-pixel time), while the present thread also gave one back (it dropped the repeat
/// 19302): the FIFO went a refresh ahead and the next two slots were shown a refresh early. The live accounting called the
/// dropped repeat fine (its refresh "shows the image it repeats"), the verifier a wrong image: that refresh showed 19303.
/// Replayed here through the collector, record by record as the present thread of that revision emitted them, on a
/// stride-2 cadence (real, repeat, generated, repeat).
/// </summary>
public class CatchUpTests
{
    private const long P = 8_341_675;
    private const long T0 = 1_000_000_000_000;
    private const long FirstPixel = 160_000;

    private static long Target(long slot) => T0 + (slot * P);

    private static SlotKind KindOf(long slot) => (slot % 4) switch
    {
        0 => SlotKind.Real,
        2 => SlotKind.Generated,
        _ => SlotKind.Repeat,
    };

    private sealed class Replay
    {
        private long _lastShown;
        private long _lastTimed;
        private int _untimedSinceTimed;

        public TelemetryCollector Collector { get; } = new(1024, 0);

        public Replay(int slots)
        {
            Collector.ReserveVerifierRows(1024);
            for (long s = 0; s < slots; s++)
            {
                var kind = KindOf(s);
                Collector.Add(new DecisionRecord
                {
                    Decision = new SlotDecision { SlotIndex = s, Kind = kind, TargetTimeNs = Target(s), FrameA = s / 4, FrameB = s / 4, PipelineDelayNs = 45_000_000 },
                    Outcome = kind == SlotKind.Repeat ? DecisionOutcome.Repeat : DecisionOutcome.Written,
                    Write = kind == SlotKind.Repeat ? 0 : (ulong)s + 1,
                });
                if (kind != SlotKind.Repeat)
                {
                    Collector.Add(new WriteRecord { SlotIndex = s, Write = (ulong)s + 1, Outcome = kind == SlotKind.Real ? WriteOutcome.Copied : WriteOutcome.Generated });
                }
            }
        }

        public void Shown(long slot, long shownNs, bool isMiss = false, MissKind miss = MissKind.LatePresent)
        {
            Collector.Add(new PresentRecord
            {
                SlotIndex = slot,
                Kind = slot < 0 || KindOf(slot) == SlotKind.Repeat ? PresentKind.Repeat : PresentKind.Write,
                Outcome = slot < 0 ? PresentOutcome.Filler : PresentOutcome.Shown,
                Write = slot < 0 || KindOf(slot) == SlotKind.Repeat ? 0 : (ulong)slot + 1,
                TargetNs = slot < 0 ? shownNs - FirstPixel : Target(slot),
                SubmitNs = (slot < 0 ? shownNs : Target(slot)) - (14 * Nanoseconds.PerMillisecond),
                PredictedNs = shownNs - FirstPixel,
                ShownNs = shownNs,
                ReportedNs = shownNs + Nanoseconds.PerMillisecond,
                Timed = true,
                PreviousShownNs = _lastShown,
                PreviousTimedNs = _lastTimed,
                UntimedBefore = _untimedSinceTimed,
                PeriodNs = P,
                InFlight = 2,
                IsMiss = isMiss,
                Miss = miss,
            });
            _lastShown = shownNs;
            _lastTimed = shownNs;
            _untimedSinceTimed = 0;
        }

        public void Untimed(long slot, MissKind attribution)
        {
            Collector.Add(new PresentRecord
            {
                SlotIndex = slot,
                Kind = KindOf(slot) == SlotKind.Repeat ? PresentKind.Repeat : PresentKind.Write,
                Outcome = PresentOutcome.Shown,
                Write = (ulong)slot + 1,
                TargetNs = Target(slot),
                SubmitNs = Target(slot) - (14 * Nanoseconds.PerMillisecond),
                PredictedNs = Target(slot) + P,
                ShownNs = Target(slot) + (2 * P),
                ReportedNs = Target(slot) + (2 * P),
                Untimed = true,
                PeriodNs = P,
                Miss = attribution,
            });
            _lastShown = 0;
            _untimedSinceTimed++;
        }

        public void Dropped(long slot, PresentOutcome outcome, bool isMiss, MissKind miss) => Collector.Add(new PresentRecord
        {
            SlotIndex = slot,
            Kind = KindOf(slot) == SlotKind.Repeat ? PresentKind.Repeat : PresentKind.Write,
            Outcome = outcome,
            Write = KindOf(slot) == SlotKind.Repeat ? 0 : (ulong)slot + 1,
            TargetNs = Target(slot),
            SubmitNs = Target(slot) - P,
            PredictedNs = Target(slot) + P,
            PeriodNs = P,
            IsMiss = isMiss,
            Miss = miss,
        });

        public VerificationResult Verify()
        {
            Collector.Flush();
            _ = Collector.ExplainGaps(Collector.MeasureFromNs, Collector.Statistics.LastMeasuredNs, Collector.Statistics.LastMeasuredNs, out _);
            return TelemetryVerifier.Verify(Collector.VerifierRows, P, Collector.MeasureFromNs, Collector.Statistics.LastMeasuredNs);
        }
    }

    [Fact]
    public void The_reviews_cascade_a_dropped_repeat_whose_refresh_showed_another_image_is_a_miss_live_too()
    {
        var r = new Replay(20);
        for (long s = 0; s <= 8; s++)
        {
            r.Shown(s, Target(s) + FirstPixel);
        }

        // 9 (a repeat handed over in time) shown a refresh late: the compositor missed refresh 9. The present thread of that
        // revision dropped repeat 11 to give the refresh back; the compositor discarded 10 to give it back as well.
        r.Shown(9, Target(10) + FirstPixel, isMiss: true, MissKind.DisplayLate);
        r.Dropped(11, PresentOutcome.DroppedLate, isMiss: false, MissKind.CatchUp);
        r.Untimed(10, MissKind.CatchUp);
        r.Shown(12, Target(11) + FirstPixel, isMiss: true, MissKind.Early);
        r.Shown(13, Target(12) + FirstPixel, isMiss: true, MissKind.Early);
        r.Shown(-1, Target(13) + FirstPixel); // a filler for the refresh the early shows left free
        for (long s = 14; s < 20; s++)
        {
            r.Shown(s, Target(s) + FirstPixel);
        }

        var v = r.Verify();
        var stats = r.Collector.Statistics;
        Log.WriteLine($"verifier: {v.Misses} missed, {v.Disagreements} disagreements: {v.Describe()}; live: {stats.TotalMisses} ({string.Join(", ", stats.MissEvents.ToArray().Select(m => m.Kind))})");
        Assert.Contains(v.Slots, s => s.Slot == 11 && (s.Finding & VerifierFinding.WrongImage) != 0);
        Assert.Equal(5, v.Misses);
        Assert.Equal(0, v.Disagreements);
        Assert.Equal(5, stats.TotalMisses);

        // Whose: 9 the display's (late although in time), 10 and 11 the late show's give-back (the display's), 12 and 13 early
        // (the pipeline's, in that revision: its own catch-up on top of the compositor's).
        Assert.Equal(1, stats.MissCount(MissKind.DisplayLate));
        Assert.Equal(2, stats.MissCount(MissKind.CatchUp));
        Assert.Equal(2, stats.MissCount(MissKind.Early));
    }

    [Fact]
    public void A_repeat_given_up_after_a_real_frame_shown_a_refresh_late_shows_that_frame_and_is_no_miss()
    {
        // Real frame 4 a refresh late (shown at 5's refresh); its repeat 5 given up: refresh 5 shows 4, as scheduled.
        var r = new Replay(12);
        for (long s = 0; s <= 3; s++)
        {
            r.Shown(s, Target(s) + FirstPixel);
        }

        r.Shown(4, Target(5) + FirstPixel, isMiss: true, MissKind.DisplayLate);
        r.Dropped(5, PresentOutcome.DroppedAbsorbed, isMiss: false, MissKind.CatchUp);
        for (long s = 6; s < 12; s++)
        {
            r.Shown(s, Target(s) + FirstPixel);
        }

        var v = r.Verify();
        var stats = r.Collector.Statistics;
        Log.WriteLine($"verifier: {v.Misses} missed: {v.Describe()}; live {stats.TotalMisses}");
        Assert.Equal(0, v.Disagreements);
        Assert.Equal(1, v.Misses);
        Assert.Equal(1, stats.TotalMisses);
        Assert.DoesNotContain(v.Slots, s => s.Slot == 5);
    }

    [Fact]
    public void A_present_handed_over_in_time_that_the_compositor_discarded_is_the_displays()
    {
        // 6 (generated) reported without a time and its refresh taken by the filler shown there: discarded although in time.
        var r = new Replay(12);
        for (long s = 0; s <= 5; s++)
        {
            r.Shown(s, Target(s) + FirstPixel);
        }

        r.Untimed(6, MissKind.Discarded);
        r.Shown(-1, Target(6) + FirstPixel);
        for (long s = 7; s < 12; s++)
        {
            r.Shown(s, Target(s) + FirstPixel);
        }

        var v = r.Verify();
        var stats = r.Collector.Statistics;
        Log.WriteLine($"verifier: {v.Misses} missed: {v.Describe()}; live {stats.TotalMisses} ({stats.PipelineMisses} the pipeline's)");
        Assert.Equal(0, v.Disagreements);
        Assert.Equal(1, stats.MissCount(MissKind.Discarded));
        Assert.Equal(0, stats.PipelineMisses);
        Assert.Equal(1, stats.EnvironmentMisses);
    }
}
