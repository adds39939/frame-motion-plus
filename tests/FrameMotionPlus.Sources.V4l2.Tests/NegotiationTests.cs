using FrameMotionPlus.App.Capture;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Pipeline;
using FrameMotionPlus.Sources.V4l2.Interop;
using FrameMotionPlus.Sources.V4l2.Tests.Support;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// Choosing a mode from what a device offers, mapping the V4L2 colour fields (with the overrides), and fmp capture's
/// options. Pure: no device needed. The offered modes are the Elgato 4K X's, as its driver enumerates them.
/// </summary>
public class NegotiationTests
{
    private static readonly V4l2Interval Fps30 = new(1, 30);
    private static readonly V4l2Interval Fps2997 = new(1001, 30000);
    private static readonly V4l2Interval Fps60 = new(1, 60);
    private static readonly V4l2Interval Fps5994 = new(1001, 60000);
    private static readonly V4l2Interval Fps120 = new(1, 120);

    /// <summary>The Elgato 4K X's uncompressed modes (a subset: what the tests need), plus MJPEG, which must never be chosen.</summary>
    private static List<CaptureMode> Elgato()
    {
        var formats = new List<(V4l2FormatDescription, IReadOnlyList<V4l2FrameSize>)>
        {
            (new(V4l2Defs.PixFmtYuyv, "YUYV 4:2:2", false, false),
                [new(1920, 1080, [Fps60, Fps30]), new(3840, 2160, [Fps30, Fps2997])]),
            (new(V4l2Defs.PixFmtMjpeg, "Motion-JPEG", true, false),
                [new(3840, 2160, [Fps120, Fps60, Fps30])]),
            (new(V4l2Defs.PixFmtNv12, "Y/UV 4:2:0", false, false),
                [new(1920, 1080, [Fps120, Fps60, Fps5994]), new(2560, 1440, [new(1, 144), Fps120, Fps60, Fps5994]), new(3840, 2160, [Fps60, Fps5994, Fps30, Fps2997])]),
            (new(V4l2Defs.PixFmtP010, "10-bit Y/UV 4:2:0", false, false),
                [new(2560, 1440, [Fps60, Fps5994, Fps30]), new(3840, 2160, [Fps30, Fps2997])]),
        };
        return CaptureFormats.Modes(formats);
    }

    [Fact]
    public void A_presets_console_rate_falls_back_to_the_whole_rate_where_the_device_has_no_ntsc_rate()
    {
        // 1920x1080 YUYV offers 60 and 30 only (as vivid and many cards do): a preset's 59.94 takes 60 there,
        // a rate the user typed does not, and a size that has 59.94 keeps it.
        var preset = new CaptureRequest { Width = 1920, Height = 1080, Fps = 60000.0 / 1001, FallbackFps = 60, Format = CapturePixelFormat.Yuyv };
        Assert.Equal(Fps60, CaptureFormats.Choose(Elgato(), preset).Interval);

        var typed = preset with { FallbackFps = 0 };
        Assert.Contains("does not offer 59.94 fps", Assert.Throws<CaptureFormatException>(() => CaptureFormats.Choose(Elgato(), typed)).Message, StringComparison.Ordinal);

        var offered = new CaptureRequest { Width = 2560, Height = 1440, Fps = 60000.0 / 1001, FallbackFps = 60, Format = CapturePixelFormat.P010 };
        Assert.Equal(Fps5994, CaptureFormats.Choose(Elgato(), offered).Interval);
    }

    [Fact]
    public void Nothing_asked_for_takes_the_largest_10_bit_mode_at_its_highest_rate()
    {
        var mode = CaptureFormats.Choose(Elgato(), new CaptureRequest());
        Assert.Equal(new CaptureMode(CapturePixelFormat.P010, 3840, 2160, Fps30), mode);
    }

    [Fact]
    public void Compressed_formats_are_never_modes()
    {
        Assert.DoesNotContain(Elgato(), m => m.Width == 3840 && m.Interval == Fps120);
        Assert.All(Elgato(), m => Assert.NotEqual(CapturePixelFormat.Unknown, m.Format));
    }

    [Theory]
    [InlineData(2560, 1440, 60, CapturePixelFormat.P010, CapturePixelFormat.P010, 60.0)]
    [InlineData(2560, 1440, 0, CapturePixelFormat.Unknown, CapturePixelFormat.P010, 60.0)] // 10-bit first, highest rate
    [InlineData(2560, 1440, 120, CapturePixelFormat.Unknown, CapturePixelFormat.Nv12, 120.0)] // only NV12 has 120
    [InlineData(3840, 2160, 60, CapturePixelFormat.Unknown, CapturePixelFormat.Nv12, 60.0)]
    [InlineData(3840, 2160, 59.94, CapturePixelFormat.Nv12, CapturePixelFormat.Nv12, 59.94)] // 59.94 is not 60
    [InlineData(0, 0, 0, CapturePixelFormat.Nv12, CapturePixelFormat.Nv12, 60.0)] // the largest NV12 size, its highest rate
    [InlineData(0, 0, 144, CapturePixelFormat.Unknown, CapturePixelFormat.Nv12, 144.0)]
    public void Requests_choose_the_matching_mode(int w, int h, double fps, CapturePixelFormat format, CapturePixelFormat expected, double expectedFps)
    {
        var mode = CaptureFormats.Choose(Elgato(), new CaptureRequest { Width = w, Height = h, Fps = fps, Format = format });
        Assert.Equal(expected, mode.Format);
        Assert.Equal(expectedFps, mode.Fps, 2);
        if (w > 0)
        {
            Assert.Equal((w, h), (mode.Width, mode.Height));
        }
    }

    [Theory]
    [InlineData(3840, 2160, 60, CapturePixelFormat.P010, "rates offered: 30, 29.97")] // the card's 10-bit 4K is 30 only
    [InlineData(1280, 720, 0, CapturePixelFormat.Unknown, "sizes offered")]
    [InlineData(0, 0, 0, CapturePixelFormat.Yuyv, "")] // offered: chosen, no error
    public void Requests_the_device_cannot_do_say_what_it_offers(int w, int h, double fps, CapturePixelFormat format, string message)
    {
        var request = new CaptureRequest { Width = w, Height = h, Fps = fps, Format = format };
        if (message.Length == 0)
        {
            Assert.Equal(format, CaptureFormats.Choose(Elgato(), request).Format);
            return;
        }

        var e = Assert.Throws<CaptureFormatException>(() => CaptureFormats.Choose(Elgato(), request));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_format_the_device_does_not_offer_is_refused_naming_the_ones_it_does()
    {
        var nv12Only = Elgato().Where(m => m.Format == CapturePixelFormat.Nv12).ToList();
        var e = Assert.Throws<CaptureFormatException>(() => CaptureFormats.Choose(nv12Only, new CaptureRequest { Format = CapturePixelFormat.P010 }));
        Assert.Contains("does not offer P010", e.Message, StringComparison.Ordinal);
        Assert.Contains("NV12", e.Message, StringComparison.Ordinal);
    }

    /// <summary>The Elgato's fields for P010: sRGB / Rec. 709 / Rec. 709 / default (as measured), the PS5 sending HDR10.</summary>
    [Fact]
    public void The_elgatos_fields_resolve_to_sdr_bt709_limited_unless_overridden()
    {
        var auto = CaptureColorMapping.Resolve(V4l2Defs.ColorspaceSrgb, V4l2Defs.XferFunc709, V4l2Defs.YcbcrEnc709, V4l2Defs.QuantizationDefault, 2160, 10,
            new CaptureColorOverrides { Hdr = HdrMode.Auto });
        Assert.False(auto.IsHdr);
        Assert.Equal(MatrixCoefficients.Bt709, auto.Matrix);
        Assert.Equal(ColorRange.Limited, auto.Range); // a YCbCr format's default quantization is limited (V4L2_MAP_QUANTIZATION_DEFAULT)
        Assert.Equal(ColorPrimaries.Bt709, auto.FrameColor.Primaries);
        Assert.Equal(MatrixCoefficients.Identity, auto.FrameColor.Matrix);
        Assert.Equal(ColorRange.Full, auto.FrameColor.Range);

        // fmp capture's default: a 10-bit capture is HDR10, with HDR10's matrix (the card reports Rec. 709 for it).
        var tenBit = CaptureColorMapping.Resolve(V4l2Defs.ColorspaceSrgb, V4l2Defs.XferFunc709, V4l2Defs.YcbcrEnc709, V4l2Defs.QuantizationDefault, 2160, 10);
        Assert.True(tenBit.IsHdr);
        Assert.Equal(TransferFunction.Pq, tenBit.FrameColor.Transfer);
        Assert.Equal(ColorPrimaries.Bt2020, tenBit.FrameColor.Primaries);
        Assert.Equal(MatrixCoefficients.Bt2020NonConstant, tenBit.Matrix);
        Assert.Equal(ColorRange.Limited, tenBit.Range);
        Assert.Contains("10-bit capture is taken as HDR10", tenBit.Origin, StringComparison.Ordinal);

        // The same default leaves an 8-bit capture as the device says.
        var eightBit = CaptureColorMapping.Resolve(V4l2Defs.ColorspaceSrgb, V4l2Defs.XferFunc709, V4l2Defs.YcbcrEnc709, V4l2Defs.QuantizationDefault, 2160, 8);
        Assert.False(eightBit.IsHdr);
    }

    [Fact]
    public void Overrides_win_and_say_so()
    {
        var c = CaptureColorMapping.Resolve(V4l2Defs.ColorspaceSrgb, V4l2Defs.XferFunc709, V4l2Defs.YcbcrEnc709, V4l2Defs.QuantizationDefault, 1440, 10,
            new CaptureColorOverrides { Hdr = HdrMode.On, Range = ColorRange.Full, Matrix = MatrixCoefficients.Bt709, Siting = ChromaSiting.TopLeft });
        Assert.True(c.IsHdr);
        Assert.Equal(MatrixCoefficients.Bt709, c.Matrix);
        Assert.Equal(ColorRange.Full, c.Range);
        Assert.Equal(ChromaSiting.TopLeft, c.Siting);
        Assert.Contains("forced", c.Origin, StringComparison.Ordinal);

        // A device that does report PQ, forced off.
        var off = CaptureColorMapping.Resolve(V4l2Defs.ColorspaceBt2020, V4l2Defs.XferFuncSmpte2084, V4l2Defs.YcbcrEncBt2020, V4l2Defs.QuantizationLimRange, 2160, 10,
            new CaptureColorOverrides { Hdr = HdrMode.Off });
        Assert.False(off.IsHdr);
        Assert.Equal(ColorPrimaries.Bt709, off.FrameColor.Primaries);
        Assert.Equal(MatrixCoefficients.Bt2020NonConstant, off.Matrix);
    }

    [Fact]
    public void A_device_that_signals_hdr10_is_hdr10_without_an_override()
    {
        var c = CaptureColorMapping.Resolve(V4l2Defs.ColorspaceBt2020, V4l2Defs.XferFuncSmpte2084, V4l2Defs.YcbcrEncDefault, V4l2Defs.QuantizationDefault, 2160, 10,
            new CaptureColorOverrides { Hdr = HdrMode.Auto });
        Assert.True(c.IsHdr);
        Assert.Equal(MatrixCoefficients.Bt2020NonConstant, c.Matrix); // the BT.2020 colourspace's default encoding
        Assert.Equal(ColorRange.Limited, c.Range);
    }

    [Theory]
    [InlineData(V4l2Defs.ColorspaceSrgb, V4l2Defs.XferFuncSrgb, V4l2Defs.YcbcrEnc601)] // vivid's default
    [InlineData(V4l2Defs.ColorspaceRec709, V4l2Defs.XferFunc709, V4l2Defs.YcbcrEnc709)]
    [InlineData(V4l2Defs.ColorspaceBt2020, V4l2Defs.XferFunc709, V4l2Defs.YcbcrEncBt2020)]
    [InlineData(V4l2Defs.ColorspaceSmpte170M, V4l2Defs.XferFunc709, V4l2Defs.YcbcrEnc601)]
    [InlineData(V4l2Defs.ColorspaceDciP3, V4l2Defs.XferFuncDciP3, V4l2Defs.YcbcrEnc709)]
    public void Default_fields_map_as_videodev2_defines(uint colorspace, uint xfer, uint encoding)
    {
        Assert.Equal(xfer, CaptureColorMapping.DefaultXfer(colorspace));
        Assert.Equal(encoding, CaptureColorMapping.DefaultEncoding(colorspace));
    }

    /// <summary>The cadence's Low threshold per mode: about twice the largest true duplicate measured in it.</summary>
    [Theory]
    [InlineData(3840, 2160, CapturePixelFormat.P010, 0.0003)]
    [InlineData(2560, 1440, CapturePixelFormat.P010, 0.0004)]
    public void Cadence_thresholds_are_chosen_per_mode(int width, int height, CapturePixelFormat format, double pqLow)
    {
        var f = new NegotiatedFormat
        {
            Requested = default,
            Format = format,
            Width = width,
            Height = height,
            BytesPerLine = width * 2,
            SizeImage = width * height * 3,
            Interval = new V4l2Interval(1, 30),
        };
        var o = CaptureSourceOptions.CadenceFor(f);
        Assert.Equal(pqLow, o.Pq.Low);
        Assert.Equal(0.0050, o.Pq.High);
        Assert.Equal(0.0016, o.Sdr.Low);
        Assert.Equal(0.0050, o.Sdr.High);
    }

    [Fact]
    public void Presets_and_options_parse()
    {
        var o = CaptureOptions.Parse(["--mode", "4k30-hdr"]);
        Assert.Null(o.Device);
        Assert.Equal(new CaptureRequest { Width = 3840, Height = 2160, Fps = 30000.0 / 1001, FallbackFps = 30, Format = CapturePixelFormat.P010 }, o.Request); // the console's 29.97, else 30
        Assert.Equal(HdrMode.Follow, o.Color.Hdr);

        o = CaptureOptions.Parse(["/dev/video0", "--mode", "1440p60-hdr", "--multiplier", "2", "--duration-s", "5", "--offscreen"]);
        Assert.Equal("/dev/video0", o.Device);
        Assert.Equal(60000.0 / 1001, o.Request.Fps);
        Assert.True(o.Offscreen);

        o = CaptureOptions.Parse(["--size", "2560x1440", "--fps", "120", "--format", "nv12", "--hdr", "off", "--range", "full", "--matrix", "bt709", "--no-cadence"]);
        Assert.Equal(CapturePixelFormat.Nv12, o.Request.Format);
        Assert.Equal(HdrMode.Off, o.Color.Hdr);
        Assert.Equal(ColorRange.Full, o.Color.Range);
        Assert.Equal(MatrixCoefficients.Bt709, o.Color.Matrix);
        Assert.False(o.Cadence);

        // The default: the largest 10-bit mode, following the console between HDR10 and SDR.
        o = CaptureOptions.Parse([]);
        Assert.Equal(new CaptureRequest(), o.Request);
        Assert.Equal(HdrMode.Follow, o.Color.Hdr);
        Assert.True(CaptureOptions.Parse(["--list"]).List);
    }

    /// <summary>
    /// Capture is fullscreen unless asked otherwise (Phase 6): there KWin gives the window variable refresh and --clock
    /// auto uses it, the lowest latency measured; --windowed or a --window size keeps a window, --offscreen has none.
    /// </summary>
    [Theory]
    [InlineData(new[] { "--mode", "4k30-hdr" }, true)]
    [InlineData(new[] { "--mode", "1440p60-hdr" }, true)]
    [InlineData(new[] { "--mode", "4k30-hdr", "--fullscreen" }, true)]
    [InlineData(new[] { "--mode", "4k30-hdr", "--windowed" }, false)]
    [InlineData(new[] { "--mode", "4k30-hdr", "--window", "960x540" }, false)]
    [InlineData(new[] { "--mode", "4k30-hdr", "--offscreen" }, false)]
    public void Capture_is_fullscreen_unless_a_window_is_asked_for(string[] args, bool fullscreen)
    {
        var o = CaptureOptions.Parse(args);
        Assert.Equal(fullscreen, o.Fullscreen);
        Assert.Equal(RefreshMode.Auto, o.Refresh);
    }

    [Theory]
    [InlineData(new[] { "--mode", "8k240" }, "--mode")]
    [InlineData(new[] { "--mode", "4k30-hdr", "--fps", "60" }, "not both")]
    [InlineData(new[] { "--format", "mjpeg" }, "MJPEG is never used")]
    [InlineData(new[] { "--format", "rgb" }, "--format")]
    [InlineData(new[] { "--hdr", "maybe" }, "--hdr")]
    [InlineData(new[] { "--range", "tv-ish" }, "--range")]
    [InlineData(new[] { "--buffer-mode", "dmabuf" }, "--buffer-mode")]
    [InlineData(new[] { "--multiplier", "2", "--output-hz", "120" }, "not both")]
    [InlineData(new[] { "--display-hz", "60" }, "only applies with '--offscreen'")]
    [InlineData(new[] { "--offscreen", "--fullscreen" }, "do not apply with '--offscreen'")]
    [InlineData(new[] { "--fullscreen", "--windowed" }, "contradict")]
    [InlineData(new[] { "--size", "4kx2k" }, "--size")]
    [InlineData(new[] { "--bogus", "1" }, "Unknown option")]
    public void Bad_options_are_usage_errors(string[] args, string message)
    {
        var e = Assert.Throws<UsageException>(() => CaptureOptions.Parse(args));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_help_describes_capture()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(CommandDispatcher.ExitOk, CommandDispatcher.Run(["--help"], output, error));
        var help = output.ToString();
        Assert.Contains("fmp capture [device] [options]", help, StringComparison.Ordinal);
        Assert.Contains("4k30-hdr", help, StringComparison.Ordinal);
        Assert.Contains("1440p60-hdr", help, StringComparison.Ordinal);
        Assert.Contains("--hdr <mode>", help, StringComparison.Ordinal);
        Assert.Contains("follow (a P010", help, StringComparison.Ordinal);
        Assert.Contains("H switches between SDR and HDR10 by hand", help, StringComparison.Ordinal);
        Assert.Contains("--hdr-peak-nits <n>", help, StringComparison.Ordinal);
        Assert.Contains("fmp hdr-pattern", help, StringComparison.Ordinal);
        Log.WriteLine(help);
    }
}
