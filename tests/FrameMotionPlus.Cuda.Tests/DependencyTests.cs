using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>
/// The input-dependency mechanism: <see cref="GpuFrame.Ready"/> carries a CUDA event (or CUDA timeline semaphore) and
/// the generator's stream waits on it GPU-side. Each test makes the other stream slow on purpose (a 50 ms GPU delay),
/// so that without the dependency the generator would run first; the control runs prove that it really does.
/// Serial: with many streams alive in parallel tests, two streams can share one of the GPU's hardware work queues
/// (CUDA_DEVICE_MAX_CONNECTIONS, 8 by default), which orders them as a side effect and hides the race in the controls.
/// </summary>
[Collection(SerialGpuDefinition.Name)]
[Trait("Category", "Gpu")]
public class DependencyTests
{
    private const int Width = 256;
    private const int Height = 144;
    private const long SlowNs = 50_000_000;
    private const PixelFormat Format = PixelFormat.Rgb10A2;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Generator_waits_for_inputs_written_on_another_stream(bool withReady)
    {
        var env = Gpu.Require();
        using var producer = CudaStream.Create(env.Context);
        using var a = env.Image(Width, Height, Format);
        using var b = env.Image(Width, Height, Format);
        using var o = env.Image(Width, Height, Format);
        using var written = CudaEvent.Create(env.Context);
        using var done = CudaEvent.Create(env.Context);
        using IFrameGenerator generator = new BlendGenerator(env.Context, env.Kernels);
        generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = Format });

        // Producer: slow, then writes both inputs and records the event, as a source's upload/convert would.
        env.Kernels.EnqueueDelay(producer, SlowNs);
        env.Kernels.EnqueueTestPattern(producer, a.AsFrame(), 7);
        env.Kernels.EnqueueTestPattern(producer, b.AsFrame(), 8);
        written.Record(producer);
        var ready = withReady ? written.Signal : GpuSignal.None;

        generator.Enqueue(a.AsFrame(ready: ready), b.AsFrame(ready: ready), 0.5f, o.AsFrame(), done.Signal);
        done.Synchronize();
        var actual = env.Read(producer, o.AsFrame());

        var expected = Reference.Blend(Reference.Pattern(Width, Height, 7, Format), Reference.Pattern(Width, Height, 8, Format), 32768, Format);
        if (withReady)
        {
            Assert.True(expected.AsSpan().SequenceEqual(actual), Reference.FirstDifference(expected, actual, Width));
        }
        else
        {
            // Control: without the dependency the generator blended the still-empty inputs.
            Assert.All(actual, p => Assert.Equal(0u, p));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Generator_waits_for_the_consumer_to_release_the_output_slot(bool withReady)
    {
        var env = Gpu.Require();
        using var setup = CudaStream.Create(env.Context);
        using var consumer = CudaStream.Create(env.Context);
        using var a = env.Image(Width, Height, Format);
        using var b = env.Image(Width, Height, Format);
        using var slot = env.Image(Width, Height, Format);
        using var shown = env.Image(Width, Height, Format);
        using var released = CudaEvent.Create(env.Context);
        using var done = CudaEvent.Create(env.Context);
        env.Kernels.EnqueueTestPattern(setup, a.AsFrame(), 1);
        env.Kernels.EnqueueTestPattern(setup, b.AsFrame(), 2);
        env.Kernels.EnqueueTestPattern(setup, slot.AsFrame(), 999); // the picture the consumer is about to show
        setup.Synchronize();

        using var generator = new BlendGenerator(env.Context, env.Kernels);
        generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = Format });

        // Consumer: slow, then reads the slot (as the present pass would) and releases it.
        env.Kernels.EnqueueDelay(consumer, SlowNs);
        consumer.CopyFrameAsync(slot.AsFrame(), shown.AsFrame());
        released.Record(consumer);
        var target = slot.AsFrame(ready: withReady ? released.Signal : GpuSignal.None);

        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.25f, target, done.Signal);
        done.Synchronize();
        consumer.Synchronize();

        var old = Reference.Pattern(Width, Height, 999, Format);
        var blended = Reference.Blend(Reference.Pattern(Width, Height, 1, Format), Reference.Pattern(Width, Height, 2, Format), 16384, Format);
        Assert.Equal(blended, env.Read(setup, slot.AsFrame()));
        if (withReady)
        {
            var seen = env.Read(setup, shown.AsFrame());
            Assert.True(old.AsSpan().SequenceEqual(seen), "consumer saw an overwritten slot: " + Reference.FirstDifference(old, seen, Width));
        }
        else
        {
            // Control: without the dependency the generator overwrote the slot before the consumer read it.
            Assert.Equal(blended, env.Read(setup, shown.AsFrame()));
        }
    }

    /// <summary>
    /// A pipelined run as Phase 4 will drive it, with no CPU waits between frames: a source stream writes frame i into
    /// history slot i % 3 and records that slot's event; the generator blends (i − 1, i) into output i. The source in
    /// turn waits on the generator's done event before reusing a history slot (the write-after-read direction).
    /// </summary>
    [Fact]
    public void Pipelined_frames_with_per_slot_events_all_come_out_right()
    {
        var env = Gpu.Require();
        const int Frames = 12;
        using var source = CudaStream.Create(env.Context);
        using var generator = new BlendGenerator(env.Context, env.Kernels);
        generator.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = Format });
        var history = new CudaDeviceBuffer[3];
        var written = new CudaEvent[3];
        var outputs = new CudaDeviceBuffer[Frames];
        var consumed = new CudaEvent[Frames];
        try
        {
            for (var i = 0; i < 3; i++)
            {
                history[i] = env.Image(Width, Height, Format);
                written[i] = CudaEvent.Create(env.Context);
            }

            for (var i = 0; i < Frames; i++)
            {
                outputs[i] = env.Image(Width, Height, Format);
                consumed[i] = CudaEvent.Create(env.Context);
            }

            for (var i = 0; i < Frames; i++)
            {
                var slot = i % 3;
                if (i >= 3)
                {
                    // Slot was last read by the blend of (i - 3, i - 2), i.e. output i - 2.
                    source.Wait(consumed[i - 2]);
                }

                env.Kernels.EnqueueDelay(source, 2_000_000);
                env.Kernels.EnqueueTestPattern(source, history[slot].AsFrame(), (uint)i);
                written[slot].Record(source);
                if (i == 0)
                {
                    continue;
                }

                var prevSlot = (i - 1) % 3;
                generator.Enqueue(
                    history[prevSlot].AsFrame(ready: written[prevSlot].Signal),
                    history[slot].AsFrame(ready: written[slot].Signal),
                    0.5f,
                    outputs[i].AsFrame(),
                    consumed[i].Signal);
            }

            generator.Stream.Synchronize();
            source.Synchronize();
            for (var i = 1; i < Frames; i++)
            {
                var expected = Reference.Blend(
                    Reference.Pattern(Width, Height, (uint)(i - 1), Format), Reference.Pattern(Width, Height, (uint)i, Format), 32768, Format);
                var actual = env.Read(source, outputs[i].AsFrame());
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"output {i}: {Reference.FirstDifference(expected, actual, Width)}");
            }
        }
        finally
        {
            foreach (var d in history.Concat(outputs))
            {
                d?.Dispose();
            }

            foreach (var e in written.Concat(consumed))
            {
                e?.Dispose();
            }
        }
    }
}
