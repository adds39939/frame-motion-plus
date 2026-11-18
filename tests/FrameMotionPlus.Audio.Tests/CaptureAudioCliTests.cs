using FrameMotionPlus.App.Capture;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.Pipeline;

namespace FrameMotionPlus.Audio.Tests;

/// <summary>
/// <c>fmp capture</c>'s audio options (parsed as the command parses them), their help, and the audio side never stopping
/// the video: whatever is missing, <see cref="CaptureAudio.Start"/> returns with audio off and one reason. Also the video
/// latency the sound follows (<see cref="LatencyFollower"/>).
/// </summary>
public class CaptureAudioCliTests
{
    [Fact]
    public void Audio_is_auto_with_no_trim_by_default()
    {
        var o = CaptureOptions.Parse(["--mode", "4k30-hdr"]);
        Assert.Equal(AudioChoice.Auto, o.Audio);
        Assert.Null(o.AudioSource);
        Assert.Null(o.AudioSink);
        Assert.Equal(0, o.AudioOffsetMs);
    }

    [Fact]
    public void Every_audio_option_is_parsed()
    {
        var o = CaptureOptions.Parse(["/dev/video0", "--audio", "on", "--audio-sink", "alsa_output.x", "--audio-offset-ms", "-12.5", "--audio-source", "alsa_input.y"]);
        Assert.Equal(AudioChoice.On, o.Audio);
        Assert.Equal("alsa_input.y", o.AudioSource);
        Assert.Equal("alsa_output.x", o.AudioSink);
        Assert.Equal(-12.5, o.AudioOffsetMs);
        Assert.Equal(AudioChoice.Off, CaptureOptions.Parse(["--audio", "off"]).Audio);
        Assert.Equal(AudioChoice.Auto, CaptureOptions.Parse(["--audio=AUTO"]).Audio);
        Assert.Equal(40, CaptureOptions.Parse(["--audio-offset-ms=40"]).AudioOffsetMs);
    }

    [Fact]
    public void Naming_a_source_turns_audio_on()
    {
        Assert.Equal(AudioChoice.On, CaptureOptions.Parse(["--audio-source", "alsa_input.y"]).Audio);
        Assert.Equal(AudioChoice.Auto, CaptureOptions.Parse(["--audio", "auto", "--audio-source", "alsa_input.y"]).Audio);
    }

    [Fact]
    public void Naming_a_sink_turns_audio_on_and_may_be_part_of_a_description()
    {
        var o = CaptureOptions.Parse(["--audio-sink", "INZONE H9"]);
        Assert.Equal(AudioChoice.On, o.Audio);
        Assert.Equal("INZONE H9", o.AudioSink);
    }

    [Fact]
    public void A_sink_is_found_by_its_name_or_one_unique_part_of_a_name_or_description()
    {
        (string, string?)[] sinks =
        [
            ("alsa_output.usb-Sony_INZONE_H9_II-00.analog-stereo", "INZONE H9 II Analog Stereo"),
            ("alsa_output.pci-0000_01_00.1.hdmi-stereo", "GB202 High Definition Audio Controller Digital Stereo (HDMI) [LG TV SSCR2]"),
            ("alsa_output.usb-Generic_USB_Audio-00.HiFi__Speaker__sink", "USB Audio Speakers"),
            ("alsa_output.usb-Generic_USB_Audio-00.HiFi__Headphones__sink", "USB Audio Front Headphones"),
        ];
        Assert.Equal(sinks[1].Item1, PipeWireAudioDelay.ResolveSink(sinks, "alsa_output.pci-0000_01_00.1.hdmi-stereo", out var why));
        Assert.Null(why);
        Assert.Equal(sinks[0].Item1, PipeWireAudioDelay.ResolveSink(sinks, "inzone", out _));
        Assert.Equal(sinks[1].Item1, PipeWireAudioDelay.ResolveSink(sinks, "lg tv", out _));
        Assert.Equal(sinks[3].Item1, PipeWireAudioDelay.ResolveSink(sinks, "HEADPHONES", out _));

        Assert.Null(PipeWireAudioDelay.ResolveSink(sinks, "usb audio", out why));
        Assert.Contains("matches 2 sinks", why, StringComparison.Ordinal);
        Assert.Contains("USB Audio Speakers", why, StringComparison.Ordinal);
        Assert.Contains("USB Audio Front Headphones", why, StringComparison.Ordinal);

        Assert.Null(PipeWireAudioDelay.ResolveSink(sinks, "bluetooth", out why));
        Assert.Contains("no PipeWire audio sink matches 'bluetooth'", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--audio", "maybe")]
    [InlineData("--audio-offset-ms", "501")]
    [InlineData("--audio-offset-ms", "-201")]
    [InlineData("--audio-offset-ms", "soon")]
    [InlineData("--audio-sink", " ")]
    [InlineData("--audio-source", "two words")]
    public void Bad_audio_values_are_usage_errors(string option, string value)
    {
        Assert.Throws<UsageException>(() => CaptureOptions.Parse([option, value]));
    }

    [Theory]
    [InlineData("--audio-source", "alsa_input.y")]
    [InlineData("--audio-sink", "alsa_output.x")]
    [InlineData("--audio-offset-ms", "10")]
    public void Audio_off_takes_no_other_audio_option(string option, string value)
    {
        var e = Assert.Throws<UsageException>(() => CaptureOptions.Parse(["--audio", "off", option, value]));
        Assert.Contains("'--audio off' takes no", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void List_takes_no_audio_option()
    {
        Assert.Throws<UsageException>(() => CaptureOptions.Parse(["--list", "--audio", "off"]));
    }

    [Fact]
    public void The_help_describes_every_audio_option()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(CommandDispatcher.ExitOk, CommandDispatcher.Run(["--help"], output, error));
        var help = output.ToString();
        foreach (var option in new[] { "--audio <on|off|auto>", "--audio-source <name>", "--audio-sink <name>", "--audio-offset-ms <n>" })
        {
            Assert.Contains(option, help, StringComparison.Ordinal);
        }

        Assert.Contains("the audio source", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_off_is_off_with_its_reason_and_never_touches_pipewire()
    {
        using var events = new StringWriter();
        using var audio = CaptureAudio.Start(CaptureOptions.Parse(["--audio", "off"]), "/dev/video0", events);
        Assert.Null(audio.Delay);
        Assert.Equal("--audio off", audio.OffReason);
        Assert.Null(audio.Finish());
        using var o = new StringWriter();
        var warnings = new List<string>();
        audio.PrintSummary(o, warnings);
        Assert.Contains("audio       off (the reason is in the setup above)", o.ToString(), StringComparison.Ordinal);
        using var setup = new StringWriter();
        audio.PrintSetup(setup, CaptureOptions.Parse(["--audio", "off"]));
        Assert.Equal("  audio       off: --audio off", setup.ToString().TrimEnd());
        Assert.Empty(warnings);
    }

    [Fact]
    public void A_source_that_is_not_there_leaves_audio_off_with_one_reason_and_a_warning_when_asked_for()
    {
        // With PipeWire: no such node. Without (CI, Windows): no library or no server. Either way: off, one line, no throw.
        using var events = new StringWriter();
        using var audio = CaptureAudio.Start(CaptureOptions.Parse(["--audio-source", "fmp-no-such-source"]), "/dev/video-none", events);
        Assert.Null(audio.Delay);
        Assert.False(string.IsNullOrWhiteSpace(audio.OffReason));
        Assert.DoesNotContain('\n', audio.OffReason);
        using var o = new StringWriter();
        var warnings = new List<string>();
        audio.PrintSummary(o, warnings);
        Assert.Contains("audio       OFF (the reason is in the setup above)", o.ToString(), StringComparison.Ordinal);
        Assert.Single(warnings);
        using var setup = new StringWriter();
        audio.PrintSetup(setup, CaptureOptions.Parse([]));
        Assert.StartsWith("  audio       OFF: ", setup.ToString(), StringComparison.Ordinal);
        Assert.Single(setup.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Auto_without_an_audio_source_for_the_device_is_quietly_off()
    {
        using var events = new StringWriter();
        using var audio = CaptureAudio.Start(CaptureOptions.Parse([]), "/dev/video-none", events);
        Assert.Null(audio.Delay);
        Assert.NotNull(audio.OffReason);
        var warnings = new List<string>();
        using var o = new StringWriter();
        audio.PrintSummary(o, warnings);
        Assert.Empty(warnings);
    }

    [Fact]
    public void The_latency_follower_is_a_running_mean_at_first_then_smooths_with_a_five_second_time_constant()
    {
        var f = new LatencyFollower();
        Assert.Equal(0, f.ValueNs);
        var t = 1_000_000_000L;
        f.Add(50_000_000, t); // an early frame, slow
        Assert.Equal(50_000_000, f.ValueNs);
        for (var i = 0; i < 99; i++)
        {
            t += 16_666_667;
            f.Add(40_000_000, t);
        }

        // The running mean of the first 100 frames (1.65 s): one slow frame is a hundredth of it.
        Assert.Equal(40.1e6, f.ValueNs, 0.01e6);
        for (var i = 0; i < 300; i++)
        {
            t += 16_666_667;
            f.Add(40_000_000, t);
        }

        // Then a step to 46 ms: after 5 s, 63% of the way; after 30 s, there.
        for (var i = 0; i < 300; i++)
        {
            t += 16_666_667;
            f.Add(46_000_000, t);
        }

        Assert.Equal(40e6 + (6e6 * (1 - Math.Exp(-1))), f.ValueNs, 0.1e6);
        for (var i = 0; i < 1500; i++)
        {
            t += 16_666_667;
            f.Add(46_000_000, t);
        }

        Assert.Equal(46e6, f.ValueNs, 0.05e6);
        Assert.Equal(2200, f.Samples);
    }

    [Fact]
    public void One_late_frame_moves_the_latency_followed_by_at_most_its_clamped_share()
    {
        var f = new LatencyFollower();
        var t = 1_000_000_000L;
        for (var i = 0; i < 600; i++)
        {
            t += 16_666_667;
            f.Add(32_000_000, t);
        }

        t += 16_666_667;
        f.Add(400_000_000, t); // a 400 ms hitch: counts as 20 ms, for one frame's share of 5 s
        Assert.True(f.ValueNs - 32_000_000 < LatencyFollower.ClampNs * 0.004, $"moved {(f.ValueNs - 32_000_000) / 1e6:0.000} ms");
        f.Add(-5, t + 1);
        f.Add(3_000_000_000, t + 2); // not latencies: ignored
        Assert.Equal(601, f.Samples);
    }
}
