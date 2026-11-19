using FrameMotionPlus.App.Capture;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// The capture rate: exact rates from the driver's intervals, the console presets at the NTSC rates, --capture-fps, and the
/// beat detector on simulated frame arrival (a card at one rate, a source at another). Pure: no device needed.
/// </summary>
public class RepeatBeatTests
{
    private static readonly FrameRate Ntsc30 = new(30000, 1001);
    private static readonly FrameRate Ntsc60 = new(60000, 1001);

    /// <summary>
    /// A card capturing at <paramref name="capture"/> from a source of new pictures at <paramref name="source"/>: each
    /// capture takes the newest source frame complete by then, a repeat when it is the one the last capture took.
    /// <paramref name="sourcePerPicture"/> source frames make one picture (2: a 30 fps game in a 60 Hz signal).
    /// </summary>
    private static RepeatBeat Simulate(FrameRate capture, FrameRate source, double seconds, int sourcePerPicture = 1, double phase = 0.37)
    {
        var detector = new RepeatBeatDetector(capture);
        var frames = (long)(seconds * capture.Hz);
        var lastPicture = long.MinValue;
        for (long k = 0; k < frames; k++)
        {
            var t = capture.TickTimeNs(k);
            var sourceFrame = (long)Math.Floor((t / source.PeriodNs) + phase);
            var picture = (long)Math.Floor(sourceFrame / (double)sourcePerPicture);
            detector.Add(t, picture != lastPicture);
            lastPicture = picture;
        }

        return detector.Snapshot;
    }

    [Fact]
    public void A_30_000_capture_of_a_29_97_source_repeats_every_33_4_s_and_says_so()
    {
        var beat = Simulate(FrameRate.FromHz(30), Ntsc30, 200);
        Assert.Equal(BeatKind.Repeats, beat.Kind);
        Assert.InRange(beat.PeriodNs / 1e9, 33.2, 33.5); // 1001/30 s
        Assert.InRange(beat.ExtraRepeats, 5, 6);
        Assert.Equal(Ntsc30, beat.Suggested);
        Assert.InRange(beat.ImpliedSourceHz, 29.969, 29.971);
        Assert.Equal("29.97", RepeatBeat.RateText(beat.Suggested));
    }

    [Fact]
    public void A_60_000_capture_of_a_30_fps_game_in_a_59_94_signal_repeats_every_16_7_s()
    {
        // The steady cadence is one repeat per picture; the beat makes a run of two.
        var beat = Simulate(FrameRate.FromHz(60), Ntsc60, 100, sourcePerPicture: 2);
        Assert.Equal(BeatKind.Repeats, beat.Kind);
        Assert.InRange(beat.PeriodNs / 1e9, 16.5, 16.9);
        Assert.Equal(Ntsc60, beat.Suggested);
    }

    [Fact]
    public void Matched_rates_show_no_beat()
    {
        var a = Simulate(Ntsc30, Ntsc30, 300);
        Assert.Equal(BeatKind.None, a.Kind);
        Assert.Equal(0, a.ExtraRepeats);
        Assert.False(a.Suggested.IsValid);

        var b = Simulate(Ntsc60, Ntsc60, 300, sourcePerPicture: 2);
        Assert.Equal(BeatKind.None, b.Kind);
        Assert.Equal(0, b.ExtraRepeats + b.Skips);
    }

    [Fact]
    public void A_59_94_capture_of_a_true_60_Hz_source_leaves_a_frame_out_every_16_7_s_when_the_content_shows_it()
    {
        // A 30 fps picture in a 60.000 Hz signal: the steady run is one repeat, a frame left out makes a run of none.
        var beat = Simulate(Ntsc60, FrameRate.FromHz(60), 100, sourcePerPicture: 2);
        Assert.Equal(BeatKind.Skips, beat.Kind);
        Assert.InRange(beat.PeriodNs / 1e9, 16.5, 16.9);
        Assert.Equal(FrameRate.FromHz(60), beat.Suggested);

        // Every frame new (a 60 fps game): the frame left out leaves no trace in the repeats.
        var hidden = Simulate(Ntsc60, FrameRate.FromHz(60), 100);
        Assert.Equal(BeatKind.None, hidden.Kind);
    }

    [Fact]
    public void Irregular_repeats_from_the_content_are_no_beat()
    {
        var detector = new RepeatBeatDetector(Ntsc30);
        var random = new Random(7);
        long t = 0;
        for (var k = 0; k < 30 * 300; k++)
        {
            t += (long)Ntsc30.PeriodNs;
            detector.Add(t, random.NextDouble() > 0.002); // a stutter now and then, at no fixed period
        }

        var beat = detector.Snapshot;
        Assert.True(beat.ExtraRepeats > 5);
        Assert.Equal(BeatKind.None, beat.Kind);
    }

    [Fact]
    public void A_still_picture_or_a_slow_game_is_no_steady_cadence()
    {
        var detector = new RepeatBeatDetector(Ntsc30);
        for (var k = 0; k < 30 * 120; k++)
        {
            detector.Add(k * 33_366_667L, k % 3 == 0 || (k / 30) % 7 == 0); // 10 fps with bursts
        }

        Assert.Equal(BeatKind.None, detector.Snapshot.Kind);
    }

    [Fact]
    public void A_period_that_matches_no_sibling_rate_suggests_nothing()
    {
        Assert.False(RepeatBeatDetector.Suggest(FrameRate.FromHz(30), BeatKind.Repeats, 60 * Nanoseconds.PerSecond).IsValid); // an autosave hitch
        Assert.Equal(Ntsc30, RepeatBeatDetector.Suggest(FrameRate.FromHz(30), BeatKind.Repeats, 33_366_667_000));
        Assert.False(RepeatBeatDetector.Suggest(FrameRate.FromHz(30), BeatKind.Skips, 33_366_667_000).IsValid); // wrong direction
        Assert.Equal(new FrameRate(120000, 1001), RepeatBeatDetector.Suggest(FrameRate.FromHz(120), BeatKind.Repeats, 8_341_667_000));
    }

    [Theory]
    [InlineData(333667u, 10000000u, 30000L, 1001L)] // UVC's 29.97 (100 ns units)
    [InlineData(333333u, 10000000u, 30L, 1L)]
    [InlineData(166833u, 10000000u, 60000L, 1001L)]
    [InlineData(166667u, 10000000u, 60L, 1L)]
    [InlineData(83333u, 10000000u, 120L, 1L)]
    [InlineData(41666u, 10000000u, 240L, 1L)] // listed as 240.004
    [InlineData(1001u, 30000u, 30000L, 1001L)]
    [InlineData(1u, 50u, 50L, 1L)]
    [InlineData(1u, 7u, 7L, 1L)]
    public void Driver_intervals_become_exact_rates(uint numerator, uint denominator, long rateNumerator, long rateDenominator)
    {
        Assert.Equal(new FrameRate(rateNumerator, rateDenominator), CaptureFormats.ExactRate(new V4l2Interval(numerator, denominator)));
        Assert.False(CaptureFormats.ExactRate(default).IsValid);
    }

    [Fact]
    public void An_interval_that_is_neither_whole_nor_ntsc_is_kept_as_it_is()
    {
        Assert.Equal(new FrameRate(10000000, 400000 + 1234), CaptureFormats.ExactRate(new V4l2Interval(401234, 10000000)));
    }

    [Fact]
    public void The_console_presets_ask_for_the_ntsc_rates_and_capture_fps_overrides_them()
    {
        var o = CaptureOptions.Parse(["--mode", "4k30-hdr"]);
        Assert.Equal(Ntsc30, o.CaptureRate);
        Assert.Equal(30000.0 / 1001, o.Request.Fps, 9);
        Assert.False(o.CaptureRateForced);
        Assert.Equal(Ntsc60, CaptureOptions.Parse(["--mode", "1440p60-hdr"]).CaptureRate);
        Assert.Equal(FrameRate.FromHz(120), CaptureOptions.Parse(["--mode", "1440p120-sdr"]).CaptureRate); // no 119.88 offered

        o = CaptureOptions.Parse(["--mode", "4k30-hdr", "--capture-fps", "30"]);
        Assert.Equal(FrameRate.FromHz(30), o.CaptureRate);
        Assert.Equal(30, o.Request.Fps);
        Assert.True(o.CaptureRateForced);
        Assert.Equal(Ntsc60, CaptureOptions.Parse(["--mode", "1440p60-hdr", "--capture-fps", "60000/1001"]).CaptureRate);
        Assert.Equal(Ntsc60, CaptureOptions.Parse(["--size", "2560x1440", "--capture-fps", "59.94"]).CaptureRate);
        Assert.Equal(Ntsc30, CaptureOptions.Parse(["--size", "3840x2160", "--fps", "29.97"]).CaptureRate);

        Assert.Throws<FrameMotionPlus.App.Cli.UsageException>(() => CaptureOptions.Parse(["--fps", "30", "--capture-fps", "30"]));
        Assert.Throws<FrameMotionPlus.App.Cli.UsageException>(() => CaptureOptions.Parse(["--capture-fps", "fast"]));
        Assert.Throws<FrameMotionPlus.App.Cli.UsageException>(() => CaptureOptions.Parse(["--capture-fps", "0"]));
    }

    [Fact]
    public void The_chosen_mode_for_29_97_is_the_drivers_29_97_interval()
    {
        var offered = new List<CaptureMode>
        {
            new(CapturePixelFormat.P010, 3840, 2160, new V4l2Interval(333333, 10000000)),
            new(CapturePixelFormat.P010, 3840, 2160, new V4l2Interval(333667, 10000000)),
        };
        var mode = CaptureFormats.Choose(offered, new CaptureRequest { Width = 3840, Height = 2160, Fps = Ntsc30.Hz, Format = CapturePixelFormat.P010 });
        Assert.Equal(Ntsc30, CaptureFormats.ExactRate(mode.Interval));
        Assert.Equal([FrameRate.FromHz(30), Ntsc30], CaptureFormats.RatesAt(offered, CapturePixelFormat.P010, 3840, 2160));
    }

    [Fact]
    public void The_summary_line_says_what_was_asked_and_what_the_beat_says()
    {
        var beat = Simulate(FrameRate.FromHz(30), Ntsc30, 200);
        var line = CapturePrinter.RateLine(new FrameMotionPlus.Pipeline.CaptureSummary
        {
            Counters = new CaptureStatistics(),
            State = CaptureSignalState.Streaming,
            AskedRate = FrameRate.FromHz(30),
            NegotiatedRate = FrameRate.FromHz(30),
            Beat = beat,
            OfferedRates = [FrameRate.FromHz(30), Ntsc30],
        });
        Assert.Contains("asked 30 fps, set 30 fps", line, StringComparison.Ordinal);
        Assert.Contains("periodic: a repeat every 33.", line, StringComparison.Ordinal);
        Assert.Contains("use --capture-fps 29.97", line, StringComparison.Ordinal);

        var none = CapturePrinter.RateLine(new FrameMotionPlus.Pipeline.CaptureSummary
        {
            Counters = new CaptureStatistics(),
            State = CaptureSignalState.Streaming,
            AskedRate = Ntsc30,
            NegotiatedRate = Ntsc30,
            Beat = Simulate(Ntsc30, Ntsc30, 60),
        });
        Assert.Contains("asked 29.97 fps (30000/1001, 33.3667 ms)", none, StringComparison.Ordinal);
        Assert.Contains("no periodic beat", none, StringComparison.Ordinal);
    }
}
