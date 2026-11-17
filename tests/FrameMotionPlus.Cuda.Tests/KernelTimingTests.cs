using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>
/// GPU time per kernel at 1440p and 4K, measured with span timers around each launch. Serial, so no other test
/// competes for the GPU. Two variants: "hot" reuses one set of images, which then sits in the RTX 5090's large L2
/// cache; "cold" rotates through enough image sets (over 512 MB) that every run reads and writes DRAM, as a pipeline
/// working on fresh frames does. The numbers go to the test output; the bounds only catch gross regressions.
/// </summary>
[Collection(SerialGpuDefinition.Name)]
[Trait("Category", "Gpu")]
public class KernelTimingTests(ITestOutputHelper output)
{
    private const int Runs = 200;

    public static TheoryData<int, int, PixelFormat> Sizes => new()
    {
        { 2560, 1440, PixelFormat.Rgba8 },
        { 2560, 1440, PixelFormat.Rgb10A2 },
        { 3840, 2160, PixelFormat.Rgba8 },
        { 3840, 2160, PixelFormat.Rgb10A2 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Pattern_blend_and_copy_gpu_times(int width, int height, PixelFormat format)
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context, CudaStreamPriority.High);
        var sets = Math.Max(2, (int)((512L << 20) / (3L * width * height * 4)) + 1);
        var buffers = new List<CudaDeviceBuffer>();
        try
        {
            var frames = new GpuFrame[sets][];
            for (var s = 0; s < sets; s++)
            {
                frames[s] = new GpuFrame[3];
                for (var i = 0; i < 3; i++)
                {
                    var buffer = env.Image(width, height, format, stream);
                    buffers.Add(buffer);
                    frames[s][i] = buffer.AsFrame();
                    env.Kernels.EnqueueTestPattern(stream, frames[s][i], (uint)i);
                }
            }

            foreach (var cold in new[] { false, true })
            {
                var pattern = Measure(env, stream, run => env.Kernels.EnqueueTestPattern(stream, Pick(frames, run, cold)[0], 7));
                var blend = Measure(env, stream, run =>
                {
                    var f = Pick(frames, run, cold);
                    env.Kernels.EnqueueBlend(stream, f[0], f[1], 0.5f, f[2]);
                });
                var copy = Measure(env, stream, run =>
                {
                    var f = Pick(frames, run, cold);
                    stream.CopyFrameAsync(f[0], f[2]);
                });

                output.WriteLine(
                    $"{width}x{height} {format} {(cold ? "cold" : "hot "),-4}: pattern p50 {pattern.P50,5:F1} us p99 {pattern.P99,5:F1} | " +
                    $"blend p50 {blend.P50,5:F1} us p99 {blend.P99,5:F1} | 2D copy p50 {copy.P50,5:F1} us p99 {copy.P99,5:F1}");
                Assert.InRange(pattern.P50, 0.1, 2000);
                Assert.InRange(blend.P50, 0.1, 2000);
                Assert.InRange(copy.P50, 0.1, 2000);
            }
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                buffer.Dispose();
            }
        }
    }

    private static GpuFrame[] Pick(GpuFrame[][] frames, int run, bool cold) => cold ? frames[run % frames.Length] : frames[0];

    private static (double P50, double P99) Measure(GpuEnvironment env, CudaStream stream, Action<int> work)
    {
        using var ring = CudaSpanTimerRing.Create(env.Context, Runs);
        for (var i = 0; i < 20; i++)
        {
            work(i);
        }

        // Keep the stream busy while the host enqueues the measured runs, so no span includes host enqueue time.
        env.Kernels.EnqueueDelay(stream, 20_000_000);
        for (var i = 0; i < Runs; i++)
        {
            ring.Begin(stream);
            work(i);
            ring.End(stream);
        }

        stream.Synchronize();
        var us = new List<double>(Runs);
        while (ring.TryRead(out var ns))
        {
            us.Add(ns / 1000.0);
        }

        Assert.Equal(Runs, us.Count);
        us.Sort();
        return (us[Runs / 2], us[(int)(Runs * 0.99)]);
    }
}
