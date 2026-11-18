using System.Runtime.ExceptionServices;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.App.Decode;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Gpu.Tests.Support;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>
/// Phase 7's automated gate, <c>fmp hdr-pattern --check</c>, in-process: the HDR patterns encoded as a console sends them,
/// through the capture's conversion, the ring (as real frames and VFG frames), the presenter and back; scaling in linear
/// light; the HDR metadata; SDR staying SDR. Also the patterns themselves (pure) and the on-screen command's options.
/// </summary>
public sealed class HdrPatternTests
{
    [Fact]
    public void Pq_arithmetic_and_the_patterns_are_what_they_say()
    {
        Assert.Equal(0, HdrPatterns.Code(0));
        Assert.Equal(1023, HdrPatterns.Code(10000));
        Assert.Equal(594, HdrPatterns.Code(203));  // BT.2408 reference white: 58% PQ (full range)
        Assert.InRange(HdrPatterns.Code(1000), 768, 770);  // 75% PQ
        Assert.Equal(100, HdrPatterns.Nits(HdrPatterns.Pq(100)), 6);
        Assert.Equal("0.01", HdrPatterns.Text(0.01));
        Assert.Equal("203", HdrPatterns.Text(203));
        Assert.Equal("10000", HdrPatterns.Text(10000));

        foreach (var kind in Enum.GetValues<HdrPatternKind>())
        {
            var px = HdrPatterns.Render(kind, 1920, 1080);
            Assert.Equal(1920 * 1080, px.Length);
            Assert.All(px, p => Assert.True(p.R is >= 0 and <= 1023 && p.G is >= 0 and <= 1023 && p.B is >= 0 and <= 1023));
            Assert.NotEmpty(HdrPatterns.Interiors(kind, 1920, 1080));
        }

        // The peak pattern's patches are at the stated levels.
        var peak = HdrPatterns.Render(HdrPatternKind.Peak, 1920, 1080);
        var levels = HdrPatterns.Interiors(HdrPatternKind.Peak, 1920, 1080).Where((_, i) => i % 2 == 0).Select(r => peak[(r.Y * 1920) + r.X].R).ToArray();
        Assert.Equal(HdrPatterns.PeakNits.Select(n => HdrPatterns.Code(n)).ToArray(), levels);

        // The ramp covers every code (in steps of the column pairs).
        var ramp = HdrPatterns.Render(HdrPatternKind.Ramp, 2048, 1080);
        var row = HdrPatterns.Interiors(HdrPatternKind.Ramp, 2048, 1080)[0].Y;
        Assert.Equal(1024, Enumerable.Range(0, 2048).Select(x => ramp[(row * 2048) + x].R).Distinct().Count());
    }

    [Fact]
    public void The_ycbcr_encoding_is_the_inverse_of_the_capture_conversion()
    {
        // Through the exact reference of the capture's kernel, a flat colour comes back within a code of what was encoded.
        const int W = 16, H = 8;
        var reference = YuvReference.Create(MatrixCoefficients.Bt2020NonConstant, ColorRange.Limited, 10, ChromaSiting.Left, PixelFormat.Rgb10A2);
        foreach (var (r, g, b) in new[] { (0, 0, 0), (1023, 1023, 1023), (592, 592, 592), (769, 0, 0), (0, 769, 0), (0, 0, 769), (300, 600, 900) })
        {
            var px = Enumerable.Repeat((r, g, b), W * H).ToArray();
            var (y, cb, cr) = HdrPatterns.ToYcbcr(px, W, H, MatrixCoefficients.Bt2020NonConstant, 10);
            var output = new uint[W * H];
            reference.Convert(y, cb, cr, W, H, output);
            var got = output[(4 * W) + 8];
            Assert.InRange((int)(got & 0x3FF), r - 1, r + 1);
            Assert.InRange((int)((got >> 10) & 0x3FF), g - 1, g + 1);
            Assert.InRange((int)((got >> 20) & 0x3FF), b - 1, b + 1);
        }
    }

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void The_hdr_path_check_passes() => Run(() =>
    {
        Hardware.RequireGpu();
        var vfg = Vfx.VfxSdk.TryLoad(out _);
        var (exit, output, err) = Cli(vfg ? ["hdr-pattern", "--check", "--size", "1280x720"] : ["hdr-pattern", "--check", "--size", "1280x720", "--no-vfg"]);
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
        Assert.Contains("pass        ramp: presented", output, StringComparison.Ordinal);
        Assert.Contains("pass        HDR metadata", output, StringComparison.Ordinal);
        Assert.Contains("pass        SDR, 10-bit", output, StringComparison.Ordinal);
        Assert.Contains("pass        back to HDR10", output, StringComparison.Ordinal);
        Assert.Contains("pass        scaling peak", output, StringComparison.Ordinal);
        if (vfg)
        {
            Assert.Contains("pass        bars: VFG frame", output, StringComparison.Ordinal);
        }
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void The_metadata_options_reach_vulkan() => Run(() =>
    {
        Hardware.RequireGpu();
        var (exit, output, err) = Cli("hdr-pattern", "--check", "--size", "640x360", "--no-vfg", "--hdr-peak-nits", "800", "--hdr-max-cll", "700", "--hdr-max-fall", "250");
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("mastering 0.0001 to 800 nits; MaxCLL 700, MaxFALL 250", output, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(new[] { "sparkle" }, "ramp, peak, bars or black")]
    [InlineData(new[] { "--check", "--size", "10x10" }, "a size is WxH")]
    [InlineData(new[] { "peak", "--hdr-peak-nits", "20" }, "--hdr-peak-nits")]
    [InlineData(new[] { "--bogus", "1" }, "Unknown option")]
    public void Bad_options_are_usage_errors(string[] args, string message)
    {
        var (exit, _, err) = Cli(["hdr-pattern", .. args]);
        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Contains(message, err, StringComparison.Ordinal);
    }

    private static void Run(Action body) => body();

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        (int, string, string) result = default;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                var exit = CommandDispatcher.Run(args, output, error);
                result = (exit, output.ToString(), error.ToString());
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        }) { IsBackground = true, Name = "fmp-cli" };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "fmp hdr-pattern did not return within 120 s");
        failure?.Throw();
        Interop.WriteLine(result.Item2 + result.Item3);
        return result;
    }
}
