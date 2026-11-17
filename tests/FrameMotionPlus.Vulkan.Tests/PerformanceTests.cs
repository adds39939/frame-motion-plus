using System.Diagnostics;
using System.Globalization;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Diagnostics;
using FrameMotionPlus.Vulkan.Tests.Support;

namespace FrameMotionPlus.Vulkan.Tests;

/// <summary>
/// Measures the present pass on the GPU (timestamp queries) and TryPresent on the CPU, offscreen at 4K output. The
/// CPU numbers here include the validation layer, which every test runs under and which dominates them; without it
/// TryPresent costs several times less.
/// </summary>
public class PerformanceTests
{
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(PixelFormat.Rgba8, 2560, 1440)]
    [InlineData(PixelFormat.Rgb10A2, 2560, 1440)]
    [InlineData(PixelFormat.Rgba8, 3840, 2160)]
    [InlineData(PixelFormat.Rgb10A2, 3840, 2160)]
    public void Present_pass_cost_at_4k_output(PixelFormat format, int ringWidth, int ringHeight) => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, ringWidth, ringHeight, format, new OffscreenTargetOptions { Width = 3840, Height = 2160, Readback = false });
        rig.Producer.FillAllStaging(TestPattern.Smooth, 0);
        const int Warmup = 60;
        const int Frames = 400;
        var copy = new List<double>();
        var pass = new List<double>();
        var cpu = new List<double>();
        for (var i = 0; i < Warmup + Frames; i++)
        {
            rig.WaitReady();
            var n = rig.Producer.WriteStaged();
            var request = rig.Request(n);
            var start = Stopwatch.GetTimestamp();
            Assert.True(rig.Presenter.TryPresent(request));
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            if (i >= Warmup)
            {
                cpu.Add(elapsed);
                var stats = rig.Presenter.Stats;
                copy.Add(stats.LastCopyGpuNs / 1000.0);
                pass.Add(stats.LastPassGpuNs / 1000.0);
            }
        }

        Assert.True(rig.Presenter.WaitIdle(1_000_000_000));
        static string P(List<double> v) =>
            string.Create(CultureInfo.InvariantCulture, $"p50 {Percentile(v, 0.5):F1} us, p99 {Percentile(v, 0.99):F1} us");
        Gpu.WriteLine($"{format} {ringWidth}x{ringHeight} -> 3840x2160: GPU copy {P(copy)}; GPU pass {P(pass)}; CPU TryPresent (with validation) {P(cpu)}");
        Assert.InRange(Percentile(pass, 0.5), 1, 2000);
        Assert.InRange(Percentile(copy, 0.5), 1, 2000);
    });

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Min(sorted.Length - 1, Math.Round(p * (sorted.Length - 1)))];
    }
}
