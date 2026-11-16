using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Statistics;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Tests.Scheduling;

public class TimelineAndPhaseTests
{
    [Fact]
    public void Timeline_finds_the_bracketing_frame_from_any_hint()
    {
        var timeline = new UniqueFrameTimeline(8);
        for (var i = 0; i < 5; i++)
        {
            timeline.Add(new TimelineFrame(100 + i, i * 10, i * 10, TimelineMarks.None));
        }

        Assert.Equal(-1, timeline.FindAtOrBefore(-1, 0));
        Assert.Equal(0, timeline.FindAtOrBefore(0, 4));
        Assert.Equal(2, timeline.FindAtOrBefore(25, 0));
        Assert.Equal(2, timeline.FindAtOrBefore(25, 4));
        Assert.Equal(4, timeline.FindAtOrBefore(1000, 1));
        Assert.Equal(102, timeline[2].FrameId);
        Assert.Equal(104, timeline.Newest.FrameId);
    }

    [Fact]
    public void Timeline_overwrites_the_oldest_and_keeps_sequence_numbers()
    {
        var timeline = new UniqueFrameTimeline(4);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(i, timeline.Add(new TimelineFrame(i, i * 10, i * 10, TimelineMarks.None)));
        }

        Assert.Equal(6, timeline.FirstSequence);
        Assert.Equal(9, timeline.LastSequence);
        Assert.Equal(4, timeline.Count);
        Assert.False(timeline.Contains(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline[5]);
        Assert.Equal(-1, timeline.FindAtOrBefore(55, 6));

        timeline.Clear();
        Assert.True(timeline.IsEmpty);
        Assert.Equal(10, timeline.Add(new TimelineFrame(10, 100, 100, TimelineMarks.HoldBefore)));
        Assert.True(timeline[10].BreaksContinuity);
    }

    private static PhaseCoherenceEstimator Estimator(double periodNs, long anchor = 0)
    {
        var e = new PhaseCoherenceEstimator(window: 64, minFrames: 8, maxDriftPpm: 500, maxJitterNs: 500_000);
        e.SetGrid(anchor, periodNs);
        return e;
    }

    [Fact]
    public void Sixty_on_120_is_coherent_and_reports_the_source_phase()
    {
        var e = Estimator(FrameRate.FromHz(120).PeriodNs);
        var source = FrameRate.FromHz(60);
        for (var i = 0; i < 100; i++)
        {
            e.Add(3_000_000 + source.TickTimeNs(i));
        }

        Assert.True(e.IsCoherent);
        Assert.InRange(e.PhaseNs, 2_999_998, 3_000_002);
        Assert.InRange(Math.Abs(e.DriftPpm), 0, 1);
    }

    [Fact]
    public void Ntsc_59_94_on_120_is_not_coherent()
    {
        var e = Estimator(FrameRate.FromHz(120).PeriodNs);
        var source = new FrameRate(60000, 1001);
        for (var i = 0; i < 100; i++)
        {
            e.Add(source.TickTimeNs(i));
        }

        Assert.False(e.IsCoherent);
        Assert.InRange(e.DriftPpm, 990, 1010);
    }

    [Fact]
    public void Thirty_on_144_is_not_coherent()
    {
        var e = Estimator(FrameRate.FromHz(144).PeriodNs);
        for (var i = 0; i < 100; i++)
        {
            e.Add(FrameRate.FromHz(30).TickTimeNs(i));
        }

        Assert.False(e.IsCoherent);
        Assert.True(e.JitterNs > 1_000_000);
    }

    [Fact]
    public void Crystal_skew_is_coherent_with_measured_drift()
    {
        var e = Estimator(FrameRate.FromHz(120).PeriodNs);
        var source = FrameRate.FromHz(60);
        for (var i = 0; i < 100; i++)
        {
            e.Add((long)Math.Round(source.TickTimeNs(i) * (1 + 100e-6)));
        }

        Assert.True(e.IsCoherent);
        Assert.InRange(e.DriftPpm, 99, 101);
    }

    [Fact]
    public void Jittery_timestamps_are_not_coherent_but_small_capture_jitter_is()
    {
        var random = new DeterministicRandom(1);
        var noisy = Estimator(FrameRate.FromHz(120).PeriodNs);
        var clean = Estimator(FrameRate.FromHz(120).PeriodNs);
        for (var i = 0; i < 100; i++)
        {
            var ts = FrameRate.FromHz(60).TickTimeNs(i);
            noisy.Add(ts + random.NextSymmetric(3_000_000));
            clean.Add(ts + random.NextSymmetric(50_000));
        }

        Assert.False(noisy.IsCoherent);
        Assert.True(clean.IsCoherent);
    }

    [Fact]
    public void Needs_the_minimum_number_of_frames_and_resets()
    {
        var e = Estimator(FrameRate.FromHz(120).PeriodNs);
        for (var i = 0; i < 7; i++)
        {
            e.Add(FrameRate.FromHz(60).TickTimeNs(i));
            Assert.False(e.IsCoherent);
        }

        e.Add(FrameRate.FromHz(60).TickTimeNs(7));
        Assert.True(e.IsCoherent);
        e.Reset();
        Assert.False(e.IsCoherent);
        Assert.Equal(0, e.Count);
    }
}
