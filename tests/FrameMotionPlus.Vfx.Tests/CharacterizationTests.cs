using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vfx.Interop;
using FrameMotionPlus.Vfx.Tests.Support;

namespace FrameMotionPlus.Vfx.Tests;

/// <summary>
/// What the SDK itself does, measured, because the Phase 4 pipeline is built around it (docs/benchmarks/phase3-vfg.md
/// quotes these logs). Each test asserts the behaviour it found, with margins, so an SDK or driver update that changes
/// it fails here rather than in the pipeline.
/// </summary>
[Trait(Category.Name, Category.Vfx)]
public unsafe partial class CharacterizationTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>
    /// NvVFX_Run blocks the calling thread for the generation's duration when automatic shot-change detection is on
    /// (the async argument makes no difference), and only enqueues when it is off. Host time of Enqueue against the GPU
    /// span of the same calls, and the thread's CPU time (spinning or sleeping).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Run_blocks_the_caller_only_with_automatic_shot_change_detection(bool detection)
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 10);
        using var b = env.Pattern(Width, Height, format, 12);
        using var o = env.Image(Width, Height, format);
        using var done = CudaEvent.Create(env.Context);
        using var generator = env.Generator(Width, Height, format, options: new VfgOptions { AutomaticShotChangeDetection = detection });

        var host = new List<double>();
        var cpu = new List<double>();
        var gpu = new List<double>();
        for (var i = 0; i < 120; i++)
        {
            var c0 = ThreadCpuNs();
            var t0 = Stopwatch.GetTimestamp();
            generator.Enqueue(a.AsFrame(contentTimeNs: 1), b.AsFrame(contentTimeNs: 2), 0.25f + (0.25f * (i % 3)), o.AsFrame(), done.Signal);
            var hostNs = Stopwatch.GetElapsedTime(t0).TotalNanoseconds;
            var cpuNs = ThreadCpuNs() - c0;
            done.Synchronize();
            Assert.True(generator.TryReadGpuTime(out var gpuNs));
            if (i >= 20)
            {
                host.Add(hostNs);
                cpu.Add(cpuNs);
                gpu.Add(gpuNs);
            }
        }

        var (h, c, g) = (Median(host), Median(cpu), Median(gpu));
        Log.WriteLine($"1080p Medium, detection {(detection ? "on" : "off")}: Enqueue host p50 {Log.Ms(h)} ms (max {Log.Ms(host.Max())}), " +
                      $"thread CPU p50 {Log.Ms(c)} ms, GPU span p50 {Log.Ms(g)} ms");
        if (detection)
        {
            Assert.True(h > 0.7 * g, $"expected Enqueue to block for about the generation: host {Log.Ms(h)} ms, GPU {Log.Ms(g)} ms");
        }
        else
        {
            Assert.True(h < 0.3 * g && h < 300_000, $"expected Enqueue to only enqueue: host {Log.Ms(h)} ms, GPU {Log.Ms(g)} ms");
        }
    }

    /// <summary>
    /// Runs neither synchronise the context nor stall other threads' CUDA calls; NvVFX_Load and NvVFX_DestroyEffect do
    /// both (they wait for all work queued in the context, here 300 ms of unrelated work on another stream, and another
    /// thread's event records and kernel launches block meanwhile), like cuMemFree in Phase 2.
    /// </summary>
    [Fact]
    public void Load_and_destroy_synchronise_the_context_and_runs_do_not()
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 10);
        using var b = env.Pattern(Width, Height, format, 12);
        using var o = env.Image(Width, Height, format);
        using var busy = CudaStream.Create(env.Context);
        using var effectStream = CudaStream.Create(env.Context, CudaStreamPriority.High);
        var key = new VfgInstanceKey(Width, Height, format, VfgMode.Medium);

        using var other = new OtherThread(env);
        (double CallMs, double StallMs) Measure(Action call)
        {
            env.Kernels.EnqueueDelay(busy, 300_000_000);
            Thread.Sleep(20);
            other.ResetWorst();
            var t0 = Stopwatch.GetTimestamp();
            call();
            var callMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            busy.Synchronize();
            Thread.Sleep(20);
            return (callMs, other.WorstMs);
        }

        var effect = VfgEffect.Create(effectStream, key, automaticShotChangeDetection: true);
        try
        {
            effect.Bind(VfgEffect.Previous, a.AsFrame());
            effect.Bind(VfgEffect.Current, b.AsFrame());
            effect.Bind(VfgEffect.Output, o.AsFrame());
            var load = Measure(effect.Load);
            for (var i = 0; i < 4; i++)
            {
                effect.Run(0.5f, false); // warm-up: lazy allocations happen in the first runs
            }

            effectStream.Synchronize();
            var runOn = Measure(() => effect.Run(0.5f, false));
            effect.SetU32(VfxSelectors.AutomaticShotChangeDetectionEnabled, 0);
            var runOff = Measure(() => effect.Run(0.5f, false));
            effectStream.Synchronize();
            var destroy = Measure(effect.Dispose);
            Log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"with 300 ms of unrelated GPU work queued: NvVFX_Load {load.CallMs:0.0} ms (other thread's worst call {load.StallMs:0.0} ms); " +
                $"run, detection on {runOn.CallMs:0.0} ms ({runOn.StallMs:0.00} ms); run, detection off {runOff.CallMs:0.0} ms ({runOff.StallMs:0.00} ms); " +
                $"NvVFX_DestroyEffect {destroy.CallMs:0.0} ms ({destroy.StallMs:0.0} ms)"));

            Assert.True(load.CallMs > 200 && load.StallMs > 150, $"load {load}");
            Assert.True(destroy.CallMs > 200 && destroy.StallMs > 150, $"destroy {destroy}");
            Assert.True(runOn.CallMs < 100 && runOn.StallMs < 50, $"run with detection {runOn}");
            Assert.True(runOff.CallMs < 100 && runOff.StallMs < 50, $"run without detection {runOff}");
            Assert.True(other.Calls > 100, "the other thread hardly ran");
        }
        finally
        {
            effect.Dispose();
        }
    }

    /// <summary>
    /// How many stream operations one VFG run adds, from the ~1,024-operation queue limit: with the stream gated and
    /// detection off, runs are enqueued until the call itself blocks. With detection on a run never queues behind
    /// another (it waits for the stream first), so it cannot fill the queue; it blocks instead.
    /// </summary>
    [Theory]
    [InlineData(VfgMode.Low, 1080)]
    [InlineData(VfgMode.Medium, 1080)]
    [InlineData(VfgMode.High, 1080)]
    [InlineData(VfgMode.Medium, 2160)]
    [InlineData(VfgMode.High, 2160)]
    public void A_run_adds_a_few_dozen_stream_operations(VfgMode mode, int height)
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        var width = height * 16 / 9;
        using var a = env.Pattern(width, height, format, 10);
        using var b = env.Pattern(width, height, format, 12);
        using var o = env.Image(width, height, format);
        using var gateStream = CudaStream.Create(env.Context);
        using var gate = CudaEvent.Create(env.Context);
        using var stream = CudaStream.Create(env.Context, CudaStreamPriority.High);
        using var effect = VfgEffect.Create(stream, new VfgInstanceKey(width, height, format, mode), automaticShotChangeDetection: false);
        effect.Bind(VfgEffect.Previous, a.AsFrame());
        effect.Bind(VfgEffect.Current, b.AsFrame());
        effect.Bind(VfgEffect.Output, o.AsFrame());
        effect.Load();
        effect.Run(0.5f, false);
        stream.Synchronize();

        int Fill(Action op)
        {
            env.Kernels.EnqueueDelay(gateStream, 1_000_000_000);
            gate.Record(gateStream);
            stream.Wait(gate);
            var count = 0;
            for (var i = 1; i <= 3000; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                op();
                if (Stopwatch.GetElapsedTime(t0).TotalMilliseconds > 50)
                {
                    count = i;
                    break;
                }
            }

            stream.Synchronize();
            gateStream.Synchronize();
            return count;
        }

        var memsets = Fill(() => stream.MemsetAsync(o.Address, 0, 64));
        var runs = Fill(() => effect.Run(0.5f, false));
        var perRun = (memsets - 1) / (double)(runs - 1);
        Log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{width}x{height} {mode}: the queue took {memsets - 1} single operations or {runs - 1} runs before the call blocked: about {perRun:0.0} operations per run"));
        Assert.InRange(memsets, 900, 1100);
        Assert.InRange(perRun, 4, 200);
    }

    /// <summary>
    /// Device memory: an instance's first runs allocate more (so the generator warms up at load); after that, runs
    /// neither allocate nor free. Two instances (RGBA8 and RGB10A2, as for SDR/HDR switching) live and run in one
    /// context side by side.
    /// </summary>
    [Fact]
    public void Steady_runs_do_not_allocate_and_two_instances_share_a_context()
    {
        var env = VfxHardware.Require();
        using var stream = CudaStream.Create(env.Context, CudaStreamPriority.High);
        long Free() => env.Context.MemoryInfo.FreeBytes;
        var results = new List<string>();
        var effects = new List<VfgEffect>();
        var buffers = new List<CudaDeviceBuffer>();
        try
        {
            foreach (var format in new[] { PixelFormat.Rgba8, PixelFormat.Rgb10A2 })
            {
                var a = env.Pattern(Width, Height, format, 10);
                var b = env.Pattern(Width, Height, format, 12);
                var o = env.Image(Width, Height, format);
                buffers.AddRange([a, b, o]);
                var free0 = Free();
                var effect = VfgEffect.Create(stream, new VfgInstanceKey(Width, Height, format, VfgMode.Medium), true);
                effects.Add(effect);
                effect.Bind(VfgEffect.Previous, a.AsFrame());
                effect.Bind(VfgEffect.Current, b.AsFrame());
                effect.Bind(VfgEffect.Output, o.AsFrame());
                effect.Load();
                var free1 = Free();
                for (var i = 0; i < 4; i++)
                {
                    effect.Run(0.5f, i == 3);
                }

                stream.Synchronize();
                var free2 = Free();
                var own2 = Machine.ProcessGpuMemoryMiB();
                for (var i = 0; i < 200; i++)
                {
                    effect.Run(0.1f + (0.8f * (i % 5) / 4), i % 50 == 0);
                }

                stream.Synchronize();
                var free3 = Free();
                var own3 = Machine.ProcessGpuMemoryMiB();
                results.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{format}: load {(free0 - free1) / 1048576.0:0.0} MiB, first 4 runs {(free1 - free2) / 1048576.0:0.0} MiB more, " +
                    $"next 200 runs {(free2 - free3) / 1048576.0:0.0} MiB (this process {own2} -> {own3} MiB)"));

                // Free memory is device-wide, so the desktop's own GPU clients (browser, compositor) allocating meanwhile
                // move it too: 2 MiB once in a whole-solution run. This process's own figure (nvidia-smi, exact to the
                // 2 MiB page, checked against cuMemAlloc) says whose it was; without it the device-wide figure decides.
                var ours = own2 < 0 || own3 < 0 || own3 != own2;
                Assert.True(free2 - free3 < 2 << 20 || !ours,
                    $"steady runs changed free memory by {(free2 - free3) >> 20} MiB, this process's own by {own3 - own2} MiB");
                Assert.True(own2 < 0 || own3 == own2, $"steady runs changed this process's GPU memory from {own2} to {own3} MiB");
            }

            // Both run, alternately, on one stream in one context, and each still interpolates.
            for (var i = 0; i < 20; i++)
            {
                effects[i % 2].Run(0.5f, false);
            }

            stream.Synchronize();
            foreach (var (format, k) in new[] { (PixelFormat.Rgba8, 0), (PixelFormat.Rgb10A2, 1) })
            {
                var output = env.Read(buffers[(3 * k) + 2]);
                var prev = env.Read(buffers[3 * k]);
                var curr = env.Read(buffers[(3 * k) + 1]);
                var band = (Height * 7 / 12, Height * 9 / 12);
                Assert.True(Images.Compare(output, prev, Width, band.Item1, band.Item2, format).Mae > 0.5);
                Assert.True(Images.Compare(output, curr, Width, band.Item1, band.Item2, format).Mae > 0.5);
            }

            Log.WriteLine($"1080p Medium: {string.Join("; ", results)}; nvidia-smi: {Machine.ProcessGpuMemoryMiB()} MiB for this process with both loaded");
        }
        finally
        {
            foreach (var e in effects)
            {
                e.Dispose();
            }

            foreach (var x in buffers)
            {
                x.Dispose();
            }
        }
    }

    /// <summary>
    /// Mode and stream are fixed at load: setting either afterwards is accepted, but every later run fails with
    /// NVCV_ERR_INITIALIZATION until the effect is loaded again. So a quality change is a reload.
    /// </summary>
    [Fact]
    public void Changing_mode_or_stream_after_load_needs_a_reload()
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(640, 360, format, 10);
        using var b = env.Pattern(640, 360, format, 12);
        using var o = env.Image(640, 360, format);
        using var stream = CudaStream.Create(env.Context);
        using var other = CudaStream.Create(env.Context);
        using var effect = VfgEffect.Create(stream, new VfgInstanceKey(640, 360, format, VfgMode.Medium), true);
        effect.Bind(VfgEffect.Previous, a.AsFrame());
        effect.Bind(VfgEffect.Current, b.AsFrame());
        effect.Bind(VfgEffect.Output, o.AsFrame());
        effect.Load();
        effect.Run(0.5f, false);

        effect.SetU32(VfxSelectors.Mode, (uint)VfgMode.High);
        var afterMode = Assert.Throws<VfxException>(() => effect.Run(0.5f, false));
        Assert.Equal(NvCVStatus.ErrInitialization, afterMode.Status);
        var watch = Stopwatch.StartNew();
        effect.Load();
        Log.WriteLine($"after Mode := High: {afterMode.Message}; reload took {watch.Elapsed.TotalMilliseconds:0.0} ms");
        effect.Run(0.5f, false);
        Assert.Equal((uint)VfgMode.High, effect.GetU32(VfxSelectors.Mode));

        unsafe
        {
            var status = VfxNative.NvVFX_SetCudaStream(effect.Handle, VfxSelectors.CudaStream, other.Handle);
            Assert.Equal(NvCVStatus.Success, status);
        }

        var afterStream = Assert.Throws<VfxException>(() => effect.Run(0.5f, false));
        Assert.Equal(NvCVStatus.ErrInitialization, afterStream.Status);
        effect.Load();
        effect.Run(0.5f, false);
        other.Synchronize();
        stream.Synchronize();
    }

    /// <summary>
    /// Shot changes. The manual flag outputs the current frame exactly (GeneratorTests) and skips the model; here its
    /// cost against a normal run. Automatic detection on a real cut (the pattern followed by an unrelated texture) runs
    /// the whole model and then outputs a real frame unchanged; which one, for t = 1/4, 1/2 and 3/4, is logged.
    /// </summary>
    [Fact]
    public void Shot_changes_cost_and_output()
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 10);
        using var b = env.Pattern(Width, Height, format, 12);
        using var cut = env.Upload(Width, Height, format, Images.Texture(Width, Height, 0, format));
        using var o = env.Image(Width, Height, format);
        using var done = CudaEvent.Create(env.Context);
        using var generator = env.Generator(Width, Height, format);

        List<double> Time(Action enqueue, int n)
        {
            var times = new List<double>();
            for (var i = 0; i < n; i++)
            {
                enqueue();
                done.Synchronize();
                Assert.True(generator.TryReadGpuTime(out var ns));
                times.Add(ns);
            }

            return times;
        }

        var normal = Time(() => generator.Enqueue(a.AsFrame(contentTimeNs: 1), b.AsFrame(contentTimeNs: 2), 0.5f, o.AsFrame(), done.Signal), 60);
        var manual = Time(() =>
        {
            generator.NotifyDiscontinuity(2);
            generator.Enqueue(a.AsFrame(contentTimeNs: 1), b.AsFrame(contentTimeNs: 2), 0.5f, o.AsFrame(), done.Signal);
        }, 60);

        // Later content times (past the notified cut, which they release), so these runs rely on automatic detection.
        var automatic = Time(() => generator.Enqueue(a.AsFrame(contentTimeNs: 3), cut.AsFrame(contentTimeNs: 4), 0.5f, o.AsFrame(), done.Signal), 60);
        Log.WriteLine($"GPU p50: normal run {Log.Ms(Median(normal))} ms, manual shot change {Log.Ms(Median(manual))} ms, " +
                      $"a real cut with automatic detection {Log.Ms(Median(automatic))} ms");
        Assert.True(Median(manual) < Median(normal) / 4, "a manual shot change was expected to skip the model");

        var prev = env.Read(a);
        var curr = env.Read(cut);
        foreach (var t in new[] { 0.25f, 0.5f, 0.75f })
        {
            generator.Enqueue(a.AsFrame(contentTimeNs: 3), cut.AsFrame(contentTimeNs: 4), t, o.AsFrame(), done.Signal);
            done.Synchronize();
            var output = env.Read(o);
            var isPrev = output.AsSpan().SequenceEqual(prev);
            var isCurr = output.AsSpan().SequenceEqual(curr);
            Log.WriteLine($"cut at t = {t}: output is {(isPrev ? "prev, unchanged" : isCurr ? "curr, unchanged" : "neither")} " +
                          $"(vs prev {Images.Compare(output, prev, Width, 0, Height, format)}, vs curr {Images.Compare(output, curr, Width, 0, Height, format)})");
            Assert.True(isPrev || isCurr, "on a detected cut the output was expected to be a real frame, unchanged");
        }
    }

    /// <summary>
    /// The SDK keeps the pointer to the descriptor handed to <c>NvVFX_SetImage</c> and reads it at every run (review
    /// finding: nvVideoEffects.h says it takes a copy). Rewriting the output descriptor in place, without calling
    /// SetImage again, moves the next run's output; so <see cref="VfgEffect"/>'s descriptors must live, unmoved, as long
    /// as the effect, which they do (native memory freed only after NvVFX_DestroyEffect). If an SDK update starts copying,
    /// this fails and the generator's descriptor handling can be simplified.
    /// </summary>
    [Fact]
    public unsafe void The_sdk_reads_the_descriptor_it_was_given_at_every_run()
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        const int w = 640, h = 360;
        const uint marker = 0xFF_7B_11_E5;
        using var a = env.Pattern(w, h, format, 20);
        using var b = env.Pattern(w, h, format, 22);
        using var first = env.Image(w, h, format, marker);
        using var second = env.Image(w, h, format, marker);
        using var stream = CudaStream.Create(env.Context);
        using var effect = VfgEffect.Create(stream, new VfgInstanceKey(w, h, format, VfgMode.Medium), true);
        effect.Bind(VfgEffect.Previous, a.AsFrame());
        effect.Bind(VfgEffect.Current, b.AsFrame());
        effect.Bind(VfgEffect.Output, first.AsFrame());
        effect.Load();

        var descriptor = effect.Descriptor(VfgEffect.Output);
        Assert.Equal((ulong)first.Address, (ulong)descriptor->Pixels);
        descriptor->Pixels = (void*)second.Address; // in place, no NvVFX_SetImage
        effect.Run(0.5f, false);
        stream.Synchronize();
        var wroteFirst = !env.Read(first).All(p => p == marker);
        var wroteSecond = !env.Read(second).All(p => p == marker);
        Log.WriteLine($"descriptor rewritten in place without SetImage: run wrote the first buffer {wroteFirst}, the second {wroteSecond}");
        Assert.True(wroteSecond && !wroteFirst, "the SDK no longer reads the caller's descriptor at run time");

        // Bind's own rewrite (same descriptor, SetImage again) puts it back; the descriptor never moves.
        effect.InvalidateBindings();
        effect.Bind(VfgEffect.Output, first.AsFrame());
        Assert.Equal((nint)descriptor, (nint)effect.Descriptor(VfgEffect.Output));
        effect.Run(0.5f, false);
        stream.Synchronize();
        Assert.False(env.Read(first).All(p => p == marker));
    }

    /// <summary>
    /// The failure mode of turning automatic shot-change detection off without a cut detector: a hard cut that nobody
    /// reports is interpolated, and the output is a morph of the two shots, neither real frame (with detection on the
    /// SDK outputs a real frame unchanged, Shot_changes_cost_and_output). Measured per mode for the results document.
    /// </summary>
    [Theory]
    [InlineData(VfgMode.Low)]
    [InlineData(VfgMode.Medium)]
    [InlineData(VfgMode.High)]
    public void Without_detection_an_unreported_cut_is_morphed(VfgMode mode)
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 10);
        using var cut = env.Upload(Width, Height, format, Images.Texture(Width, Height, 0, format));
        using var o = env.Image(Width, Height, format);
        using var done = CudaEvent.Create(env.Context);
        var quality = mode switch { VfgMode.Low => FrameGenQuality.Low, VfgMode.High => FrameGenQuality.High, _ => FrameGenQuality.Medium };
        using var generator = env.Generator(Width, Height, format, quality, new VfgOptions { AutomaticShotChangeDetection = false });
        generator.Enqueue(a.AsFrame(contentTimeNs: 1), cut.AsFrame(contentTimeNs: 2), 0.5f, o.AsFrame(), done.Signal);
        done.Synchronize();
        var output = env.Read(o);
        var prev = env.Read(a);
        var curr = env.Read(cut);
        var toPrev = Images.Compare(output, prev, Width, 0, Height, format);
        var toCurr = Images.Compare(output, curr, Width, 0, Height, format);
        var toBlend = Images.Compare(output, Images.Blend(prev, curr, 0.5, format), Width, 0, Height, format);
        Log.WriteLine($"{mode}, detection off, an unreported cut at t = 0.5: vs prev {toPrev}; vs curr {toCurr}; vs the blend {toBlend}");
        Assert.True(toPrev.Mae > 10 && toCurr.Mae > 10, "with detection off a cut was expected to come out as neither real frame");
    }

    /// <summary>
    /// VFG near t = 0 and 1: a pan by whole pixels over a detailed texture, with the truth at every pixel of the motion.
    /// For each t, the error of VFG against the truth and of the nearest real frame (what the scheduler shows when it
    /// snaps t to 0 or 1): where VFG stops beating the real frame is where snapping costs nothing.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Rgba8, 40)]
    [InlineData(PixelFormat.Rgba8, 10)]
    [InlineData(PixelFormat.Rgb10A2, 40)]
    public void Timestep_sweep_near_zero_and_one(PixelFormat format, int motion)
    {
        var env = VfxHardware.Require();
        const int w = 1280, h = 720;
        using var prev = env.Upload(w, h, format, Images.Texture(w, h, 0, format));
        using var curr = env.Upload(w, h, format, Images.Texture(w, h, motion, format));
        using var o = env.Image(w, h, format);
        using var done = CudaEvent.Create(env.Context);
        using var generator = env.Generator(w, h, format);
        var p = env.Read(prev);
        var c = env.Read(curr);
        var lines = new List<string>();
        var wins = 0;
        var middle = 0;
        foreach (var step in new[] { 1, 2, 4, 6, 8, 10, 15, 20, 25, 30, 32, 34, 36, 38, 39 }.Where(s => s < motion || motion == 40))
        {
            var t = step / 40f;
            var shift = (int)Math.Round(t * motion);
            if (Math.Abs((t * motion) - shift) > 1e-4)
            {
                continue; // only where the truth is a whole-pixel shift
            }

            generator.Enqueue(prev.AsFrame(contentTimeNs: 1), curr.AsFrame(contentTimeNs: 2), t, o.AsFrame(), done.Signal);
            done.Synchronize();
            var truth = Images.Texture(w, h, shift, format);
            var vfg = Interior(env.Read(o), truth, w, h, motion, format);
            var nearest = Interior(t < 0.5 ? p : c, truth, w, h, motion, format);
            var blend = Interior(Images.Blend(p, c, t, format), truth, w, h, motion, format);
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"t {t:0.000}: VFG {vfg.Psnr:0.0} dB, nearest real frame {nearest.Psnr:0.0} dB, blend {blend.Psnr:0.0} dB"));
            if (t >= 0.25f && t <= 0.75f)
            {
                middle++;
                wins += vfg.Psnr > nearest.Psnr ? 1 : 0;
            }
        }

        Log.WriteLine($"{format}, pan of {motion} px at 1280x720, PSNR against the truth:\n  {string.Join("\n  ", lines)}");
        Assert.Equal(middle, wins);
    }

    private static ImageError Interior(uint[] a, uint[] b, int w, int h, int motion, PixelFormat format)
    {
        // Away from the right edge, where content enters the frame.
        var x0 = 16;
        var width = w - motion - 32;
        var ca = new uint[width * h];
        var cb = new uint[width * h];
        for (var y = 0; y < h; y++)
        {
            Array.Copy(a, (y * w) + x0, ca, y * width, width);
            Array.Copy(b, (y * w) + x0, cb, y * width, width);
        }

        return Images.Compare(ca, cb, width, 0, h, format);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private static double ThreadCpuNs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        Timespec ts;
        unsafe
        {
            _ = ClockGetTime(3, &ts); // CLOCK_THREAD_CPUTIME_ID
        }

        return (ts.Seconds * 1e9) + ts.Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    private static unsafe partial int ClockGetTime(int clock, Timespec* ts);

    /// <summary>Another thread doing ordinary CUDA work (an event record and a tiny kernel per turn), timing each turn.</summary>
    private sealed class OtherThread : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        private double _worst;

        public OtherThread(VfxEnvironment env)
        {
            _thread = new Thread(() =>
            {
                using var stream = CudaStream.Create(env.Context);
                using var e = CudaEvent.Create(env.Context);
                while (!_stop)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    e.Record(stream);
                    env.Kernels.EnqueueDelay(stream, 1000);
                    var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    if (ms > _worst)
                    {
                        _worst = ms;
                    }

                    Calls++;
                    stream.Synchronize();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true, Name = "other-cuda" };
            _thread.Start();
        }

        public long Calls { get; private set; }

        public double WorstMs => _worst;

        public void ResetWorst() => _worst = 0;

        public void Dispose()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }
}
