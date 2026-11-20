using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// From the independent review of `--scd off` (2026-11-20): with VFG's shot-change detection off and the GPU behind (every generation behind a delay
/// kernel), writes queue up to the bridge's lead while the source goes on in real time, so source frames are released
/// and their buffers rewritten while generations that read them are still queued. Every generated frame shown must still
/// be made of its own two frames: closer to the true middle frame than the blend of them.
/// </summary>
[Trait(Category.Name, Category.Vfx)]
public class QueuedGenerationInputsTests
{
    [Theory]
    [InlineData(false, 30)]
    [InlineData(false, 0)]
    [InlineData(true, 30)]
    public void Queued_generations_read_their_own_frames(bool detection, int delayMs)
    {
        var cuda = Hardware.RequireVfx();
        var even = Clips.Get("even-60");
        var truth = ReferenceFrames.Decode(cuda, Clips.Get("truth-120"), 240, loop: false);
        var reference = ReferenceFrames.Decode(cuda, even, 120, loop: false);
        var checker = new PresentChecker(reference, GeneratorKind.Vfg) { Truth = a => 2 * a + 1 < truth.Count ? truth.Pixels((2 * a) + 1) : null };
        var hooks = new PlaybackHooks
        {
            OffscreenReadback = true,
            OnPresented = checker.OnPresented,
            WrapGenerator = (h, s, k) => delayMs == 0 ? h : new GeneratorHandle(new Slow(h.Generator, s, k, 8, 100, delayMs * 1_000_000L), h.ReadGpuTime, h.Name),
        };
        var options = Playback.Virtual(even, GeneratorKind.Vfg, 2, 120, 1.8) with { ShotChangeDetection = detection, Quality = FrameGenQuality.Medium };
        var report = Playback.Run(cuda, options, hooks, onOpen: s => checker.Session = s);
        var generated = checker.Results.Where(r => r.Frame.Kind == SlotKind.Generated).ToList();
        var halves = generated.Where(r => Math.Abs(r.Frame.Timestep - 0.5) < 0.01 && r.BlendToTruth > 0).ToList();
        var better = halves.Count(r => r.ToTruth < r.BlendToTruth);
        var worst = halves.Count == 0 ? 0 : halves.Max(r => r.ToTruth / r.BlendToTruth);
        Log.WriteLine($"REVIEW detection {(detection ? "on" : "off")}, delay {delayMs} ms: lead at most {report.MaxOutstanding} of {report.Setup.MaxLead}; " +
                      $"{report.SkippedBackPressure} skipped for back-pressure; {checker.Results.Count} presents checked, {generated.Count} generated, " +
                      $"{halves.Count} at t = 0.5 scored: {better} beat the blend, worst VFG/blend error ratio {worst:0.000}, " +
                      $"mean VFG error {(halves.Count == 0 ? 0 : halves.Average(r => r.ToTruth)):0.000}, mean blend error {(halves.Count == 0 ? 0 : halves.Average(r => r.BlendToTruth)):0.000}; " +
                      $"problems {checker.Problems.Count}; fallbacks {report.Fallbacks}, faults {report.GeneratorFaults}, out of order {report.OutOfOrderSignals}");
        Assert.True(checker.Problems.Count == 0, string.Join("\n", checker.Problems.Take(10)));
        Assert.Equal(0, report.GeneratorFaults);
        Assert.Equal(0, report.OutOfOrderSignals);
        Assert.True(halves.Count > 20, $"only {halves.Count} generated frames scored");
        // Nine in ten, as the quality-switch test asks: a few frames lose to the blend with detection on as well (4 of 108 measured).
        Assert.True(better * 10 >= halves.Count * 9, $"VFG beat the blend in only {better} of {halves.Count} frames (worst ratio {worst:0.000})");
        if (delayMs > 0 && !detection)
        {
            Assert.True(report.MaxOutstanding >= 3, $"the lead only reached {report.MaxOutstanding}: nothing queued");
        }
    }

    private sealed class Slow(IFrameGenerator inner, Cuda.CudaStream stream, Cuda.Kernels.ImageKernels kernels, int from, int until, long ns) : IFrameGenerator
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
}
