using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vfx.Tests.Support;

namespace FrameMotionPlus.Vfx.Tests;

/// <summary>
/// <see cref="VfgGenerator"/> behind <see cref="IFrameGenerator"/>: output rebinding, the GPU-side waits and the done
/// signal (each with a control that removes the synchronisation and shows the test then sees the failure), shot
/// changes, t = 0 and 1, configuration switching and every error path.
/// </summary>
[Trait(Category.Name, Category.Vfx)]
public class GeneratorTests
{
    private const int Width = 640;
    private const int Height = 360;

    /// <summary>A marker no pattern, blend or VFG output of the pattern produces (opaque magenta-ish in 8-bit).</summary>
    private const uint Marker = 0xFF_7B_11_E5;

    [Fact]
    public void Capabilities_are_what_vfg_supports()
    {
        var caps = VfgGenerator.CapabilitiesFor(automaticShotChangeDetection: true);
        Assert.True(caps.EnqueueMayBlock); // the call returns when the frame is generated (CharacterizationTests)
        Assert.False(VfgGenerator.CapabilitiesFor(automaticShotChangeDetection: false).EnqueueMayBlock);
        Assert.Equal(caps with { EnqueueMayBlock = false }, VfgGenerator.CapabilitiesFor(false));
        Assert.Equal(8, caps.MaxMultiplier);
        Assert.True(caps.SupportsArbitraryTimestep);
        Assert.False(caps.SupportsExtrapolation);
        Assert.False(caps.SupportsScaling);
        Assert.Equal(PixelFormatSet.Rgba8 | PixelFormatSet.Bgra8 | PixelFormatSet.Rgb10A2, caps.Formats);
        Assert.Equal(MemoryDomain.CudaDevice, caps.Domain);
        Assert.Equal(VfgMode.Low, VfgInstanceKey.ModeOf(FrameGenQuality.Low));
        Assert.Equal(VfgMode.Medium, VfgInstanceKey.ModeOf(FrameGenQuality.Default));
        Assert.Equal(VfgMode.Medium, VfgInstanceKey.ModeOf(FrameGenQuality.Medium));
        Assert.Equal(VfgMode.High, VfgInstanceKey.ModeOf(FrameGenQuality.High));
    }

    /// <summary>
    /// The output descriptor is pointed at a different buffer every run (as at a ring slot each time): each run's frame
    /// lands in its own buffer, complete, and no other buffer is touched. Inputs are swapped between runs too.
    /// </summary>
    [Fact]
    public void Each_run_writes_into_the_output_it_is_given_and_nowhere_else()
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 20);
        using var b = env.Pattern(Width, Height, format, 22);
        var outputs = Enumerable.Range(0, 4).Select(_ => env.Image(Width, Height, format, Marker)).ToArray();
        try
        {
            using var generator = env.Generator(Width, Height, format);
            using var done = CudaEvent.Create(env.Context);

            // Reference outputs, each made into a fresh buffer of its own.
            using var refForward = env.Image(Width, Height, format);
            using var refBackward = env.Image(Width, Height, format);
            generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, refForward.AsFrame(), done.Signal);
            generator.Enqueue(b.AsFrame(), a.AsFrame(), 0.5f, refBackward.AsFrame(), done.Signal);
            done.Synchronize();
            var forward = env.Read(refForward);
            var backward = env.Read(refBackward);
            var (y0, y1) = (Height * 7 / 12, Height * 9 / 12);
            Assert.True(Images.Compare(forward, backward, Width, y0, y1, format).Mae < 0.5, "the direction of the pair should not matter at t = 0.5");

            for (var i = 0; i < outputs.Length; i++)
            {
                var swap = (i & 1) == 1;
                generator.Enqueue(swap ? b.AsFrame() : a.AsFrame(), swap ? a.AsFrame() : b.AsFrame(), 0.5f, outputs[i].AsFrame(), done.Signal);
                done.Synchronize();
                for (var j = 0; j < outputs.Length; j++)
                {
                    var pixels = env.Read(outputs[j]);
                    if (j <= i)
                    {
                        var expected = (j & 1) == 1 ? backward : forward;
                        Assert.True(pixels.AsSpan().SequenceEqual(expected), $"output {j} after run {i} is not that run's frame");
                    }
                    else
                    {
                        Assert.All(pixels, p => Assert.Equal(Marker, p));
                    }
                }
            }
        }
        finally
        {
            foreach (var o in outputs)
            {
                o.Dispose();
            }
        }
    }

    /// <summary>
    /// Frames with the pitches a ring slot can have: an odd width with a tight, only 4-byte aligned pitch (1277 px,
    /// 5108 B, as a Vulkan-exported slot of that width has), and an output inside a larger allocation with guard bytes
    /// before each row, after it and above and below the image. VFG honours every pitch and offset: its output equals
    /// the one written into an ordinary pitched buffer, and no guard byte changes.
    /// </summary>
    [Fact]
    public unsafe void Pitches_and_offsets_of_ring_slots_are_honoured_and_nothing_outside_is_written()
    {
        var env = VfxHardware.Require();
        const int w = 1277, h = 719;
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(w, h, format, 20);
        using var b = env.Pattern(w, h, format, 22);
        using var reference = env.Image(w, h, format);
        using var generator = env.Generator(w, h, format);
        using var done = CudaEvent.Create(env.Context);
        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, reference.AsFrame(), done.Signal);
        done.Synchronize();
        var expected = env.Read(reference);

        // Tight pitch (5108 B) for an input and the output.
        using var tightA = CudaDeviceBuffer.Allocate(env.Context, (long)w * 4 * h);
        using var tightO = CudaDeviceBuffer.Allocate(env.Context, (long)w * 4 * h);
        var frameA = tightA.AsFrame(w, h, format, w * 4);
        var frameO = tightO.AsFrame(w, h, format, w * 4);
        using (var s = CudaStream.Create(env.Context))
        {
            s.CopyFrameAsync(a.AsFrame(), frameA);
            s.FillFrameAsync(frameO, Marker);
            s.Synchronize();
        }

        generator.Enqueue(frameA, b.AsFrame(), 0.5f, frameO, done.Signal);
        done.Synchronize();
        Assert.True(env.Read(frameO).AsSpan().SequenceEqual(expected), "tight-pitch frames gave a different output");

        // An output view 16 B into each row of a padded allocation, with 2 guard rows above and below.
        const byte guard = 0xA5;
        const int lead = 16, extra = 44, guardRows = 2;
        var pitch = lead + (w * 4) + extra;
        using var padded = CudaDeviceBuffer.Allocate(env.Context, (long)pitch * (h + (2 * guardRows)));
        var view = padded.AsFrame(w, h, format, pitch, offset: ((long)guardRows * pitch) + lead);
        using (var s = CudaStream.Create(env.Context))
        {
            s.MemsetAsync(padded.Address, guard, padded.SizeBytes);
            s.Synchronize();
        }

        for (var i = 0; i < 3; i++)
        {
            generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, view, done.Signal);
        }

        done.Synchronize();
        Assert.True(env.Read(view).AsSpan().SequenceEqual(expected), "the padded output view gave a different output");
        var raw = new byte[padded.SizeBytes];
        using (var s = CudaStream.Create(env.Context))
        {
            s.CopyToHost<byte>(padded.Address, raw);
        }

        var start = (long)view.Offset;
        for (long i = 0; i < raw.Length; i++)
        {
            var inside = i >= start && (i - start) / pitch < h && (i - start) % pitch < w * 4;
            if (!inside && raw[i] != guard)
            {
                Assert.Fail($"byte {i} outside the output view was written (0x{raw[i]:x2})");
            }
        }
    }

    /// <summary>
    /// <c>done</c> is signalled after the output is complete: a consumer on another stream that waits on it and copies
    /// the output reads the finished frame, though the generator's stream is held back 300 ms by a GPU-side wait.
    /// Control: the same copy without the wait, enqueued at the same moment, reads the output before it is written (it
    /// still holds the marker). With automatic shot-change detection off, Enqueue returns before the GPU work runs, so
    /// the control can fail. With it on, Enqueue itself blocks in NvVFX_Run until the gate has opened and the generation
    /// is nearly done, so a consumer can only wait after it returns; that variant checks the waiting consumer alone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Done_is_signalled_after_the_output_is_written(bool shotChangeDetection)
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 20);
        using var b = env.Pattern(Width, Height, format, 22);
        using var expectedBuffer = env.Image(Width, Height, format);
        using var output = env.Image(Width, Height, format, Marker);
        using var waited = env.Image(Width, Height, format);
        using var unwaited = env.Image(Width, Height, format);
        using var generator = env.Generator(Width, Height, format, options: new VfgOptions { AutomaticShotChangeDetection = shotChangeDetection });
        using var done = CudaEvent.Create(env.Context);
        using var gateStream = CudaStream.Create(env.Context);
        using var gate = CudaEvent.Create(env.Context);
        using var consumer = CudaStream.Create(env.Context);
        using var control = CudaStream.Create(env.Context);

        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, expectedBuffer.AsFrame(), done.Signal);
        done.Synchronize();
        var expected = env.Read(expectedBuffer);

        // What the capability says is what the call does, measured just below.
        Assert.Equal(shotChangeDetection, generator.Capabilities.EnqueueMayBlock);

        // The generator's stream waits for 'gate', recorded behind 300 ms of GPU work: prev is "not ready" until then.
        env.Kernels.EnqueueDelay(gateStream, 300_000_000);
        gate.Record(gateStream);
        var prev = a.AsFrame() with { Ready = gate.Signal };
        var watch = Stopwatch.StartNew();
        generator.Enqueue(prev, b.AsFrame(), 0.5f, output.AsFrame(), done.Signal);
        var enqueueMs = watch.Elapsed.TotalMilliseconds;
        control.CopyFrameAsync(output.AsFrame(), unwaited.AsFrame());
        consumer.Wait(done.Signal);
        consumer.CopyFrameAsync(output.AsFrame(), waited.AsFrame());
        consumer.Synchronize();
        control.Synchronize();
        gateStream.Synchronize();
        Log.WriteLine($"detection {(shotChangeDetection ? "on" : "off")}: Enqueue returned after {enqueueMs:0.0} ms with the stream gated for 300 ms");

        Assert.True(env.Read(waited).AsSpan().SequenceEqual(expected), "the consumer that waited on done read an unfinished output");
        if (shotChangeDetection)
        {
            Assert.True(enqueueMs > 250, $"with detection on Enqueue was expected to block behind the gate; it took {enqueueMs:0.0} ms");
        }
        else
        {
            Assert.True(enqueueMs < 100, $"with detection off Enqueue should only enqueue; it took {enqueueMs:0.0} ms");
            Assert.All(env.Read(unwaited), p => Assert.Equal(Marker, p)); // control: without the wait it is read too early
        }
    }

    /// <summary>
    /// The generator waits GPU-side on prev.Ready, curr.Ready and output.Ready: each is recorded behind a 200 ms delay
    /// and the content written (or, for the output, the old content read away) only then. Control: with Ready = None
    /// the generator reads the stale input (the marker) and its output differs.
    /// </summary>
    [Theory]
    [InlineData("prev")]
    [InlineData("curr")]
    [InlineData("output")]
    public void The_ready_signals_of_inputs_and_output_are_waited_for(string which)
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 20);
        using var b = env.Pattern(Width, Height, format, 22);
        using var reference = env.Image(Width, Height, format);
        using var generator = env.Generator(Width, Height, format, options: new VfgOptions { AutomaticShotChangeDetection = false });
        using var done = CudaEvent.Create(env.Context);
        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, reference.AsFrame(), done.Signal);
        done.Synchronize();
        var expected = env.Read(reference);

        uint[] Run(bool wait)
        {
            using var producer = CudaStream.Create(env.Context);
            using var ready = CudaEvent.Create(env.Context);
            using var prev = env.Image(Width, Height, format, Marker);
            using var curr = env.Image(Width, Height, format, Marker);
            using var output = env.Image(Width, Height, format, Marker);
            using var saved = env.Image(Width, Height, format);
            using var setup = CudaStream.Create(env.Context);
            setup.CopyFrameAsync(a.AsFrame(), prev.AsFrame());
            setup.CopyFrameAsync(b.AsFrame(), curr.AsFrame());
            setup.Synchronize();

            // After 200 ms: the input is (re)written, or the output's old content is read away into 'saved'.
            env.Kernels.EnqueueDelay(producer, 200_000_000);
            switch (which)
            {
                case "prev":
                    producer.FillFrameAsync(prev.AsFrame(), Marker);
                    producer.CopyFrameAsync(a.AsFrame(), prev.AsFrame());
                    break;
                case "curr":
                    producer.FillFrameAsync(curr.AsFrame(), Marker);
                    producer.CopyFrameAsync(b.AsFrame(), curr.AsFrame());
                    break;
                default:
                    producer.CopyFrameAsync(output.AsFrame(), saved.AsFrame());
                    break;
            }

            ready.Record(producer);
            var signal = wait ? ready.Signal : GpuSignal.None;
            if (which == "prev")
            {
                // Make prev stale until then, so reading it early shows.
                setup.FillFrameAsync(prev.AsFrame(), Marker);
                setup.Synchronize();
            }
            else if (which == "curr")
            {
                setup.FillFrameAsync(curr.AsFrame(), Marker);
                setup.Synchronize();
            }

            generator.Enqueue(
                prev.AsFrame() with { Ready = which == "prev" ? signal : GpuSignal.None },
                curr.AsFrame() with { Ready = which == "curr" ? signal : GpuSignal.None },
                0.5f,
                output.AsFrame() with { Ready = which == "output" ? signal : GpuSignal.None },
                done.Signal);
            done.Synchronize();
            producer.Synchronize();
            return which == "output" ? env.Read(saved) : env.Read(output);
        }

        var honoured = Run(wait: true);
        var ignored = Run(wait: false);
        if (which == "output")
        {
            // The old content was read away before VFG overwrote it only when the wait was honoured.
            Assert.All(honoured, p => Assert.Equal(Marker, p));
            Assert.True(ignored.AsSpan().SequenceEqual(expected), "control: without the wait VFG writes before the old content is read");
        }
        else
        {
            Assert.True(honoured.AsSpan().SequenceEqual(expected), $"{which}.Ready was not waited for");
            Assert.False(ignored.AsSpan().SequenceEqual(expected), $"control: without {which}.Ready the stale input was not seen");
        }
    }

    /// <summary>
    /// Core's "next pair" across a real cut (the pattern followed by an unrelated texture), 2x and 4x, detection on and
    /// off: after NotifyDiscontinuity every generated frame of the pair is the current frame, exactly (VFG's manual
    /// shot-change flag, set for every run of the pair), even when the pair is revisited; t = 0 shows prev and does not
    /// use the notification up; the next pair interpolates again. Review finding: the flag used to cover one call, so 4x
    /// gave curr, prev, curr with detection on (stepping back and forth) and curr then two morphs with it off, and a t = 0
    /// call consumed it.
    /// </summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(true, 4)]
    [InlineData(false, 2)]
    [InlineData(false, 4)]
    public void A_discontinuity_covers_every_frame_of_the_pair_across_the_cut(bool shotChangeDetection, int multiplier)
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 20);
        using var cut = env.Upload(Width, Height, format, Images.Texture(Width, Height, 0, format));
        using var after = env.Upload(Width, Height, format, Images.Texture(Width, Height, 6, format));
        using var output = env.Image(Width, Height, format);
        using var generator = env.Generator(Width, Height, format, options: new VfgOptions { AutomaticShotChangeDetection = shotChangeDetection });
        using var done = CudaEvent.Create(env.Context);
        var prev = env.Read(a);
        var curr = env.Read(cut);
        uint[] Run(CudaDeviceBuffer p, CudaDeviceBuffer q, float t)
        {
            generator.Enqueue(p.AsFrame(), q.AsFrame(), t, output.AsFrame(), done.Signal);
            done.Synchronize();
            return env.Read(output);
        }

        float[] timesteps = multiplier == 2 ? [0.5f] : [0.25f, 0.5f, 0.75f];
        generator.NotifyDiscontinuity();
        foreach (var t in timesteps.Concat(timesteps))
        {
            Assert.True(Run(a, cut, t).AsSpan().SequenceEqual(curr), $"{multiplier}x t={t} across the cut is not the current frame");
        }

        Assert.Equal(2 * timesteps.Length, generator.ShotChangeRuns);

        // t = 0 is prev itself, and the pair stays cut after it.
        generator.NotifyDiscontinuity();
        Assert.True(Run(a, cut, 0f).AsSpan().SequenceEqual(prev), "t = 0 after a cut is not the previous frame");
        Assert.True(Run(a, cut, 0.5f).AsSpan().SequenceEqual(curr), "a t = 0 call used the notification up");

        // The next pair (the new shot moving on) is interpolated, not copied.
        var next = Run(cut, after, 0.5f);
        Assert.False(next.AsSpan().SequenceEqual(curr), "the pair after the cut was treated as a cut too");
        Assert.False(next.AsSpan().SequenceEqual(env.Read(after)), "the pair after the cut was treated as a cut too");
    }

    /// <summary>
    /// With detection off, runs queued behind a GPU gate (none executed yet) each keep the output, timestep and shot-change
    /// flag they were enqueued with, although the generator rewrites the same descriptors and parameters in place for the
    /// next call: NvVFX_Run captures them when it enqueues. VfgEffect's descriptor invariant (rewritten only after the
    /// previous Run returned) relies on this. Adopted from the review's probes.
    /// </summary>
    [Fact]
    public void Queued_runs_keep_what_they_were_enqueued_with()
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 16);
        using var b = env.Pattern(Width, Height, format, 24);
        using var c = env.Pattern(Width, Height, format, 32);
        using var done = CudaEvent.Create(env.Context);
        using var generator = env.Generator(Width, Height, format, options: new VfgOptions { AutomaticShotChangeDetection = false });
        (CudaDeviceBuffer P, CudaDeviceBuffer Q, float T, bool Cut)[] runs = [(a, b, 0.5f, true), (b, c, 0.25f, false), (b, c, 0.75f, false), (b, c, 0.5f, false)];
        var expected = new List<uint[]>();
        using (var reference = env.Image(Width, Height, format))
        {
            foreach (var run in runs)
            {
                if (run.Cut)
                {
                    generator.NotifyDiscontinuity();
                }

                generator.Enqueue(run.P.AsFrame(), run.Q.AsFrame(), run.T, reference.AsFrame(), done.Signal);
                done.Synchronize();
                expected.Add(env.Read(reference));
            }
        }

        Assert.True(expected[0].AsSpan().SequenceEqual(env.Read(b)));
        var outputs = runs.Select(_ => env.Image(Width, Height, format, 0xFF_7B_11_E5)).ToArray();
        try
        {
            using var gateStream = CudaStream.Create(env.Context);
            using var gate = CudaEvent.Create(env.Context);
            env.Kernels.EnqueueDelay(gateStream, 200_000_000);
            gate.Record(gateStream);
            for (var i = 0; i < runs.Length; i++)
            {
                if (runs[i].Cut)
                {
                    generator.NotifyDiscontinuity();
                }

                var prev = runs[i].P.AsFrame() with { Ready = i == 0 ? gate.Signal : GpuSignal.None };
                generator.Enqueue(prev, runs[i].Q.AsFrame(), runs[i].T, outputs[i].AsFrame(), done.Signal);
            }

            Assert.False(gate.IsComplete, "the gate opened before every run was enqueued; the test proves nothing");
            done.Synchronize();
            for (var i = 0; i < runs.Length; i++)
            {
                Assert.True(env.Read(outputs[i]).AsSpan().SequenceEqual(expected[i]), $"queued run {i} (t = {runs[i].T}, cut {runs[i].Cut}) did not produce its own output");
            }
        }
        finally
        {
            foreach (var output in outputs)
            {
                output.Dispose();
            }
        }
    }

    /// <summary>The manual flag covers the pair and is cleared after each run: a later pair is not a copy (both settings).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void After_the_cut_pair_the_flag_is_clear(bool shotChangeDetection)
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgb10A2;
        using var a = env.Pattern(Width, Height, format, 20);
        using var b = env.Pattern(Width, Height, format, 22);
        using var c = env.Pattern(Width, Height, format, 24);
        using var output = env.Image(Width, Height, format);
        using var generator = env.Generator(Width, Height, format, options: new VfgOptions { AutomaticShotChangeDetection = shotChangeDetection });
        using var done = CudaEvent.Create(env.Context);

        generator.NotifyDiscontinuity();
        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, output.AsFrame(), done.Signal);
        done.Synchronize();
        Assert.True(env.Read(output).AsSpan().SequenceEqual(env.Read(b)), "a shot-change run should output the current frame");

        generator.Enqueue(b.AsFrame(), c.AsFrame(), 0.5f, output.AsFrame(), done.Signal);
        done.Synchronize();
        Assert.False(env.Read(output).AsSpan().SequenceEqual(env.Read(c)), "the flag was not cleared after its pair");
        Assert.Equal(1, generator.ShotChangeRuns);
        Assert.Equal(2, generator.Runs);
    }

    [Fact]
    public void T_zero_and_one_show_the_real_frames_and_are_timed()
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 20);
        using var b = env.Pattern(Width, Height, format, 22);
        using var output = env.Image(Width, Height, format);
        using var generator = env.Generator(Width, Height, format);
        using var done = CudaEvent.Create(env.Context);
        while (generator.TryReadGpuTime(out _))
        {
        }

        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0f, output.AsFrame(), done.Signal);
        done.Synchronize();
        Assert.True(env.Read(output).AsSpan().SequenceEqual(env.Read(a)));
        generator.Enqueue(a.AsFrame(), b.AsFrame(), 1f, output.AsFrame(), done.Signal);
        done.Synchronize();
        Assert.True(env.Read(output).AsSpan().SequenceEqual(env.Read(b)));
        Assert.Equal(2, generator.RealFrameCopies);
        Assert.Equal(0, generator.Runs);
        Assert.True(generator.TryReadGpuTime(out var first) && first > 0);
        Assert.True(generator.TryReadGpuTime(out var second) && second > 0);
    }

    /// <summary>
    /// Prepared instances switch without a load (SDR and HDR both kept loaded, as the design doc wants); the evicted
    /// instance beyond the limit is the least recently used one; ReleaseInactive keeps only the active one.
    /// </summary>
    [Fact]
    public void Prepared_configurations_switch_without_loading_and_old_ones_are_evicted()
    {
        var env = VfxHardware.Require();
        using var generator = new VfgGenerator(env.Context, new VfgOptions { MaxLoadedInstances = 2 });
        var sdr = new FrameGenConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8, Quality = FrameGenQuality.Medium };
        var hdr = sdr with { Format = PixelFormat.Rgb10A2 };
        var low = sdr with { Quality = FrameGenQuality.Low };

        generator.Configure(sdr);
        var load = generator.Prepare(hdr);
        Assert.Equal(new VfgInstanceKey(Width, Height, PixelFormat.Rgb10A2, VfgMode.Medium), load.Key);
        Assert.True(load.LoadNs > 0);
        Assert.True(generator.IsPrepared(hdr));
        Assert.Equal(2, generator.LoadedInstances);

        var watch = Stopwatch.StartNew();
        generator.Configure(hdr);
        generator.Configure(sdr);
        generator.Configure(hdr);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(5), $"switching prepared instances took {watch.Elapsed.TotalMilliseconds} ms");
        Assert.Equal(hdr, generator.Config);
        Assert.Equal(default, generator.Prepare(hdr)); // already loaded: nothing to do

        // Runs through the switched-to instance.
        using var a = env.Pattern(Width, Height, PixelFormat.Rgb10A2, 20);
        using var b = env.Pattern(Width, Height, PixelFormat.Rgb10A2, 22);
        using var output = env.Image(Width, Height, PixelFormat.Rgb10A2);
        using var done = CudaEvent.Create(env.Context);
        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, output.AsFrame(), done.Signal);
        done.Synchronize();

        // A third configuration evicts the least recently used one (sdr), not the active one.
        generator.Configure(low);
        Assert.Equal(2, generator.LoadedInstances);
        Assert.True(generator.IsPrepared(hdr));
        Assert.False(generator.IsPrepared(sdr));
        generator.ReleaseInactive();
        Assert.Equal(1, generator.LoadedInstances);
        Assert.True(generator.IsPrepared(low));
    }

    /// <summary>
    /// A switch that never loads (review: Configure may load on any thread): TryConfigurePrepared switches only to a
    /// loaded instance, and leaves everything as it was otherwise.
    /// </summary>
    [Fact]
    public void A_prepared_only_switch_never_loads()
    {
        var env = VfxHardware.Require();
        using var generator = new VfgGenerator(env.Context);
        var sdr = new FrameGenConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8, Quality = FrameGenQuality.Medium };
        var hdr = sdr with { Format = PixelFormat.Rgb10A2 };
        Assert.False(generator.TryConfigurePrepared(sdr));
        Assert.False(generator.IsConfigured);
        generator.Configure(sdr);
        var loads = generator.Loads;
        Assert.False(generator.TryConfigurePrepared(hdr));
        Assert.Equal(sdr, generator.Config);
        Assert.False(generator.TryConfigurePrepared(sdr with { Format = PixelFormat.Unknown }));
        Assert.False(generator.TryConfigurePrepared(sdr with { Width = 0 }));
        generator.Prepare(hdr);
        Assert.True(generator.TryConfigurePrepared(hdr));
        Assert.Equal(hdr, generator.Config);
        Assert.True(generator.TryConfigurePrepared(sdr));
        Assert.Equal(loads + 1, generator.Loads); // only the Prepare loaded
    }

    /// <summary>
    /// A failed SDK call leaves the instance faulted (review: Configure with the same configuration used to be a no-op
    /// and every later call threw): the failing call enqueues no done signal, later calls say the instance is faulted,
    /// a prepared-only switch refuses it, and Configure loads it again, after which it generates normally.
    /// </summary>
    [Fact]
    public unsafe void A_failed_run_faults_the_instance_until_it_is_loaded_again()
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 20);
        using var b = env.Pattern(Width, Height, format, 22);
        using var output = env.Image(Width, Height, format);
        using var done = CudaEvent.Create(env.Context);
        using var generator = env.Generator(Width, Height, format);
        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, output.AsFrame(), done.Signal);
        done.Synchronize();
        var expected = env.Read(output);

        // Break the loaded instance the way CharacterizationTests found: a mode change after load.
        generator.ActiveEffect!.SetU32(Interop.VfxSelectors.Mode, (uint)VfgMode.High);
        var signals = generator.Stream.EventSignals;
        var failure = Assert.Throws<VfxException>(() => generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, output.AsFrame(), done.Signal));
        Assert.Equal(Interop.NvCVStatus.ErrInitialization, failure.Status);
        Assert.Equal(signals, generator.Stream.EventSignals); // no done for a frame that was not produced
        Assert.True(generator.IsFaulted);
        var again = Assert.Throws<InvalidOperationException>(() => generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, output.AsFrame(), done.Signal));
        Assert.Contains("faulted", again.Message, StringComparison.Ordinal);
        Assert.False(generator.TryConfigurePrepared(generator.Config));
        Assert.False(generator.IsPrepared(generator.Config));

        var loads = generator.Loads;
        generator.Configure(generator.Config);
        Assert.Equal(loads + 1, generator.Loads);
        Assert.False(generator.IsFaulted);
        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, output.AsFrame(), done.Signal);
        done.Synchronize();
        Assert.True(env.Read(output).AsSpan().SequenceEqual(expected));
    }

    [Fact]
    public void Misuse_is_refused_clearly_and_enqueues_nothing()
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 20);
        using var b = env.Pattern(Width, Height, format, 22);
        using var output = env.Image(Width, Height, format);
        using var small = env.Image(Width / 2, Height, format);
        using var tenBit = env.Image(Width, Height, PixelFormat.Rgb10A2);
        using var done = CudaEvent.Create(env.Context);

        using (var unconfigured = new VfgGenerator(env.Context))
        {
            var e = Assert.Throws<InvalidOperationException>(() => unconfigured.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, output.AsFrame(), done.Signal));
            Assert.Contains("Configure", e.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentOutOfRangeException>(() => unconfigured.Configure(new FrameGenConfig { Width = 0, Height = 10, Format = format }));
            Assert.Throws<ArgumentException>(() => unconfigured.Configure(new FrameGenConfig { Width = 64, Height = 64, Format = PixelFormat.Unknown }));
            Assert.Throws<ArgumentException>(() => unconfigured.Configure(new FrameGenConfig { Width = 64, Height = 64, Format = format, Quality = (FrameGenQuality)9 }));
        }

        using var generator = env.Generator(Width, Height, format);
        var stream = generator.Stream;
        var (waits, signals) = (stream.EventWaits, stream.EventSignals);
        var shape = Assert.Throws<ArgumentException>(() => generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, small.AsFrame(), done.Signal));
        Assert.Contains("does not match the configured 640x360 Rgba8", shape.Message, StringComparison.Ordinal);
        Assert.Equal("output", shape.ParamName);
        Assert.Throws<ArgumentException>(() => generator.Enqueue(a.AsFrame(), tenBit.AsFrame(), 0.5f, output.AsFrame(), done.Signal));
        Assert.Throws<ArgumentException>(() => generator.Enqueue(a.AsFrame() with { Domain = MemoryDomain.Vulkan }, b.AsFrame(), 0.5f, output.AsFrame(), done.Signal));
        Assert.Throws<ArgumentException>(() => generator.Enqueue(a.AsFrame() with { SizeBytes = 100 }, b.AsFrame(), 0.5f, output.AsFrame(), done.Signal));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Enqueue(a.AsFrame(), b.AsFrame(), float.NaN, output.AsFrame(), done.Signal));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Enqueue(a.AsFrame(), b.AsFrame(), 1.5f, output.AsFrame(), done.Signal));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Enqueue(a.AsFrame(), b.AsFrame(), -0.01f, output.AsFrame(), done.Signal));

        // A signal whose event was destroyed, and one of another API's domain.
        var stale = CudaEvent.Create(env.Context);
        var staleSignal = stale.Signal;
        stale.Dispose();
        Assert.Throws<ObjectDisposedException>(() => generator.Enqueue(a.AsFrame() with { Ready = staleSignal }, b.AsFrame(), 0.5f, output.AsFrame(), done.Signal));
        Assert.Throws<ObjectDisposedException>(() => generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, output.AsFrame(), staleSignal));
        Assert.Throws<ArgumentException>(() => generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, output.AsFrame(), GpuSignal.Timeline(MemoryDomain.Vulkan, 1, 1)));

        // Nothing was enqueued for any refused call, and the generator still works.
        Assert.Equal((waits, signals), (stream.EventWaits, stream.EventSignals));
        Assert.Equal(0, generator.Runs);
        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, output.AsFrame(), done.Signal);
        done.Synchronize();
        Assert.Equal(1, generator.Runs);

        generator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.5f, output.AsFrame(), done.Signal));
        Assert.Throws<ObjectDisposedException>(() => generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = format }));
        generator.Dispose(); // twice is fine
    }

    [Fact]
    public void A_disposed_stream_or_context_is_refused()
    {
        var env = VfxHardware.Require();
        var format = PixelFormat.Rgba8;
        using var a = env.Pattern(Width, Height, format, 20);
        using var output = env.Image(Width, Height, format);
        using var done = CudaEvent.Create(env.Context);

        var stream = CudaStream.Create(env.Context);
        using var generator = env.Generator(Width, Height, format, options: new VfgOptions { Stream = stream });
        Assert.Same(stream, generator.Stream);
        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => generator.Enqueue(a.AsFrame(), a.AsFrame(), 0.5f, output.AsFrame(), done.Signal));

        var disposed = CudaContext.RetainPrimary(0);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new VfgGenerator(disposed));

        using var otherContextStream = CudaStream.Create(env.Context);
        otherContextStream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new VfgGenerator(env.Context, new VfgOptions { Stream = otherContextStream }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VfgGenerator(env.Context, new VfgOptions { MaxLoadedInstances = 0 }));
    }

    /// <summary>A failing SDK call reports the call, the status and its description.</summary>
    [Fact]
    public void A_failing_sdk_call_names_the_call_and_the_status()
    {
        var env = VfxHardware.Require();
        using var stream = CudaStream.Create(env.Context);
        using var effect = VfgEffect.Create(stream, new VfgInstanceKey(Width, Height, PixelFormat.Rgba8, VfgMode.Medium), true);
        using var a = env.Pattern(Width, Height, PixelFormat.Rgba8, 1);
        effect.Bind(VfgEffect.Previous, a.AsFrame());
        effect.Bind(VfgEffect.Current, a.AsFrame());
        effect.Bind(VfgEffect.Output, a.AsFrame());

        // Not loaded yet.
        var notLoaded = Assert.Throws<VfxException>(() => effect.Run(0.5f, false));
        Log.WriteLine(notLoaded.Message);
        Assert.Equal("NvVFX_Run", notLoaded.Function);
        Assert.StartsWith("NvVFX_Run: NVCV_ERR_", notLoaded.Message, StringComparison.Ordinal);

        effect.Load();
        var zero = Assert.Throws<VfxException>(() => effect.Run(0f, false));
        Log.WriteLine(zero.Message);
        Assert.Equal(Interop.NvCVStatus.ErrParameter, zero.Status);
        Assert.Contains("NVCV_ERR_PARAMETER (-7, ", zero.Message, StringComparison.Ordinal);
    }
}
