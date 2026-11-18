using System.Diagnostics;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Diagnostics;
using FrameMotionPlus.Vulkan.Tests.Support;

namespace FrameMotionPlus.Vulkan.Tests;

/// <summary>
/// Phase 7 at the presenter, offscreen: PQ code values reach an HDR10 target untouched, scaling filters PQ content in linear
/// light (checked against a CPU reference, and against what averaging code values would have shown), SDR content stays on an
/// SDR target, the HDR metadata handed to Vulkan is the content's, and a presenter following frame colours rebuilds its
/// output between SDR and HDR10 and back, repeatedly, without refusing for long or leaking.
/// </summary>
public class HdrPresentTests
{
    private static readonly int[] PeakPatchNits = [100, 203, 400, 600, 1000, 2000, 4000, 10000];

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Every_pq_code_reaches_the_hdr10_target_unchanged() => Gpu.Run(ctx =>
    {
        // 1024 columns: every 10-bit code in R, G and B (G and B offset, so the channels are not all equal).
        const int W = 1024, H = 8;
        using var rig = OffscreenRig.Create(ctx, W, H, PixelFormat.Rgb10A2, outputColor: ColorMetadata.Hdr10);
        Assert.Equal(TargetColorSpace.Hdr10St2084, rig.Target.ColorSpace);
        Assert.Equal(OutputTransform.Passthrough, rig.Presenter.ActiveTransform);
        Assert.True(rig.Presenter.IsScalingExact);
        var pixels = Ring(rig, (x, _) => (x, (x + 341) % 1024, (x + 682) % 1024));
        rig.PresentPixels(pixels);
        var output = rig.ReadPixels();
        var wrong = 0;
        for (var x = 0; x < W; x++)
        {
            var (r, g, b, a) = TestPatterns.Unpack(output[(4 * W) + x], PixelFormat.Rgb10A2);
            wrong += r == x && g == (x + 341) % 1024 && b == (x + 682) % 1024 && a == 3 ? 0 : 1;
        }

        Assert.Equal(0, wrong);
    });

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(256, 144, 1024, 576)]   // 4x up
    [InlineData(320, 180, 480, 270)]    // 1.5x up
    [InlineData(400, 300, 1000, 600)]   // 2.5x up, pillarboxed
    [InlineData(1280, 720, 640, 360)]   // 2x down
    public void Pq_content_is_scaled_in_linear_light(int ringW, int ringH, int targetW, int targetH) => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, ringW, ringH, PixelFormat.Rgb10A2, new OffscreenTargetOptions { Width = targetW, Height = targetH }, outputColor: ColorMetadata.Hdr10);
        Assert.False(rig.Presenter.IsScalingExact);

        // Hard edges between black (0 nits) and 1000 nits every 7 columns, and between 0.1 and 4000 nits every 5 rows:
        // the worst case for filtering code values, which darkens a bright edge (0 and 1000 nits average to about 39 nits).
        var dark = Code(0);
        var bright = Code(1000);
        var lo = Code(0.1);
        var hi = Code(4000);
        int Ch(int x, int y, int c) => c switch
        {
            0 => (x / 7) % 2 == 0 ? dark : bright,
            1 => (y / 5) % 2 == 0 ? lo : hi,
            _ => ((x / 7) + (y / 5)) % 2 == 0 ? bright : lo,
        };
        rig.PresentPixels(Ring(rig, (x, y) => (Ch(x, y, 0), Ch(x, y, 1), Ch(x, y, 2))));
        var output = rig.ReadPixels();
        var rect = ContentRect.Fit(ringW, ringH, targetW, targetH);
        var worst = 0.0;
        var codeAverageWorst = 0.0;
        var outside = 0;
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            for (var x = rect.X; x < rect.X + rect.Width; x++)
            {
                var (r, g, b, _) = TestPatterns.Unpack(output[(y * targetW) + x], PixelFormat.Rgb10A2);
                var got = new[] { r, g, b };
                for (var c = 0; c < 3; c++)
                {
                    var (linear, average, min, max) = Reference(ringW, ringH, rect, x, y, (px, py) => Ch(px, py, c));
                    worst = Math.Max(worst, Math.Abs(got[c] - linear));
                    codeAverageWorst = Math.Max(codeAverageWorst, Math.Abs(average - linear));

                    // No ringing: a bilinear result stays within the codes it was made from.
                    outside += got[c] < min - 1 || got[c] > max + 1 ? 1 : 0;
                }
            }
        }

        Gpu.WriteLine($"{ringW}x{ringH} -> {rect.Width}x{rect.Height}: worst error against the linear-light reference {worst:F2} codes; averaging code values instead would be up to {codeAverageWorst:F0} codes off");
        Assert.True(worst <= 1.0, $"scaled PQ is {worst:F2} codes from the linear-light reference");
        Assert.Equal(0, outside);
        Assert.True(codeAverageWorst > 100, "the pattern should tell linear light from code values apart");

        // The bars stay at code 0: true black in PQ.
        if (rect.X > 0)
        {
            Assert.Equal(0u, output[(targetH / 2 * targetW) + (rect.X / 2)] & 0x3FFFFFFF);
        }
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_bright_edge_keeps_its_light_when_scaled() => Gpu.Run(ctx =>
    {
        // Left half 0 nits, right half 1000 nits, upscaled 4x: the pixel straddling the edge shows about half the light.
        using var rig = OffscreenRig.Create(ctx, 64, 16, PixelFormat.Rgb10A2, new OffscreenTargetOptions { Width = 256, Height = 64 }, outputColor: ColorMetadata.Hdr10);
        var bright = Code(1000);
        rig.PresentPixels(Ring(rig, (x, _) => x < 32 ? (0, 0, 0) : (bright, bright, bright)));
        var output = rig.ReadPixels();
        var row = new double[256];
        for (var x = 0; x < 256; x++)
        {
            row[x] = OffscreenPresentTests.PqToNits(TestPatterns.Unpack(output[(32 * 256) + x], PixelFormat.Rgb10A2).R / 1023.0);
        }

        // Target pixels 127 and 128 sample source x 31.375 and 31.625: 3/8 and 5/8 of the way from black to 1000 nits.
        Gpu.WriteLine($"nits across the edge: {string.Join(", ", row[124..132].Select(v => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))}");
        Assert.InRange(row[127], 1000 * (0.375 - 0.02), 1000 * (0.375 + 0.02));
        Assert.InRange(row[128], 1000 * (0.625 - 0.02), 1000 * (0.625 + 0.02));
        for (var x = 1; x < 256; x++)
        {
            Assert.True(row[x] >= row[x - 1] - 0.01, $"not monotonic at {x}: {row[x - 1]:0.###} then {row[x]:0.###} nits");
        }
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Sdr_content_stays_sdr_at_eight_and_ten_bits() => Gpu.Run(ctx =>
    {
        using (var rig = OffscreenRig.Create(ctx, 256, 4, PixelFormat.Rgba8, outputColor: ColorMetadata.SdrBt709))
        {
            Assert.Equal(TargetColorSpace.Sdr, rig.Target.ColorSpace);
            Assert.Equal(OutputTransform.Passthrough, rig.Presenter.ActiveTransform);
            Assert.Null(rig.Target.HdrMetadataSent);
            var pixels = new byte[rig.Ring.ContentSizeBytes];
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 256; x++)
                {
                    BitConverter.TryWriteBytes(pixels.AsSpan((y * rig.Ring.PitchBytes) + (x * 4)), TestPatterns.PackRgba8(x, 255 - x, x / 2, 255));
                }
            }

            rig.PresentPixels(pixels);
            var output = rig.ReadPixels();
            for (var x = 0; x < 256; x++)
            {
                Assert.Equal(TestPatterns.PackRgba8(x, 255 - x, x / 2, 255), output[256 + x]);
            }
        }

        // 10-bit SDR (a P010 capture of an SDR signal): an SDR image, every code as it was.
        using (var rig = OffscreenRig.Create(ctx, 1024, 4, PixelFormat.Rgb10A2, new OffscreenTargetOptions { Width = 1024, Height = 4, SdrFormat = PixelFormat.Rgb10A2 }, outputColor: ColorMetadata.SdrBt709))
        {
            Assert.Equal(TargetColorSpace.Sdr, rig.Target.ColorSpace);
            Assert.Equal(OutputTransform.Passthrough, rig.Presenter.ActiveTransform);
            rig.PresentPixels(Ring(rig, (x, _) => (x, 1023 - x, x / 2)));
            var output = rig.ReadPixels();
            for (var x = 0; x < 1024; x++)
            {
                Assert.Equal(TestPatterns.PackRgb10A2(x, 1023 - x, x / 2, 3), output[1024 + x]);
            }
        }
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void The_hdr_metadata_handed_over_is_the_contents() => Gpu.Run(ctx =>
    {
        // A source with no metadata of its own: BT.2020 primaries, D65, the stated peak (a capture's --hdr-peak-nits).
        using var rig = OffscreenRig.Create(ctx, 64, 32, PixelFormat.Rgb10A2, outputColor: ColorMetadata.Hdr10WithPeak(800, 650, 300));
        var sent = rig.Target.HdrMetadataSent ?? throw new InvalidOperationException("no metadata was handed over");
        Gpu.WriteLine($"sent: {sent}");
        Assert.Equal(new Chromaticity(0.708f, 0.292f), sent.Red);
        Assert.Equal(new Chromaticity(0.170f, 0.797f), sent.Green);
        Assert.Equal(new Chromaticity(0.131f, 0.046f), sent.Blue);
        Assert.Equal(new Chromaticity(0.3127f, 0.3290f), sent.WhitePoint);
        Assert.Equal(800f, sent.MaxLuminanceNits);
        Assert.Equal(0.0001f, sent.MinLuminanceNits);
        Assert.Equal(650f, sent.MaxContentLightLevel);
        Assert.Equal(300f, sent.MaxFrameAverageLightLevel);
        Assert.Equal("BT.2020 primaries, D65 white; mastering 0.0001 to 800 nits; MaxCLL 650, MaxFALL 300", sent.ToString());

        // A file's mastering display (P3, 4000 nits) and light levels go over as they are; a change of metadata alone is
        // handed over without rebuilding the images.
        var file = ColorMetadata.Hdr10 with { Mastering = MasteringDisplay.P3D65Default with { MaxLuminanceNits = 4000 }, LightLevel = new ContentLightLevel(3500, 420) };
        var images = rig.Target.Images[0];
        rig.Configure(64, 32, PixelFormat.Rgb10A2, file);
        Assert.Equal(images, rig.Target.Images[0]);
        Assert.Equal(1, rig.Presenter.Stats.MetadataUpdates);
        sent = rig.Target.HdrMetadataSent!.Value;
        Assert.Equal("P3 primaries, D65 white; mastering 0.0001 to 4000 nits; MaxCLL 3500, MaxFALL 420", sent.ToString());

        // No metadata at all in the content: the defaults (1000 nits); SDR: none.
        rig.Configure(64, 32, PixelFormat.Rgb10A2, ColorMetadata.Hdr10 with { Mastering = default, LightLevel = default });
        Assert.Equal("BT.2020 primaries, D65 white; mastering 0.0001 to 1000 nits; MaxCLL not stated, MaxFALL not stated", rig.Target.HdrMetadataSent!.Value.ToString());
        rig.Configure(64, 32, PixelFormat.Rgb10A2, ColorMetadata.SdrBt709);
        Assert.Null(rig.Target.HdrMetadataSent);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Following_frame_colours_switches_the_output_between_sdr_and_hdr10_and_back() => Gpu.Run(ctx =>
    {
        // As a P010 capture that the console switches (or the owner, by hand): the ring stays RGB10A2 and the frames say
        // which colour they are; the output follows at the first frame in the new colour.
        using var rig = OffscreenRig.Create(
            ctx, 320, 180, PixelFormat.Rgb10A2,
            new OffscreenTargetOptions { Width = 320, Height = 180, SdrFormat = PixelFormat.Rgb10A2 },
            new VulkanPresenterOptions { FollowFrameColor = true },
            ColorMetadata.Hdr10);
        Assert.Equal(TargetColorSpace.Hdr10St2084, rig.Target.ColorSpace);
        var hdr = ColorMetadata.Hdr10WithPeak(1000, 1000, 0);
        var sdr = ColorMetadata.SdrBt709;
        var refusals = 0;
        var watch = new Stopwatch();
        var worstSwitchMs = 0.0;
        for (var i = 0; i < 12; i++)
        {
            var color = i % 2 == 0 ? sdr : hdr;
            var code = 100 + (i * 70);
            var n = rig.Producer.WritePixels(Ring(rig, (x, _) => (code, code, x % 1024)));
            watch.Restart();
            var request = rig.Request(n, color);
            while (!rig.Presenter.TryPresent(request))
            {
                refusals++;
                Assert.True(watch.ElapsedMilliseconds < 2000, $"switch {i} was refused for 2 s");
                Thread.Sleep(1);
            }

            worstSwitchMs = Math.Max(worstSwitchMs, watch.Elapsed.TotalMilliseconds);
            Assert.Equal(color.IsPq ? TargetColorSpace.Hdr10St2084 : TargetColorSpace.Sdr, rig.Target.ColorSpace);
            Assert.Equal(OutputTransform.Passthrough, rig.Presenter.ActiveTransform);
            Assert.Equal(color.IsPq, rig.Presenter.ContentColor.IsPq);
            Assert.Equal(color.IsPq, rig.Target.HdrMetadataSent is not null);
            var output = rig.ReadPixels();
            Assert.Equal(TestPatterns.PackRgb10A2(code, code, 77, 3), output[(90 * 320) + 77]);

            // A few more frames in the same colour: no rebuild.
            for (var k = 0; k < 3; k++)
            {
                rig.WaitReady();
                var m = rig.Producer.WritePixels(Ring(rig, (x, _) => (code, code, x % 1024)));
                Assert.True(rig.Presenter.TryPresent(rig.Request(m, color)));
            }
        }

        var stats = rig.Presenter.Stats;
        var last = rig.Presenter.LastOutputColorChange;
        Gpu.WriteLine($"12 switches: {stats.OutputColorChanges} output rebuilds, {refusals} refused requests while frames in flight finished, worst {worstSwitchMs:F1} ms " +
            $"from the first request to the frame accepted; last rebuild waited {last.WaitNs / 1e6:F2} ms and took {last.RebuildNs / 1e6:F2} ms");
        Assert.Equal(12, stats.OutputColorChanges);
        Assert.Equal(12, stats.TargetRecreations);
        Assert.Equal(0, stats.RejectedRequests);
        Assert.True(rig.Presenter.WaitIdle(1_000_000_000));

        // A frame that does not say what colour it is changes nothing.
        rig.WaitReady();
        var u = rig.Producer.WritePixels(Ring(rig, (_, _) => (5, 5, 5)));
        Assert.True(rig.Presenter.TryPresent(rig.Request(u)));
        Assert.Equal(12, rig.Presenter.Stats.OutputColorChanges);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Peak_patches_keep_their_codes_on_an_hdr10_target() => Gpu.Run(ctx =>
    {
        // The on-screen peak pattern's patches (100 to 10,000 nits) and near-black steps, through the presenter unchanged.
        var codes = PeakPatchNits.Select(n => Code(n)).Concat([0, 1, 2, 4, 8, 16, 32, 64]).ToArray();
        using var rig = OffscreenRig.Create(ctx, codes.Length * 8, 8, PixelFormat.Rgb10A2, outputColor: ColorMetadata.Hdr10);
        rig.PresentPixels(Ring(rig, (x, _) => (codes[x / 8], codes[x / 8], codes[x / 8])));
        var output = rig.ReadPixels();
        for (var i = 0; i < codes.Length; i++)
        {
            Assert.Equal(TestPatterns.PackRgb10A2(codes[i], codes[i], codes[i], 3), output[(4 * codes.Length * 8) + (i * 8) + 4]);
        }
    });

    private static int Code(double nits) => (int)Math.Round(OffscreenPresentTests.NitsToPq(nits) * 1023);

    private static byte[] Ring(OffscreenRig rig, Func<int, int, (int R, int G, int B)> pixel)
    {
        var bytes = new byte[rig.Ring.ContentSizeBytes];
        for (var y = 0; y < rig.Ring.Height; y++)
        {
            for (var x = 0; x < rig.Ring.Width; x++)
            {
                var (r, g, b) = pixel(x, y);
                BitConverter.TryWriteBytes(bytes.AsSpan((y * rig.Ring.PitchBytes) + (x * 4)), TestPatterns.PackRgb10A2(r, g, b, 3));
            }
        }

        return bytes;
    }

    /// <summary>
    /// The presenter's bilinear mapping at target pixel (x, y) of one channel: in linear light (the expected code), on code
    /// values (what the Phase 2 presenter did), and the range of the four codes it is made from.
    /// </summary>
    private static (double Linear, double Average, int Min, int Max) Reference(int w, int h, ContentRect rect, int x, int y, Func<int, int, int> code)
    {
        var sx = ((x + 0.5 - rect.X) / rect.Width * w) - 0.5;
        var sy = ((y + 0.5 - rect.Y) / rect.Height * h) - 0.5;
        var x0 = (int)Math.Floor(sx);
        var y0 = (int)Math.Floor(sy);
        var fx = sx - x0;
        var fy = sy - y0;
        int At(int px, int py) => code(Math.Clamp(px, 0, w - 1), Math.Clamp(py, 0, h - 1));
        int[] c = [At(x0, y0), At(x0 + 1, y0), At(x0, y0 + 1), At(x0 + 1, y0 + 1)];
        double[] wgt = [(1 - fx) * (1 - fy), fx * (1 - fy), (1 - fx) * fy, fx * fy];
        double linear = 0, average = 0;
        for (var i = 0; i < 4; i++)
        {
            linear += wgt[i] * OffscreenPresentTests.PqToNits(c[i] / 1023.0);
            average += wgt[i] * c[i];
        }

        return (OffscreenPresentTests.NitsToPq(linear) * 1023, average, c.Min(), c.Max());
    }
}
