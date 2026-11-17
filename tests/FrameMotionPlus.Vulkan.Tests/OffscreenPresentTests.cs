using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Diagnostics;
using FrameMotionPlus.Vulkan.Tests.Support;

namespace FrameMotionPlus.Vulkan.Tests;

public class OffscreenPresentTests
{
    private const long Second = 1_000_000_000;

    public static TheoryData<PixelFormat, int, int> OneToOne => new()
    {
        { PixelFormat.Rgba8, 640, 360 },
        { PixelFormat.Rgba8, 1277, 719 },
        { PixelFormat.Rgb10A2, 640, 360 },
        { PixelFormat.Rgb10A2, 1277, 719 },
    };

    public static TheoryData<PixelFormat, int, int, int, int> Scaled => new()
    {
        { PixelFormat.Rgba8, 1280, 720, 1000, 1000 },  // letterbox
        { PixelFormat.Rgba8, 720, 1280, 1000, 600 },   // pillarbox
        { PixelFormat.Rgba8, 960, 540, 1920, 1080 },   // 2x up, no bars
        { PixelFormat.Rgb10A2, 1280, 720, 1000, 1000 },
        { PixelFormat.Rgb10A2, 720, 1280, 1000, 600 },
        { PixelFormat.Rgb10A2, 1920, 1080, 1280, 720 }, // down-scale
    };

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [MemberData(nameof(OneToOne))]
    public void One_to_one_present_is_bit_exact(PixelFormat format, int width, int height) => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, width, height, format);
        var expectedSpace = format == PixelFormat.Rgb10A2 ? TargetColorSpace.Hdr10St2084 : TargetColorSpace.Sdr;
        Assert.Equal(expectedSpace, rig.Target.ColorSpace);
        Assert.Equal(OutputTransform.Passthrough, rig.Presenter.ActiveTransform);
        Assert.Equal(format, rig.Target.PixelFormat);

        for (var frame = 0; frame < 3; frame++)
        {
            rig.WriteAndPresent(TestPattern.Ramp, frame);
            var pixels = rig.ReadPixels();
            var mismatches = 0;
            var first = string.Empty;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var expected = TestPatterns.Pixel(TestPattern.Ramp, format, x, y, width, height, frame);
                    var actual = pixels[(y * width) + x];
                    if (expected != actual && mismatches++ == 0)
                    {
                        first = $"({x},{y}) expected {expected:X8} got {actual:X8}";
                    }
                }
            }

            Assert.True(mismatches == 0, $"frame {frame}: {mismatches} pixels differ, first {first}");
        }
    });

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [MemberData(nameof(Scaled))]
    public void Scaled_present_keeps_aspect_with_black_bars(PixelFormat format, int ringWidth, int ringHeight, int targetWidth, int targetHeight) => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, ringWidth, ringHeight, format, new OffscreenTargetOptions { Width = targetWidth, Height = targetHeight });
        rig.WriteAndPresent(TestPattern.Smooth, 0);
        var pixels = rig.ReadPixels();
        var rect = ContentRect.Fit(ringWidth, ringHeight, targetWidth, targetHeight);
        Gpu.WriteLine($"content rect {rect}");
        var max = format == PixelFormat.Rgb10A2 ? 1023 : 255;
        var tolerance = format == PixelFormat.Rgb10A2 ? 4 : 2;
        var worst = 0.0;
        for (var y = 0; y < targetHeight; y++)
        {
            for (var x = 0; x < targetWidth; x++)
            {
                var (r, g, b, a) = TestPatterns.Unpack(pixels[(y * targetWidth) + x], format);
                Assert.Equal(format == PixelFormat.Rgb10A2 ? 3 : 255, a);
                var inside = x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;
                if (!inside)
                {
                    Assert.True(r == 0 && g == 0 && b == 0, $"bar pixel ({x},{y}) is ({r},{g},{b}), not black");
                    continue;
                }

                var expected = Bilinear(format, ringWidth, ringHeight, rect, x, y);
                worst = Math.Max(worst, Math.Max(Math.Abs(r - expected.R), Math.Max(Math.Abs(g - expected.G), Math.Abs(b - expected.B))));
                Assert.True(r >= max / 8 - tolerance && g >= max / 8 - tolerance, $"content pixel ({x},{y}) looks like a bar");
            }
        }

        Gpu.WriteLine($"worst channel error against the CPU bilinear reference: {worst:F2} codes");
        Assert.True(worst <= tolerance, $"scaled output is {worst:F2} codes from the bilinear reference (tolerance {tolerance})");
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Ten_bit_ring_on_an_sdr_target_uses_the_labelled_placeholder() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 64, 32, PixelFormat.Rgb10A2, new OffscreenTargetOptions { Width = 64, Height = 32, Hdr10Capable = false });
        Assert.Equal(TargetColorSpace.Sdr, rig.Target.ColorSpace);
        Assert.Equal(OutputTransform.PqToSdrPlaceholder, rig.Presenter.ActiveTransform);
        Assert.Equal(PixelFormat.Rgba8, rig.Target.PixelFormat);

        // Columns of PQ codes: black, ~50 nits, 203 nits (SDR white), 1000 nits (clipped).
        int[] codes = [0, Pq(50), Pq(203), Pq(1000)];
        var pitch = rig.Ring.PitchBytes;
        var pixels = new byte[rig.Ring.ContentSizeBytes];
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var c = codes[x / 16];
                BitConverter.TryWriteBytes(pixels.AsSpan((y * pitch) + (x * 4)), TestPatterns.PackRgb10A2(c, c, c, 3));
            }
        }

        rig.PresentPixels(pixels);
        var output = rig.ReadPixels();
        for (var i = 0; i < codes.Length; i++)
        {
            var expected = (int)Math.Round(255 * SrgbEncode(Math.Clamp(PqToNits(codes[i] / 1023.0) / 203.0, 0, 1)));
            var (r, g, b, _) = TestPatterns.Unpack(output[(16 * 64) + (i * 16) + 8], PixelFormat.Rgba8);
            Gpu.WriteLine($"PQ code {codes[i]} -> {r} (expected {expected})");
            Assert.InRange(r, expected - 1, expected + 1);
            Assert.Equal(r, g);
            Assert.Equal(r, b);
        }

        Assert.Equal(255, TestPatterns.Unpack(output[(16 * 64) + 40], PixelFormat.Rgba8).R);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Many_frames_reuse_every_slot_and_honour_release() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 320, 180, PixelFormat.Rgba8);
        const int Frames = 300;
        var feedback = 0;
        var lastSlot = -1L;
        void DrainFeedback()
        {
            while (rig.Presenter.TryGetFeedback(out var f))
            {
                Assert.True(f.SlotIndex > lastSlot);
                Assert.Equal(0, f.ActualTimeNs);
                lastSlot = f.SlotIndex;
                feedback++;
            }
        }

        for (var frame = 1; frame <= Frames; frame++)
        {
            var n = rig.WriteAndPresent(TestPattern.Ramp, frame);
            Assert.Equal((ulong)frame, n);
            DrainFeedback();
            if (frame % 25 == 0)
            {
                var pixels = rig.ReadPixels();
                Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 100, 50, 320, 180, frame), pixels[(50 * 320) + 100]);
                Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 319, 179, 320, 180, frame), pixels[^1]);
            }
        }

        Assert.True(rig.Presenter.WaitIdle(Second));
        var stats = rig.Presenter.Stats;
        Gpu.WriteLine(stats.ToString());
        Assert.Equal(Frames, stats.Presented);
        Assert.Equal(0, stats.Dropped);
        Assert.InRange(stats.MaxPresentsInFlight, 1, 2);
        Assert.Equal((ulong)Frames, rig.Producer.ReadReleasedValue());
        Assert.Equal((ulong)Frames, rig.Producer.ReadWrittenValue());
        Assert.Equal((ulong)Frames, rig.Ring.ReadReleasedValue());

        DrainFeedback();
        Assert.Equal(Frames, feedback);
        Assert.Equal(0, rig.Presenter.Stats.FeedbackOverflows);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Repeats_use_the_staged_copy_and_stale_writes_are_refused() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgb10A2);
        var n1 = rig.WriteAndPresent(TestPattern.Ramp, 1);
        rig.WaitReady();
        Assert.True(rig.Presenter.TryPresent(rig.Request(n1)), "repeat of the last write");
        Assert.Equal(1, rig.Presenter.Stats.Repeats);
        Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgb10A2, 7, 7, 160, 90, 1), rig.ReadPixels()[(7 * 160) + 7]);

        var n2 = rig.WriteAndPresent(TestPattern.Ramp, 2);

        // Ready may be left empty: the frame's own Ready (set by ConsumerFrame) is used.
        rig.WaitReady();
        Assert.True(rig.Presenter.TryPresent(rig.Request(n2) with { Ready = GpuSignal.None }));
        Assert.Equal(2, rig.Presenter.Stats.Repeats);
        rig.WaitReady();
        Assert.False(rig.Presenter.TryPresent(rig.Request(n1)), "an older write may already be overwritten");
        Assert.Equal(1, rig.Presenter.Stats.StaleRequests);
        Assert.Equal(n2, rig.Presenter.LastPresentedWrite);

        // Requests that do not name the ring's slot and timeline are programming errors.
        var wrongSlot = rig.Request(n2 + 1) with { Frame = rig.Ring.ConsumerFrame(n2) };
        Assert.Throws<ArgumentException>(() => rig.Presenter.TryPresent(wrongSlot));
        var wrongSignal = rig.Request(n2 + 1) with { Ready = GpuSignal.Timeline(MemoryDomain.Vulkan, 1234, n2 + 1) };
        Assert.Throws<ArgumentException>(() => rig.Presenter.TryPresent(wrongSignal));
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Present_never_blocks_and_a_refused_request_keeps_only_its_own_write() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgba8);

        // Nothing is written yet: frames 1 and 2 wait on the GPU for 'written', and the CPU does not wait at all.
        var watch = Stopwatch.StartNew();
        Assert.True(rig.Presenter.TryPresent(rig.Request(1)));
        Assert.True(rig.Presenter.TryPresent(rig.Request(2)));
        Assert.False(rig.Presenter.IsReadyForPresent());
        Assert.False(rig.Presenter.TryPresent(rig.Request(3)), "a third frame in flight must be refused");
        watch.Stop();
        Gpu.WriteLine($"three TryPresent calls with nothing written took {watch.Elapsed.TotalMilliseconds:F3} ms");
        Assert.True(watch.Elapsed.TotalMilliseconds < 50);
        Assert.Equal(1, rig.Presenter.Stats.Dropped);

        // The refused write 3 is the newest handed to the presenter: kept presentable, not released (everything older is).
        Assert.Equal(2UL, rig.Presenter.LastReleasedWrite);
        Assert.Equal(3UL, rig.Presenter.RetainedWrite);
        Assert.Equal(0UL, rig.Producer.ReadReleasedValue());

        for (var i = 1; i <= 3; i++)
        {
            rig.Producer.WritePattern(TestPattern.Ramp, i);
        }

        Assert.True(rig.Producer.WaitReleased(2, Second));
        Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 3, 4, 160, 90, 2), rig.ReadPixels()[(4 * 160) + 3]);
        Assert.Equal(2UL, rig.Producer.ReadReleasedValue());

        // A retry shows it after all, and that releases it.
        rig.WaitReady();
        Assert.True(rig.Presenter.TryPresent(rig.Request(3)));
        Assert.True(rig.Producer.WaitReleased(3, Second));
        Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 3, 4, 160, 90, 3), rig.ReadPixels()[(4 * 160) + 3]);
        Assert.Equal(1, rig.Presenter.Stats.RetainedShown);

        // Writes never offered to the presenter can be released explicitly.
        rig.Producer.WritePattern(TestPattern.Ramp, 4);
        rig.Producer.WritePattern(TestPattern.Ramp, 5);
        rig.Presenter.ReleaseThrough(5);
        Assert.True(rig.Producer.WaitReleased(5, Second));
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Target_resize_recreates_the_images() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 640, 360, PixelFormat.Rgba8);
        rig.WriteAndPresent(TestPattern.Smooth, 0);
        rig.Target.Resize(1000, 1000);
        rig.WriteAndPresent(TestPattern.Smooth, 0);
        Assert.Equal(1, rig.Presenter.Stats.TargetRecreations);
        Assert.Equal(1000, rig.Target.Width);
        var pixels = rig.ReadPixels();
        var rect = ContentRect.Fit(640, 360, 1000, 1000);
        Assert.Equal(0u, pixels[(rect.Y - 1) * 1000 + 500] & 0x00FFFFFF);
        Assert.NotEqual(0u, pixels[rect.Y * 1000 + 500] & 0x00FFFFFF);
        Assert.NotEqual(0u, pixels[(rect.Y + rect.Height - 1) * 1000 + 500] & 0x00FFFFFF);
        Assert.Equal(0u, pixels[(rect.Y + rect.Height) * 1000 + 500] & 0x00FFFFFF);

        rig.Target.Resize(640, 360);
        rig.WriteAndPresent(TestPattern.Ramp, 9);
        Assert.Equal(2, rig.Presenter.Stats.TargetRecreations);
        Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 639, 359, 640, 360, 9), rig.ReadPixels()[^1]);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Reconfigure_replaces_the_ring_for_a_new_format_and_size() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 1280, 720, PixelFormat.Rgba8);
        Assert.Equal(1, rig.Presenter.RingGeneration);
        rig.WriteAndPresent(TestPattern.Ramp, 1);
        Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 1279, 719, 1280, 720, 1), rig.ReadPixels()[^1]);

        // Same configuration: nothing is rebuilt.
        rig.Configure(1280, 720, PixelFormat.Rgba8);
        Assert.Equal(1, rig.Presenter.RingGeneration);

        rig.Configure(1280, 720, PixelFormat.Rgb10A2);
        Assert.Equal(2, rig.Presenter.RingGeneration);
        Assert.Equal(TargetColorSpace.Hdr10St2084, rig.Target.ColorSpace);
        Assert.Equal(1UL, rig.Producer.NextWrite);
        rig.WriteAndPresent(TestPattern.Ramp, 2);
        Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgb10A2, 1279, 719, 1280, 720, 2), rig.ReadPixels()[^1]);

        // An SDR output colour for 10-bit content selects the placeholder on an SDR image.
        rig.Configure(1280, 720, PixelFormat.Rgb10A2, ColorMetadata.SdrBt709);
        Assert.Equal(2, rig.Presenter.RingGeneration);
        Assert.Equal(OutputTransform.PqToSdrPlaceholder, rig.Presenter.ActiveTransform);
        rig.WriteAndPresent(TestPattern.Ramp, 3);

        rig.Configure(640, 360, PixelFormat.Rgb10A2);
        Assert.Equal(3, rig.Presenter.RingGeneration);
        rig.WriteAndPresent(TestPattern.Smooth, 0);
        var pixels = rig.ReadPixels();
        Assert.Equal(1280 * 720, pixels.Length);
        Assert.NotEqual(0u, pixels[0] & 0x3FFFFFFF);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Shutdown_completes_when_the_producer_never_delivers() => Gpu.Run(ctx =>
    {
        var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgba8, presenter: new VulkanPresenterOptions { DrainTimeoutNs = 200_000_000 });
        Assert.True(rig.Presenter.TryPresent(rig.Request(1)));
        var watch = Stopwatch.StartNew();
        rig.Dispose();
        Gpu.WriteLine($"dispose with an undelivered write took {watch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(watch.Elapsed.TotalSeconds < 2);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Presenter_reports_capabilities_and_timing() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 64, 64, PixelFormat.Rgba8, new OffscreenTargetOptions
        {
            Width = 64,
            Height = 64,
            RefreshRate = Core.Time.FrameRate.FromHz(120),
        });
        IPresenter presenter = rig.Presenter;
        Assert.Equal(2, presenter.Capabilities.MaxFramesInFlight);
        Assert.Equal(MemoryDomain.Vulkan, presenter.Capabilities.Domain);
        Assert.True(presenter.Capabilities.Features.HasFlag(PresenterFeatures.Hdr10));
        Assert.Equal(120, presenter.Timing.RefreshRate.Hz);
        Assert.Throws<NotSupportedException>(() => presenter.Configure(new PresenterConfig { Width = 64, Height = 64, Format = PixelFormat.Rgba8, EnableIntegratedFrameGeneration = true }));
    });

    internal static int Pq(double nits)
    {
        const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
        var y = Math.Pow(nits / 10000, m1);
        return (int)Math.Round(Math.Pow((c1 + (c2 * y)) / (1 + (c3 * y)), m2) * 1023);
    }

    private static double PqToNits(double e)
    {
        const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
        var p = Math.Pow(e, 1 / m2);
        return Math.Pow(Math.Max(p - c1, 0) / (c2 - (c3 * p)), 1 / m1) * 10000;
    }

    private static double SrgbEncode(double l) => l <= 0.0031308 ? l * 12.92 : (1.055 * Math.Pow(l, 1 / 2.4)) - 0.055;

    /// <summary>The Smooth pattern sampled bilinearly at target pixel (x, y), as the presenter's mapping defines it.</summary>
    private static (double R, double G, double B) Bilinear(PixelFormat format, int w, int h, ContentRect rect, int x, int y)
    {
        var sx = ((x + 0.5 - rect.X) / rect.Width * w) - 0.5;
        var sy = ((y + 0.5 - rect.Y) / rect.Height * h) - 0.5;
        var x0 = (int)Math.Floor(sx);
        var y0 = (int)Math.Floor(sy);
        var fx = sx - x0;
        var fy = sy - y0;
        (double, double, double) At(int px, int py)
        {
            var (r, g, b, _) = TestPatterns.Unpack(TestPatterns.Pixel(TestPattern.Smooth, format, Math.Clamp(px, 0, w - 1), Math.Clamp(py, 0, h - 1), w, h, 0), format);
            return (r, g, b);
        }

        var p00 = At(x0, y0);
        var p10 = At(x0 + 1, y0);
        var p01 = At(x0, y0 + 1);
        var p11 = At(x0 + 1, y0 + 1);
        double Mix(double a, double b, double c, double d) => ((a * (1 - fx)) + (b * fx)) * (1 - fy) + (((c * (1 - fx)) + (d * fx)) * fy);
        return (Mix(p00.Item1, p10.Item1, p01.Item1, p11.Item1), Mix(p00.Item2, p10.Item2, p01.Item2, p11.Item2), Mix(p00.Item3, p10.Item3, p01.Item3, p11.Item3));
    }
}
