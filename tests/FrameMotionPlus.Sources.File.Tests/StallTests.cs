using System.Diagnostics;
using FrameMotionPlus.App.Decode;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Sources.File.Tests.Support;

namespace FrameMotionPlus.Sources.File.Tests;

/// <summary>What decoding does to another thread's CUDA calls (run alone: other tests' CUDA work would show).</summary>
[Trait(Category.Name, Category.Media)]
[Collection(SerialMediaDefinition.Name)]
public class StallTests
{
    /// <summary>
    /// While the source decodes at full speed (1080p, looping), another thread launching kernels and recording events on
    /// a stream of its own is not held up: p99.9 under 1 ms and its worst call under 20 ms (OS scheduling on a busy desktop
    /// included; the baseline without decoding is logged). Control: a context-synchronising free behind 100 ms of queued
    /// work holds the same probe for most of those 100 ms, so the probe would see a stall if decoding caused one.
    /// </summary>
    [Fact]
    public void Decoding_does_not_stall_another_threads_cuda_calls()
    {
        var env = Media.Require();
        StallReport baseline;
        using (var alone = StallProbe.Start(env.Context, env.Kernels))
        {
            Thread.Sleep(1000);
            baseline = alone.Stop();
        }

        using var source = new FileFrameSource(env.Context, env.Clip("h264-1080p"), new FileSourceOptions { Loop = true, Kernels = env.Kernels });
        using var probe = StallProbe.Start(env.Context, env.Kernels);
        source.Start();
        var deadline = Stopwatch.GetTimestamp() + (2 * Stopwatch.Frequency);
        long frames = 0;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (source.TryRead(out var frame))
            {
                source.Release(frame.FrameId);
                frames++;
            }
        }

        var during = probe.Stop();
        source.Stop();
        Log.WriteLine($"decoding {frames} frames in 2 s: other thread {during}; alone: {baseline}");
        Assert.True(frames > 600, $"only {frames} frames decoded in 2 s");
        Assert.True(during.P999Ns <= 1_000_000, $"p99.9 {during.P999Ns / 1e6:0.000} ms");
        Assert.True(during.MaxNs <= 20_000_000, $"worst {during.MaxNs / 1e6:0.000} ms");

        // Control: the same probe, and a free that waits behind 100 ms of work on another stream.
        using var control = StallProbe.Start(env.Context, env.Kernels);
        using (var busy = CudaStream.Create(env.Context))
        {
            var buffer = CudaDeviceBuffer.Allocate(env.Context, 1 << 20);
            Thread.Sleep(100);
            env.Kernels.EnqueueDelay(busy, 100_000_000);
            buffer.Dispose(); // cuMemFree waits for all queued work in the context, holding other threads' enqueues
            busy.Synchronize();
            Thread.Sleep(100);
        }

        var stalled = control.Stop();
        Log.WriteLine($"control (a free behind 100 ms of work): {stalled}");
        Assert.True(stalled.MaxNs >= 50_000_000, $"control: the probe did not see the free's stall ({stalled.MaxNs / 1e6:0.000} ms)");
    }

}
