using System.Collections.Concurrent;
using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Pipeline.Tests.Support;
using FrameMotionPlus.Vfx;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// VFG with its automatic shot-change detection off (<c>--scd off</c>, a capture's default): <c>NvVFX_Run</c> then only
/// enqueues, so the generator thread returns at once and up to the bridge's lead of writes queue on the generator stream.
/// Every generated slot is generated, in order, with nothing held back and nothing allocated on the hot threads; a source's
/// discontinuity (a loop) is still never interpolated across; a generator fault is still caught and played through.
/// (Quality switching with it off: <see cref="QualitySwitchPlaybackTests"/>.)
/// </summary>
[Trait(Category.Name, Category.Vfx)]
public class ShotChangeDetectionOffTests
{
    private static PlaybackOptions Realtime(string path, double rateHz, int multiplier, double seconds, FrameGenQuality quality, bool detection) => new()
    {
        Path = path,
        Generator = GeneratorKind.Vfg,
        Quality = quality,
        ShotChangeDetection = detection,
        Multiplier = multiplier,
        Display = DisplayKind.Offscreen,
        Clock = PlaybackClock.Real,
        OffscreenRate = Core.Time.FrameRate.FromDouble(rateHz),
        DurationNs = (long)(seconds * 1e9),
        WarmupNs = 500_000_000,
        Loop = true,
    };

    /// <summary>
    /// The real clock, offscreen, no test hooks on the hot threads: every slot the scheduler gives a generated frame gets
    /// one (no fallback, no fault), no slot is missed, the lead stays within the bridge's limit (which keeps the stream
    /// under its queue limit), the generator thread's time per write is the enqueue alone (not the generation), nothing is
    /// allocated in the measured window, and the teardown is clean. 4K 10-bit at 4x and with High (about 6 ms a frame), and
    /// the same 4K 4x run with detection on for comparison.
    /// </summary>
    [Theory]
    [InlineData("c60-long", 120.0, 2, FrameGenQuality.Medium, false)]
    [InlineData("hevc10-2160p", 240.0, 4, FrameGenQuality.Medium, false)]
    [InlineData("hevc10-2160p", 120.0, 2, FrameGenQuality.High, false)]
    [InlineData("hevc10-2160p", 240.0, 4, FrameGenQuality.Medium, true)]
    public void Every_slot_is_generated_in_order_and_nothing_is_allocated(string clip, double rateHz, int multiplier, FrameGenQuality quality, bool detection)
    {
        var cuda = Hardware.RequireVfx();
        VfgGenerator? vfg = null;
        var hooks = new PlaybackHooks
        {
            WrapGenerator = (h, s, k) =>
            {
                vfg = Assert.IsType<VfgGenerator>(h.Generator);
                return h;
            },
        };
        var report = Playback.Run(cuda, Realtime(Clips.Get(clip), rateHz, multiplier, 4, quality, detection), hooks);
        var s = report.Statistics;
        Log.WriteLine($"{clip} {quality} {multiplier}x at {rateHz} Hz, detection {(detection ? "on" : "off")}: generation p50 {Ms(s.GenerationTimes.Percentile(0.5))} " +
                      $"p99 {Ms(s.GenerationTimes.Percentile(0.99))} ms; generator thread per write p50 {Ms(s.WriteHostTimes.Percentile(0.5))} p99 " +
                      $"{Ms(s.WriteHostTimes.Percentile(0.99))} ms; lead at most {report.MaxOutstanding} of {report.Setup.MaxLead} writes, budget " +
                      $"{report.Setup.OperationsPerWrite} operations a write; latency p50 {Ms(s.RealFrameLatency.Percentile(0.5))} ms; " +
                      $"GC {report.MeasuredGc.Gen0Collections}/{report.MeasuredGc.Gen1Collections}/{report.MeasuredGc.Gen2Collections}");

        Assert.Empty(report.Failures()); // the 0 B rule on every hot thread included, teardown errors, out-of-order signals
        Assert.Equal(StopReason.Duration, report.StopReason);
        Assert.NotNull(vfg);
        Assert.Equal(detection, vfg.Options.AutomaticShotChangeDetection);
        Assert.Equal(detection, report.Setup.GeneratorMayBlock);
        Assert.Equal(0, report.GeneratorFaults);
        Assert.Equal(0, report.Fallbacks);
        Assert.Equal(0, s.PipelineMisses);
        Assert.Equal(0, report.SkippedBackPressure);
        Assert.True(report.MaxOutstanding <= report.Setup.MaxLead, $"lead {report.MaxOutstanding} over the limit {report.Setup.MaxLead}");
        Assert.True(report.Setup.MaxLead * report.Setup.OperationsPerWrite <= CudaVulkan.CudaVulkanBridgeOptions.StreamQueueOperations,
            $"lead {report.Setup.MaxLead} x {report.Setup.OperationsPerWrite} operations could fill the stream");
        Assert.Equal(0, report.AbandonedImports);
        Assert.False(report.GpuAbandoned);

        // Every pair but the loop's gets M - 1 generated frames (the clip loops every 40 frames at 4K).
        Assert.True(report.GeneratedWrites >= (multiplier - 1) * report.Copies * 0.9, $"{report.GeneratedWrites} generated for {report.Copies} real frames at {multiplier}x");
        var host = s.WriteHostTimes.Percentile(0.5);
        var gpu = s.GenerationTimes.Percentile(0.5);
        if (detection)
        {
            Assert.True(host > 0.5 * gpu, $"with detection on the generator thread should wait for the generation: {Ms(host)} ms a write, d {Ms(gpu)} ms");
        }
        else
        {
            Assert.True(host < 0.5 * gpu && host < 1_000_000, $"with detection off the generator thread should only enqueue: {Ms(host)} ms a write, d {Ms(gpu)} ms");
        }
    }

    /// <summary>
    /// A loop is a source discontinuity: with detection off, the scheduler still never shows a frame generated between a
    /// frame before it and one after (which VFG would now morph); real frames stay bit-exact; and the
    /// generator is told of it (<c>NotifyDiscontinuity</c>, which arms VFG's manual flag for any pair across it: none is
    /// enqueued, so no run used it). Every generated frame is a genuine interpolation, not a copy. 2x and 4x.
    /// </summary>
    [Theory]
    [InlineData(2, 120.0)]
    [InlineData(4, 240.0)]
    public void A_loop_is_never_interpolated_across(int multiplier, double rateHz)
    {
        var cuda = Hardware.RequireVfx();
        var even = Clips.Get("even-60");
        var reference = ReferenceFrames.Decode(cuda, even, 200, loop: true);
        var checker = new PresentChecker(reference, GeneratorKind.Vfg);
        VfgGenerator? vfg = null;
        var notices = new ConcurrentQueue<long>();
        var hooks = new PlaybackHooks
        {
            OffscreenReadback = true,
            OnPresented = checker.OnPresented,
            WrapGenerator = (h, s, k) =>
            {
                vfg = Assert.IsType<VfgGenerator>(h.Generator);
                return new GeneratorHandle(new Notified(h.Generator, notices), h.ReadGpuTime, h.Name);
            },
        };
        var options = Playback.Virtual(even, GeneratorKind.Vfg, multiplier, rateHz, 2.6) with { Loop = true, ShotChangeDetection = false };
        var report = Playback.Run(cuda, options, hooks, onOpen: s => checker.Session = s);
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.True(checker.Problems.Count == 0, string.Join("\n", checker.Problems.Take(10)));
        Assert.NotNull(vfg);
        Assert.False(vfg.Options.AutomaticShotChangeDetection);
        Assert.Equal(0, report.Fallbacks);
        Assert.Equal(0, report.Statistics.TotalMisses);

        var cuts = Enumerable.Range(0, reference.Count).Where(i => reference.Discontinuity(i)).ToList();
        Assert.NotEmpty(cuts);
        Assert.True(report.Discontinuities >= 1, "the loop did not reach the pipeline as a discontinuity");
        Assert.False(notices.IsEmpty, "the generator was not told of the loop");
        Assert.Equal(0, vfg.ShotChangeRuns);

        // Across the loop: no generated frame spans it, and the first image after it is the loop's first frame, real.
        var results = checker.Results;
        var cutsSeen = 0;
        var realFirst = 0;
        foreach (var cut in cuts.Where(c => c > 0))
        {
            Assert.DoesNotContain(results, r => r.Frame.Kind == SlotKind.Generated && (r.Frame.FrameB == cut || r.Frame.BreakBeforeB));
            var after = results.Where(r => r.Frame.Kind != SlotKind.Repeat && Math.Max(r.Frame.FrameA, r.Frame.FrameB) >= cut).Take(1).ToList();
            if (after.Count == 0)
            {
                continue; // the run ended before this loop
            }

            // The first new image after it is made of frames after it only: the loop's first frame (bit-exact), or, where
            // the slots fall between that frame and the next, their interpolation (between its frames, not a copy).
            var first = after[0];
            cutsSeen++;
            realFirst += first.Frame.Kind == SlotKind.Real ? 1 : 0;
            Assert.True(first.Frame.FrameA >= cut, $"slot {first.Frame.SlotIndex} after the loop at frame {cut} shows frames {first.Frame.FrameA} and {first.Frame.FrameB}");
            Assert.True(first.Ok, $"slot {first.Frame.SlotIndex}, the first image after the loop at frame {cut}, is not right");
        }

        Assert.True(cutsSeen >= 1, "the run never reached the loop");
        var generated = results.Where(r => r.Frame.Kind == SlotKind.Generated).ToList();
        Assert.True(generated.Count > (multiplier - 1) * 100, $"only {generated.Count} generated frames");
        Log.WriteLine($"{multiplier}x: {results.Count} presents checked, {generated.Count} generated, every one between its frames (nearest neighbour at least " +
                      $"{generated.Min(r => r.ToNeighbour):0.###} codes away); {cutsSeen} loop(s) crossed ({realFirst} with the loop's first frame as the first new image); {notices.Count} discontinuity notice(s), " +
                      $"{vfg.ShotChangeRuns} manual shot-change runs; VFG runs {vfg.Runs}");
    }

    /// <summary>
    /// A generator that throws mid-run with detection off (the call returns before the GPU work, so whatever it queued is
    /// still on the stream): the slot gets the real frame behind it, generation stays off, playback carries on with real
    /// frames and the run reports the fault, as with the blocking call.
    /// </summary>
    [Fact]
    public void A_generator_fault_is_still_caught_and_played_through()
    {
        var cuda = Hardware.RequireVfx();
        var hooks = new PlaybackHooks
        {
            WrapGenerator = (h, s, k) => new GeneratorHandle(new ThrowingAt(h.Generator, s, 40), h.ReadGpuTime, "VFG, throwing at 40"),
        };
        var report = Playback.Run(cuda, Realtime(Clips.Get("c60-long"), 120, 2, 3, FrameGenQuality.Medium, detection: false), hooks);
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(StopReason.Duration, report.StopReason);
        Assert.Equal(1, report.GeneratorFaults);
        Assert.True(report.GeneratorFaulted);
        Assert.Contains("injected", report.FirstGeneratorFault, StringComparison.Ordinal);
        Assert.Equal(39, report.GeneratedWrites);
        Assert.True(report.Fallbacks > 50, $"only {report.Fallbacks} generated slots fell back to real frames");
        Assert.True(report.Statistics.Presents >= report.Statistics.MeasuredSeconds * 120 * 0.97, $"{report.Statistics.Presents} presents in {report.Statistics.MeasuredSeconds:0.00} s");
        Assert.Equal(0, report.Statistics.PipelineMisses);
        Assert.Equal(0, report.OutOfOrderSignals);
    }

    /// <summary>
    /// Back-pressure now that the call no longer waits for the GPU: VFG High (the dearest, about 170 stream operations a
    /// run, budgeted at 192 a write) with every generation 40 ms behind a delay kernel for a while. Writes pile up on the
    /// generator stream until the bridge refuses slots; the lead never passes its limit (5 writes, at most 960 of the
    /// stream's ~1,024 operations), the pacing thread skips slots instead (counted), nothing stalls, and playback recovers.
    /// </summary>
    [Fact]
    public void A_slow_gpu_is_held_back_at_the_bridge()
    {
        var cuda = Hardware.RequireVfx();
        var presents = new ConcurrentQueue<long>();
        var hooks = new PlaybackHooks
        {
            WrapGenerator = (h, s, k) => new GeneratorHandle(new SlowFor(h.Generator, s, k, 30, 70, 40_000_000), h.ReadGpuTime, h.Name),
            BeforePresent = j => presents.Enqueue(Stopwatch.GetTimestamp()),
        };
        var report = Playback.Run(cuda, Realtime(Clips.Get("c60-long"), 120, 2, 5, FrameGenQuality.High, detection: false), hooks);
        Log.WriteLine($"lead at most {report.MaxOutstanding} of {report.Setup.MaxLead} writes x {report.Setup.OperationsPerWrite} operations; " +
                      $"{report.SkippedBackPressure} slots skipped for back-pressure");
        Assert.Empty(Playback.FailuresIgnoringHookAllocations(report));
        Assert.Equal(StopReason.Duration, report.StopReason);
        Assert.Equal(192, report.Setup.OperationsPerWrite);
        Assert.True(report.Setup.MaxLead * report.Setup.OperationsPerWrite <= CudaVulkan.CudaVulkanBridgeOptions.StreamQueueOperations);
        Assert.True(report.SkippedBackPressure > 10, $"only {report.SkippedBackPressure} slots skipped for back-pressure");
        Assert.Equal(report.Setup.MaxLead, report.MaxOutstanding); // it did fill up to the limit, and no further
        Assert.Equal(0, report.OutOfOrderSignals);
        Assert.Equal(0, report.GeneratorFaults);

        // In the last second presents came at about the refresh rate again.
        var times = presents.ToArray();
        var lastSecond = times.Count(t => times[^1] - t < Stopwatch.Frequency);
        Assert.True(lastSecond >= 120 * 0.9, $"{lastSecond} presents in the last second at 120 Hz");
    }

    private static string Ms(double ns) => (ns / 1e6).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Passes everything through and records each discontinuity notice.</summary>
    private sealed class Notified(IFrameGenerator inner, ConcurrentQueue<long> notices) : IFrameGenerator
    {
        public FrameGenCapabilities Capabilities => inner.Capabilities;

        public void Configure(in FrameGenConfig config) => inner.Configure(config);

        public void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done) => inner.Enqueue(prev, curr, t, output, done);

        public void NotifyDiscontinuity(long contentTimeNs)
        {
            notices.Enqueue(contentTimeNs);
            inner.NotifyDiscontinuity(contentTimeNs);
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>Keeps the GPU busy before each of a range of calls (a delay kernel on the generator stream).</summary>
    private sealed class SlowFor(IFrameGenerator inner, Cuda.CudaStream stream, Cuda.Kernels.ImageKernels kernels, int from, int until, long ns) : IFrameGenerator
    {
        private int _calls;

        public FrameGenCapabilities Capabilities => inner.Capabilities;

        public void Configure(in FrameGenConfig config) => inner.Configure(config);

        public void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done)
        {
            var call = ++_calls;
            if (call >= from && call < until)
            {
                kernels.EnqueueDelay(stream, ns);
            }

            inner.Enqueue(prev, curr, t, output, done);
        }

        public void NotifyDiscontinuity(long contentTimeNs) => inner.NotifyDiscontinuity(contentTimeNs);

        public void Dispose() => inner.Dispose();
    }

    /// <summary>Throws at one call after queueing its GPU-side waits, as a failing call may (Core's failure contract).</summary>
    private sealed class ThrowingAt(IFrameGenerator inner, Cuda.CudaStream stream, int at) : IFrameGenerator
    {
        private int _calls;

        public FrameGenCapabilities Capabilities => inner.Capabilities;

        public void Configure(in FrameGenConfig config) => inner.Configure(config);

        public void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done)
        {
            if (++_calls == at)
            {
                stream.Wait(prev.Ready);
                stream.Wait(output.Ready);
                throw new InvalidOperationException($"injected generator failure at call {_calls}");
            }

            inner.Enqueue(prev, curr, t, output, done);
        }

        public void NotifyDiscontinuity(long contentTimeNs) => inner.NotifyDiscontinuity(contentTimeNs);

        public void Dispose() => inner.Dispose();
    }
}
