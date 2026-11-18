using FrameMotionPlus.Audio.Dsp;
using FrameMotionPlus.Audio.Tests.Support;

namespace FrameMotionPlus.Audio.Tests;

/// <summary>
/// The delay line between a simulated card and sink (<see cref="DelayLineRig"/>): the delay achieved, measured from clicks
/// in the output against their true capture times, equals the target (video latency plus trim) to within a frame; it
/// follows a change of target without a discontinuity; it holds under ±500 ppm of clock drift and jittery timestamps
/// with no dropout; underruns, overruns, capture gaps, stalls and mute are handled as documented.
/// </summary>
public class DelayLineTests
{
    private const long Ms = 1_000_000;
    private const long S = 1_000_000_000;
    private const int ClickEvery = 4_800; // 100 ms
    private const float ClickHeight = 0.5f;

    private static float Clicks(long frame, DelayLineRig rig) => frame % ClickEvery == 0 ? ClickHeight : 0f;

    private static float Sine(long frame, DelayLineRig rig) => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * frame / 48_000.0));

    private static (DelayLine Line, DelayLineRig Rig) Make(RigOptions rig, DelayLineOptions? line = null, long videoNs = 40 * Ms)
    {
        var l = new DelayLine(line ?? new DelayLineOptions { SettleNs = 2 * S });
        l.SetVideoLatency(videoNs);
        return (l, new DelayLineRig(l, rig with { ExpectedDelayNs = videoNs + (line?.TrimNs ?? 0) }));
    }

    private static List<double> RecordClicks(DelayLineRig rig, long fromNs, long toNs)
    {
        rig.Run(fromNs);
        rig.Output.Clear();
        rig.Record = true;
        rig.Run(toNs);
        rig.Record = false;
        return rig.ClickDelays(ClickEvery, ClickHeight);
    }

    [Fact]
    public void The_delay_achieved_equals_the_target_to_within_a_frame()
    {
        var (line, rig) = Make(new RigOptions { Content = Clicks });
        var delays = RecordClicks(rig, 10 * S, 20 * S);
        Assert.True(delays.Count >= 95, $"{delays.Count} clicks found");
        foreach (var d in delays)
        {
            Assert.True(Math.Abs(d - (40 * Ms)) < 21_000, $"click delay {d / 1e6:0.0000} ms against 40 ms");
        }

        var s = line.Statistics;
        Assert.Equal(0, s.Underruns + s.Overruns + s.Jumps + s.Resyncs);
        Assert.True(Math.Abs(s.ErrorMean) < 20_000, $"error mean {s.ErrorMean / 1e3:0.0} µs");
        Assert.True(Math.Abs(s.OffsetMean) < 20_000);
        Assert.Equal(40 * Ms, line.CurrentDelayNs, 30_000.0);
    }

    [Theory]
    [InlineData(12.0)]
    [InlineData(-8.0)]
    public void The_trim_moves_the_sound_later_or_earlier(double trimMs)
    {
        var trim = (long)(trimMs * Ms);
        var (line, rig) = Make(new RigOptions { Content = Clicks }, new DelayLineOptions { TrimNs = trim, SettleNs = 2 * S });
        var delays = RecordClicks(rig, 15 * S, 20 * S);
        Assert.NotEmpty(delays);
        Assert.All(delays, d => Assert.True(Math.Abs(d - (40 * Ms) - trim) < 21_000, $"click delay {d / 1e6:0.0000} ms"));
        Assert.Equal(trimMs, line.Statistics.OffsetMean / 1e6, 1);
    }

    [Fact]
    public void A_target_below_what_the_path_can_do_plays_as_early_as_it_can_and_says_by_how_much()
    {
        var (line, rig) = Make(new RigOptions { Content = Clicks }, videoNs: 5 * Ms);
        var delays = RecordClicks(rig, 10 * S, 15 * S);
        var s = line.Statistics;
        Assert.Equal(0, s.Underruns);
        Assert.True(s.ShortBlocks == s.MeasuredBlocks && s.MeasuredBlocks > 0);

        // Capture 8 + sink 5.33 + block and kernel 5.7 + margin 2: about 21 ms; the clicks come out there.
        Assert.InRange(s.MinimumMean / 1e6, 20.0, 23.5);
        Assert.All(delays, d => Assert.True(Math.Abs(d - s.MinimumMean) < 300_000, $"click delay {d / 1e6:0.000} ms, minimum {s.MinimumMean / 1e6:0.000}"));
        Assert.Equal(s.MinimumMean - (5 * Ms), s.ShortfallMean, 300_000.0);
    }

    [Fact]
    public void A_change_of_target_is_followed_without_a_discontinuity()
    {
        // A sine through a 6 ms change of video latency: no sample-to-sample step beyond the sine's own, no jump, and the
        // ratio inside its bound; then the delay, measured with clicks in a second run, is on the new target.
        var (line, rig) = Make(new RigOptions { Content = Sine });
        rig.BeforePlayback = r => r.Line.SetVideoLatency(r.NowNs < 10 * S ? 40 * Ms : 46 * Ms);
        rig.Run(9 * S);
        rig.Record = true;
        rig.Run(25 * S);
        var maxSlope = 0.5 * 2 * Math.PI * 440 / 48_000 * 1.003;
        for (var i = 1; i < rig.Output.Count; i++)
        {
            var step = Math.Abs(rig.Output[i].Left - rig.Output[i - 1].Left);
            Assert.True(step <= maxSlope + 2e-5, $"step {step} at {rig.Output[i].HeardNs / 1e9:0.0000} s");
        }

        Assert.Equal(0, line.Statistics.Jumps);
        Assert.InRange(line.Statistics.RatioMax, 0.998 - 1e-9, 1.002 + 1e-9);
        Assert.InRange(line.Statistics.RatioMin, 0.998 - 1e-9, 1.002 + 1e-9);

        var (line2, rig2) = Make(new RigOptions { Content = Clicks, ExpectedDelayNs = 46 * Ms });
        rig2.BeforePlayback = r => r.Line.SetVideoLatency(r.NowNs < 10 * S ? 40 * Ms : 46 * Ms);
        var delays = RecordClicks(rig2, 35 * S, 40 * S);
        Assert.NotEmpty(delays);
        Assert.All(delays, d => Assert.True(Math.Abs(d - (46 * Ms)) < 50_000, $"click delay {d / 1e6:0.000} ms"));
        Assert.Equal(0, line2.Statistics.Jumps);
    }

    [Theory]
    [InlineData(500.0, 0.0)]
    [InlineData(-500.0, 0.0)]
    [InlineData(0.0, 500.0)]
    [InlineData(250.0, -250.0)]
    public void Clock_drift_of_up_to_500_ppm_is_corrected_and_the_delay_holds(double captureDriftPpm, double playbackDriftPpm)
    {
        var (line, rig) = Make(new RigOptions
        {
            Content = Clicks,
            CaptureDriftPpm = captureDriftPpm,
            PlaybackDriftPpm = playbackDriftPpm,
            TimestampJitterNs = 200_000,
            Seed = 3,
        });
        var delays = RecordClicks(rig, 170 * S, 180 * S);
        var s = line.Statistics;
        Assert.Equal(0, s.Underruns + s.Overruns + s.Jumps + s.Resyncs + s.CaptureGaps);
        Assert.True(delays.Count >= 95, $"{delays.Count} clicks");
        Assert.All(delays, d => Assert.True(Math.Abs(d - (40 * Ms)) < 250_000, $"click delay {d / 1e6:0.000} ms"));

        // The ratio averages to the clock ratio (input frames per output frame), and the integral has learned it.
        var expected = (1 + (captureDriftPpm * 1e-6)) / (1 + (playbackDriftPpm * 1e-6));
        Assert.True(Math.Abs(s.RatioMean - expected) < 10e-6, $"ratio mean {(s.RatioMean - 1) * 1e6:0.0} ppm, clocks {(expected - 1) * 1e6:0.0} ppm");
        Assert.True(Math.Abs(s.LastIntegral - (expected - 1)) < 30e-6, $"integral {s.LastIntegral * 1e6:0.0} ppm");
    }

    [Fact]
    public void Jittery_timestamps_do_not_wobble_the_ratio()
    {
        var (line, rig) = Make(new RigOptions { Content = Sine, CaptureDriftPpm = 100, TimestampJitterNs = 500_000, Seed = 9 }, new DelayLineOptions { SettleNs = 60 * S });
        rig.Run(150 * S);
        var s = line.Statistics;
        Assert.True(s.MeasuredBlocks > 10_000);

        // ±0.5 ms of uniform jitter on every timestamp (far more than PipeWire's) moves the ratio within 300 ppm: half a cent.
        Assert.True(s.RatioMax - s.RatioMin < 300e-6, $"ratio {(s.RatioMin - 1) * 1e6:0.0} to {(s.RatioMax - 1) * 1e6:0.0} ppm");
        // The per-block measurement carries the timestamps' jitter (two timestamps of ±0.5 ms uniform: about 0.41 ms rms);
        // its mean, and the filtered delay, are on target.
        Assert.True(s.ErrorRms < 450_000, $"error rms {s.ErrorRms / 1e3:0} µs");
        Assert.True(Math.Abs(s.ErrorMean) < 30_000, $"error mean {s.ErrorMean / 1e3:0.0} µs");
        Assert.Equal(0, s.Underruns + s.Jumps);
    }

    [Fact]
    public void A_lost_capture_block_is_filled_with_silence_and_the_timeline_stays_true()
    {
        var (line, rig) = Make(new RigOptions { Content = Clicks });
        rig.CaptureOutage = (8 * S, (8 * S) + (4 * Ms)); // exactly one 5.3 ms block
        var delays = RecordClicks(rig, 10 * S, 15 * S);
        var s = line.Statistics;
        Assert.Equal(1, s.CaptureGaps);
        Assert.Equal(256, s.CaptureGapFrames);
        Assert.Equal(0, s.Underruns + s.Jumps + s.Resyncs);
        Assert.All(delays, d => Assert.True(Math.Abs(d - (40 * Ms)) < 21_000, $"click delay {d / 1e6:0.0000} ms"));
    }

    [Fact]
    public void When_the_source_stops_the_sound_fades_out_and_comes_back_on_target()
    {
        var (line, rig) = Make(new RigOptions { Content = Sine });
        rig.CaptureOutage = (8 * S, 9 * S);
        rig.Run((8 * S) - (50 * Ms));
        rig.Record = true;
        rig.Run((8 * S) + (300 * Ms));
        rig.Record = false;

        // The buffered sound plays out, fades (no step larger than the sine's own plus the fade's), then silence.
        var maxSlope = 0.5 * 2 * Math.PI * 440 / 48_000;
        for (var i = 1; i < rig.Output.Count; i++)
        {
            Assert.True(Math.Abs(rig.Output[i].Left - rig.Output[i - 1].Left) <= maxSlope + 0.02, $"step at {rig.Output[i].HeardNs / 1e9:0.0000} s");
        }

        var loud = rig.Output.Where(o => o.HeardNs > (8 * S) + (100 * Ms) && o.Left != 0f).Select(o => o.HeardNs / 1e9).ToList();
        Assert.True(loud.Count == 0, $"sound after the outage began: {loud.Count} frames from {loud.FirstOrDefault():0.0000} s; underruns {line.Statistics.Underruns}, state {line.State}");
        Assert.True(line.Statistics.Underruns >= 1, "no underrun");
        Assert.Equal(DelayLineState.Starved, line.State);

        // Back after a second (a discontinuity: the gap is too long to fill): resynchronised on target.
        rig.Run(11 * S);
        Assert.Equal(DelayLineState.Playing, line.State);
        Assert.Equal(1, line.Statistics.Resyncs);
        Assert.Equal(1, line.Statistics.CaptureDiscontinuities);
        Assert.Equal(40 * Ms, line.CurrentDelayNs, 100_000.0);
    }

    [Fact]
    public void Nothing_from_before_a_long_outage_is_played_when_the_source_returns()
    {
        // A level of 0.5 before the outage and 0.25 after: once it is back, no output may come near 0.5 (the ring is cleared
        // on the discontinuity, so the restart cannot fade in sound from before it).
        var (line, rig) = Make(new RigOptions { Content = static (_, r) => r.NowNs < 8 * S ? 0.5f : 0.25f });
        rig.CaptureOutage = (8 * S, 9 * S);
        rig.Run(9 * S);
        rig.Record = true;
        rig.Run(11 * S);
        Assert.Equal(1, line.Statistics.CaptureDiscontinuities);
        Assert.Equal(1, line.Statistics.Resyncs);
        Assert.Contains(rig.Output, o => Math.Abs(o.Left - 0.25f) < 1e-3);
        // (The kernel rings by up to 12% at the step from the cleared silence to the new level: 0.28, far from 0.5.)
        Assert.All(rig.Output, o => Assert.True(o.Left <= 0.3f, $"{o.Left} at {o.HeardNs / 1e9:0.0000} s: sound from before the outage"));
    }

    [Fact]
    public void Silent_until_the_first_video_latency_when_asked()
    {
        var line = new DelayLine(new DelayLineOptions { SilentUntilVideoLatency = true, SettleNs = S });
        var rig = new DelayLineRig(line, new RigOptions { Content = Sine });
        rig.Record = true;
        rig.Run(3 * S);
        Assert.All(rig.Output, o => Assert.Equal(0f, o.Left));
        Assert.Equal(DelayLineState.Muted, line.State);
        line.SetVideoLatency(40 * Ms);
        rig.Run(6 * S);
        Assert.Equal(DelayLineState.Playing, line.State);
        Assert.Contains(rig.Output, o => Math.Abs(o.Left) > 0.4f);
        Assert.Equal(40 * Ms, line.CurrentDelayNs, 100_000.0);
    }

    [Fact]
    public void A_playback_stall_longer_than_the_ring_is_an_overrun_and_resynchronises()
    {
        var (line, rig) = Make(new RigOptions { Content = Clicks });
        rig.PlaybackOutage = (8 * S, 10 * S);
        var delays = RecordClicks(rig, 13 * S, 16 * S);
        var s = line.Statistics;
        Assert.Equal(1, s.Overruns);
        Assert.Equal(1, s.Resyncs);
        Assert.Equal(1, s.SinkDiscontinuities);
        Assert.All(delays, d => Assert.True(Math.Abs(d - (40 * Ms)) < 21_000, $"click delay {d / 1e6:0.0000} ms"));
    }

    [Fact]
    public void A_short_playback_stall_is_corrected_at_once_by_a_cross_fade()
    {
        // A sink switch: the playback side is not called for 300 ms, so its delay is 300 ms too long when it resumes. The
        // sink's timeline jumped: the line moves straight back to the target.
        var (line, rig) = Make(new RigOptions { Content = Clicks });
        rig.PlaybackOutage = (8 * S, (8 * S) + (300 * Ms));
        var delays = RecordClicks(rig, (8 * S) + (400 * Ms), 14 * S);
        var s = line.Statistics;
        Assert.Equal(1, s.SinkDiscontinuities);
        Assert.True(s.Relocks >= 1, $"re-locks {s.Relocks}");
        Assert.Equal(0, s.Overruns + s.Underruns + s.Jumps);
        Assert.All(delays, d => Assert.True(Math.Abs(d - (40 * Ms)) < 50_000, $"click delay {d / 1e6:0.000} ms"));
    }

    [Fact]
    public void A_change_of_the_sinks_latency_is_put_right_at_once_with_a_cross_fade()
    {
        // Another sink 3 ms slower: its heard times move by 3 ms; the delay is back on target at once.
        var (line, rig) = Make(new RigOptions { Content = Clicks });
        rig.Run(8 * S);
        rig.SinkLatencyShiftNs = 3 * Ms;
        var delays = RecordClicks(rig, (8 * S) + (100 * Ms), 12 * S);
        Assert.Equal(1, line.Statistics.PathChanges);
        Assert.Equal(0, line.Statistics.Jumps + line.Statistics.Underruns);
        Assert.NotEmpty(delays);
        Assert.All(delays, d => Assert.True(Math.Abs(d - (40 * Ms)) < 60_000, $"click delay {d / 1e6:0.000} ms"));
    }

    [Fact]
    public void Mute_fades_out_and_unmuting_starts_again_on_target()
    {
        var (line, rig) = Make(new RigOptions { Content = Sine });
        rig.BeforePlayback = r => r.Line.SetMuted(r.NowNs >= 8 * S && r.NowNs < 9 * S);
        rig.Run((8 * S) - (20 * Ms));
        rig.Record = true;
        rig.Run((8 * S) + (200 * Ms));
        rig.Record = false;
        var maxSlope = 0.5 * 2 * Math.PI * 440 / 48_000;
        for (var i = 1; i < rig.Output.Count; i++)
        {
            Assert.True(Math.Abs(rig.Output[i].Left - rig.Output[i - 1].Left) <= maxSlope + 0.003, $"step at {rig.Output[i].HeardNs / 1e9:0.0000} s");
        }

        Assert.True(rig.Output.Where(o => o.HeardNs > (8 * S) + (30 * Ms)).All(o => o.Left == 0f));
        Assert.Equal(DelayLineState.Muted, line.State);
        rig.Run(12 * S);
        Assert.Equal(DelayLineState.Playing, line.State);
        Assert.Equal(40 * Ms, line.CurrentDelayNs, 100_000.0);
        Assert.True(line.Statistics.MutedBlocks > 150);
    }

    [Fact]
    public void Without_capture_data_the_output_is_silence()
    {
        var line = new DelayLine();
        var block = Enumerable.Repeat(1f, 512).ToArray();
        line.OnPlayback(block, 256, 1_000_000_000, 5_000_000);
        Assert.All(block, v => Assert.Equal(0f, v));
        Assert.Equal(DelayLineState.Waiting, line.State);
    }

    [Fact]
    public void The_clocks_are_measured_against_the_timestamps()
    {
        var (line, rig) = Make(new RigOptions { Content = Sine, CaptureDriftPpm = 120, PlaybackDriftPpm = -40 });
        rig.Run(60 * S);
        Assert.Equal(120, line.Statistics.CaptureClockPpm, 1.0);
        Assert.Equal(-40, line.Statistics.SinkClockPpm, 1.0);
    }
}
