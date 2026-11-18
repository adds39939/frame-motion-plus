using System.Diagnostics;
using System.Runtime.Versioning;
using FrameMotionPlus.App.Capture;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Pipeline;
using FrameMotionPlus.Sources.V4l2.Interop;
using FrameMotionPlus.Sources.V4l2.Tests.Support;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// Phase 7 on the capture side: telling an 8-bit signal from a 10-bit one in P010 (<see cref="SampleDepthProbe"/>) and
/// following it (<see cref="SampleDepthFollower"/>), the two colours a capture switches between, the HDR metadata options,
/// and a switch asked of a running capture reaching its frames and, through a whole session, the presenter's output.
/// </summary>
public class HdrFollowTests
{
    private const int W = 640;
    private const int H = 360;

    [Fact]
    public void An_8_bit_signal_in_p010_reads_8_bit_and_a_10_bit_one_10_bit()
    {
        var random = new Random(7);

        // 8 bits padded into P010 (the card with an SDR signal): every code a multiple of 4.
        var eight = Plane((x, y) => 64 + (4 * ((x + (3 * y) + random.Next(4)) % 200)));
        Assert.Equal(SampleDepth.EightBit, Judge(eight).Depth);

        // A varied 10-bit picture (an HDR10 signal): about three quarters of the codes use the low bits.
        var ten = Plane((x, y) => 64 + ((x + (3 * y) + random.Next(4)) % 800));
        var reading = Judge(ten);
        Assert.Equal(SampleDepth.TenBit, reading.Depth);
        Assert.InRange(reading.LowBitSamples * 1.0 / reading.Samples, 0.6, 0.9);

        // Nothing to judge: black (limited range), a flat picture, or a picture of few distinct codes.
        Assert.Equal(SampleDepth.Inconclusive, Judge(Plane((_, _) => 64)).Depth);
        Assert.Equal(SampleDepth.Inconclusive, Judge(Plane((_, _) => 500)).Depth);
        Assert.Equal(SampleDepth.Inconclusive, Judge(Plane((x, _) => x < W / 2 ? 400 : 600)).Depth);

        // A mostly black frame with a small bright 10-bit area: too few samples to say.
        Assert.Equal(SampleDepth.Inconclusive, Judge(Plane((x, y) => x < 40 && y < 40 ? 401 + (x % 7) : 64)).Depth);
    }

    [Fact]
    public void The_follower_takes_its_first_reading_then_needs_three_in_a_row()
    {
        var f = new SampleDepthFollower(hdr: true);
        Assert.False(f.Feed(SampleDepth.Inconclusive));
        Assert.True(f.Feed(SampleDepth.EightBit)); // the first conclusive frame decides: SDR at once
        Assert.False(f.IsHdr);
        Assert.False(f.Feed(SampleDepth.TenBit));
        Assert.False(f.Feed(SampleDepth.Inconclusive)); // does not break a run
        Assert.False(f.Feed(SampleDepth.TenBit));
        Assert.True(f.Feed(SampleDepth.TenBit));
        Assert.True(f.IsHdr);
        Assert.False(f.Feed(SampleDepth.EightBit));
        Assert.False(f.Feed(SampleDepth.TenBit)); // a run broken: starts again
        Assert.False(f.Feed(SampleDepth.EightBit));
        Assert.False(f.Feed(SampleDepth.EightBit));
        Assert.True(f.Feed(SampleDepth.EightBit));
        Assert.Equal((4, 5, 2), (f.TenBitFrames, f.EightBitFrames, f.InconclusiveFrames));

        // After a manual choice (reset), the first reading does not decide alone.
        f.Reset(hdr: true);
        Assert.False(f.Feed(SampleDepth.EightBit));
        Assert.True(f.IsHdr);
    }

    [Fact]
    public void Follow_starts_as_hdr10_for_p010_and_both_colours_are_ready()
    {
        var overrides = new CaptureColorOverrides { Hdr = HdrMode.Follow, PeakNits = 800, MaxCll = 650, MaxFall = 250 };
        var c = CaptureColorMapping.Resolve(V4l2Defs.ColorspaceSrgb, V4l2Defs.XferFunc709, V4l2Defs.YcbcrEnc709, V4l2Defs.QuantizationDefault, 2160, 10, overrides);
        Assert.True(c.IsHdr);
        Assert.Contains("following the signal", c.Origin, StringComparison.Ordinal);
        Assert.Equal(800f, c.FrameColor.Mastering.MaxLuminanceNits);
        Assert.Equal(new ContentLightLevel(650, 250), c.FrameColor.LightLevel);
        Assert.Equal(new Chromaticity(0.708f, 0.292f), c.FrameColor.Mastering.Red);

        // 8-bit: as the device says (no depth to follow).
        Assert.False(CaptureColorMapping.Resolve(V4l2Defs.ColorspaceSrgb, V4l2Defs.XferFunc709, V4l2Defs.YcbcrEnc709, V4l2Defs.QuantizationDefault, 2160, 8, overrides).IsHdr);

        var (hdr, sdr) = CaptureColorMapping.ResolveBoth(V4l2Defs.ColorspaceSrgb, V4l2Defs.XferFunc709, V4l2Defs.YcbcrEnc709, V4l2Defs.QuantizationDefault, 2160, 10, overrides);
        Assert.Equal((TransferFunction.Pq, ColorPrimaries.Bt2020, MatrixCoefficients.Bt2020NonConstant), (hdr.FrameColor.Transfer, hdr.FrameColor.Primaries, hdr.Matrix));
        Assert.Equal((TransferFunction.Bt709, ColorPrimaries.Bt709, MatrixCoefficients.Bt709), (sdr.FrameColor.Transfer, sdr.FrameColor.Primaries, sdr.Matrix));
        Assert.Equal(ColorRange.Limited, hdr.Range);
        Assert.Equal(ColorRange.Limited, sdr.Range);
        Assert.Equal("PQ (SMPTE ST 2084), BT.2020 primaries, limited range, 10-bit YCbCr 4:2:0 with the BT.2020 matrix", CaptureColorMapping.SignalDescription(hdr, 10));
        Assert.Equal("SDR gamma (BT.709), BT.709 primaries, limited range, 10-bit YCbCr 4:2:0 with the BT.709 matrix", CaptureColorMapping.SignalDescription(sdr, 10));
    }

    [Fact]
    public void The_hdr_options_parse()
    {
        var o = CaptureOptions.Parse(["--mode", "1440p60-hdr", "--hdr-peak-nits", "800"]);
        Assert.Equal(HdrMode.Follow, o.Color.Hdr);
        Assert.Equal(800, o.Color.PeakNits);
        Assert.Null(o.Color.MaxCll);
        Assert.Equal(0, o.Color.MaxFall);
        o = CaptureOptions.Parse(["--hdr", "on", "--hdr-max-cll", "1200", "--hdr-max-fall", "300"]);
        Assert.Equal(HdrMode.On, o.Color.Hdr);
        Assert.Equal(ColorMetadata.DefaultPeakNits, o.Color.PeakNits);
        Assert.Equal(1200, o.Color.MaxCll);
        Assert.Equal(300, o.Color.MaxFall);
        Assert.Equal(HdrMode.Follow, CaptureOptions.Parse(["--hdr", "follow"]).Color.Hdr);
        Assert.Equal(HdrMode.TenBit, CaptureOptions.Parse(["--hdr", "10bit"]).Color.Hdr);
        Assert.Throws<App.Cli.UsageException>(() => CaptureOptions.Parse(["--hdr-peak-nits", "50"]));
        Assert.Throws<App.Cli.UsageException>(() => CaptureOptions.Parse(["--hdr", "maybe"]));
    }

    /// <summary>
    /// vivid (NV12, SDR): a switch asked of the running capture lands on its next frame: the frames carry the new colour,
    /// the first of them marks a discontinuity and a format change, and the log says by hand; back again the same way.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Capture)]
    [SupportedOSPlatform("linux")]
    public void A_switch_asked_of_a_running_capture_lands_on_its_next_frames()
    {
        var path = CaptureHardware.RequireVivid();
        var context = CaptureHardware.RequireCuda();
        using var source = new V4l2FrameSource(context, Capture.Options(path, W, H, 30, CapturePixelFormat.Nv12) with { Color = new CaptureColorOverrides { Hdr = HdrMode.Off } });
        Assert.False(source.IsHdrNow);
        Assert.False(source.IsFollowing); // 8-bit: nothing to follow
        source.Start();
        try
        {
            Expect(source, hdr: false, frames: 3);
            source.RequestColor(CaptureColorRequest.Toggle);
            var first = Expect(source, hdr: true, frames: 3);
            Assert.True((first.Marks & SourceFrameMarks.Discontinuity) != 0);
            Assert.True((first.Marks & SourceFrameMarks.FormatChanged) != 0);
            Assert.True(source.IsHdrNow);
            source.RequestColor(CaptureColorRequest.Sdr);
            Expect(source, hdr: false, frames: 3);
            Assert.Equal(2, source.ColorSwitches);
            Assert.All(source.SwitchLog.ToArray(), w => Assert.Equal(ColorSwitchCause.Manual, w.Cause));
            Assert.Equal(0, source.Statistics.DequeueThreadAllocatedBytes);
        }
        finally
        {
            source.Stop();
        }
    }

    /// <summary>
    /// A whole capture session (vivid, offscreen, blend 2x): switched to HDR10 and back to SDR while it plays, the
    /// presenter's output follows each switch (rebuilt in the other colour space), and the run stays clean.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Capture)]
    [SupportedOSPlatform("linux")]
    public void A_capture_session_switched_while_playing_rebuilds_its_output_each_time()
    {
        var path = CaptureHardware.RequireVivid();
        var cuda = CaptureHardware.RequireCuda();
        var options = new PlaybackOptions
        {
            Path = path,
            Capture = new CaptureSourceOptions
            {
                Device = path,
                Request = new CaptureRequest { Width = 320, Height = 180, Fps = 60, Format = CapturePixelFormat.Nv12 },
                Color = new CaptureColorOverrides { Hdr = HdrMode.Off },
            },
            Generator = GeneratorKind.Blend,
            Display = DisplayKind.Offscreen,
            Clock = PlaybackClock.Real,
            OffscreenRate = FrameRate.FromHz(120),
            DurationNs = 5 * Nanoseconds.PerSecond,
            WarmupNs = 500 * Nanoseconds.PerMillisecond,
        };
        PlaybackReport report;
        using (var session = new PlaybackSession(options, cuda))
        {
            try
            {
                session.Open();
            }
            catch (PlaybackUnavailableException e)
            {
                Assert.Skip(e.Message);
            }

            // Each request once the capture is publishing, and the next once the last has landed (requests made before a
            // frame comes collapse into the last one, as two quick presses of H would).
            var source = ((CapturePlaybackSource)session.PlaybackSource!).Source;
            var switcher = new Thread(() =>
            {
                var watch = Stopwatch.StartNew();
                while (source.Statistics.Published < 20 && watch.Elapsed < TimeSpan.FromSeconds(3))
                {
                    Thread.Sleep(10);
                }

                var expected = 0L;
                foreach (var request in (CaptureColorRequest[])[CaptureColorRequest.Toggle, CaptureColorRequest.Toggle, CaptureColorRequest.Hdr, CaptureColorRequest.Sdr])
                {
                    _ = session.RequestCaptureColor(request);
                    expected++;
                    while (source.ColorSwitches < expected && watch.Elapsed < TimeSpan.FromSeconds(4))
                    {
                        Thread.Sleep(5);
                    }

                    Thread.Sleep(300);
                }
            }) { IsBackground = true };
            switcher.Start();
            report = session.Run();
            switcher.Join();
        }

        var capture = report.Capture!;
        Log.WriteLine($"{report.StopReason}: {capture.ColorSwitches} capture switches, {report.PresenterStats.OutputColorChanges} output rebuilds; last: waited " +
            $"{report.LastOutputColorChange.WaitNs / 1e6:0.00} ms, rebuilt in {report.LastOutputColorChange.RebuildNs / 1e6:0.00} ms; output at the end: {report.OutputAtEnd}");
        Assert.Null(report.Failure);
        Assert.Equal(4, capture.ColorSwitches);
        Assert.Equal(4, report.PresenterStats.OutputColorChanges);
        Assert.False(capture.HdrAtEnd);
        Assert.StartsWith("SDR output for SDR content", report.OutputAtEnd, StringComparison.Ordinal);
        Assert.Equal(0, capture.Counters.DequeueThreadAllocatedBytes);
        Assert.Equal(0, report.PresenterStats.RejectedRequests);
    }

    [SupportedOSPlatform("linux")]
    private static SourceFrame Expect(V4l2FrameSource source, bool hdr, int frames)
    {
        SourceFrame first = default;
        for (var i = 0; i < frames; i++)
        {
            var f = Capture.Next(source);
            source.Release(f.FrameId, GpuSignal.None);
            if (i == 0)
            {
                first = f;
            }

            Assert.Equal(hdr, f.Frame.Color.IsPq);
            Assert.Equal(hdr ? ColorPrimaries.Bt2020 : ColorPrimaries.Bt709, f.Frame.Color.Primaries);
        }

        return first;
    }

    private static byte[] Plane(Func<int, int, int> code)
    {
        var bytes = new byte[W * H * 2];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(((y * W) + x) * 2), (ushort)(Math.Clamp(code(x, y), 0, 1023) << 6));
            }
        }

        return bytes;
    }

    private static unsafe SampleDepthReading Judge(byte[] plane)
    {
        fixed (byte* p = plane)
        {
            return SampleDepthProbe.Judge(p, W * 2, W, H, 64);
        }
    }
}
