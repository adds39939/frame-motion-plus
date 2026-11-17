using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Gpu.Tests.Support;
using Reference = FrameMotionPlus.Cuda.Tests.Support.Reference;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>
/// The generators write straight into ring slots through <see cref="IFrameGenerator.Enqueue"/> with
/// <c>output</c> = the slot's producer view (whose Ready is the release wait) and <c>done</c> = its written signal:
/// no copy, no extra plumbing. The source frames live in ordinary CUDA images (a different pitch from the ring's)
/// drawn on another stream, with one CUDA event per buffer as their Ready.
/// </summary>
public class GeneratorTests
{
    private const int Width = 1277;
    private const int Height = 719;
    private static readonly float[] Timesteps = [0f, 0.25f, 0.5f, 0.75f, 1f, 0.1f, 0.9f, 0.4999f, 0.5001f, 0.33f];

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData("blend", PixelFormat.Rgba8)]
    [InlineData("blend", PixelFormat.Rgb10A2)]
    [InlineData("repeat", PixelFormat.Rgba8)]
    [InlineData("repeat", PixelFormat.Rgb10A2)]
    public void Generator_output_goes_straight_into_a_ring_slot_bit_exact(string kind, PixelFormat format) => Interop.Run(Width, Height, format, rig =>
    {
        var cuda = rig.Cuda;
        using var source = CudaStream.Create(cuda);
        // Source images with a pitch of their own (the ring's is 5120), so a generator mixing up pitches shows.
        const int SourcePitch = (Width * 4) + 68;
        using var prevBuffer = CudaDeviceBuffer.Allocate(cuda, (long)SourcePitch * Height);
        using var currBuffer = CudaDeviceBuffer.Allocate(cuda, (long)SourcePitch * Height);
        using var prevDrawn = CudaEvent.Create(cuda);
        using var currDrawn = CudaEvent.Create(cuda);
        Assert.NotEqual(SourcePitch, rig.Ring.PitchBytes);

        // The previous and current source frames, drawn on their own stream; their events are their Ready.
        var prev = prevBuffer.AsFrame(Width, Height, format, SourcePitch, ready: prevDrawn.Signal);
        var curr = currBuffer.AsFrame(Width, Height, format, SourcePitch, ready: currDrawn.Signal);
        rig.Kernels.EnqueueTestPattern(source, prev, 40);
        prevDrawn.Record(source);
        rig.Kernels.EnqueueTestPattern(source, curr, 41);
        currDrawn.Record(source);
        var prevPixels = Reference.Pattern(Width, Height, 40, format);
        var currPixels = Reference.Pattern(Width, Height, 41, format);

        using CudaFrameGenerator generator = kind == "blend" ? new BlendGenerator(cuda, rig.Kernels) : new RepeatGenerator(cuda);
        IFrameGenerator g = generator;
        g.Configure(new FrameGenConfig { Width = Width, Height = Height, Format = format });

        // More outputs than slots, so later ones really wait on 'released' through output.Ready.
        var waited = 0;
        for (var i = 0; i < Timesteps.Length + 1; i++)
        {
            var discontinuity = i == Timesteps.Length;
            var t = discontinuity ? 0.2f : Timesteps[i];
            if (discontinuity)
            {
                g.NotifyDiscontinuity();
            }

            var slot = rig.Bridge.AcquireSlot();
            waited += slot.ProducerView.Ready.IsNone ? 0 : 1;
            g.Enqueue(prev, curr, t, slot.ProducerView, slot.ProducerSignal);
            rig.Present(slot);

            var expected = discontinuity ? currPixels
                : kind == "blend" ? Reference.Blend(prevPixels, currPixels, Reference.BlendWeight(t), format)
                : t < 0.5f ? prevPixels : currPixels;
            Interop.AssertPixels(expected, rig.ReadPixels(), Width, $"{kind} t={t}{(discontinuity ? " after a discontinuity" : "")} in slot {slot.Index}");
        }

        Assert.True(waited >= Timesteps.Length + 1 - rig.Ring.SlotCount);
        Assert.Equal(0, rig.Presenter.Stats.Dropped);
        source.Synchronize();
    });

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(PixelFormat.Rgba8)]
    [InlineData(PixelFormat.Rgb10A2)]
    public void Real_and_generated_frames_alternate_in_one_ring(PixelFormat format) => Interop.Run(640, 360, format, rig =>
    {
        // A 2x generator in the loop: real frame k copied into the ring, then blend(k, k + 1) at t = 0.5, all ring
        // writes on the generator's stream so 'written' is signalled in order.
        var cuda = rig.Cuda;
        using var source = CudaStream.Create(cuda);
        using var blend = new BlendGenerator(cuda, rig.Kernels, rig.Stream);
        IFrameGenerator g = blend;
        g.Configure(new FrameGenConfig { Width = 640, Height = 360, Format = format });
        var history = new CudaDeviceBuffer[3];
        var drawn = new CudaEvent[3];
        var lastReader = new GpuSignal[3];
        try
        {
            for (var i = 0; i < 3; i++)
            {
                history[i] = CudaDeviceBuffer.AllocateImage(cuda, 640, 360, format);
                drawn[i] = CudaEvent.Create(cuda);
            }

            GpuFrame Draw(int k)
            {
                var h = k % 3;
                source.Wait(lastReader[h]); // the buffer's previous frame is no longer read
                rig.Kernels.EnqueueTestPattern(source, history[h].AsFrame(), (uint)k);
                drawn[h].Record(source);
                return history[h].AsFrame(ready: drawn[h].Signal);
            }

            var frames = new GpuFrame[3];
            frames[0] = Draw(0);
            for (var k = 0; k < 12; k++)
            {
                // Real frame k.
                var real = rig.Bridge.AcquireSlot();
                rig.Stream.Wait(real.ProducerWait);
                rig.Stream.Wait(frames[k % 3].Ready);
                rig.Stream.CopyFrameAsync(frames[k % 3], real.ProducerView);
                rig.Stream.Signal(real.ProducerSignal);
                lastReader[k % 3] = real.ProducerSignal;
                rig.Present(real);
                Interop.AssertPixels(Reference.Pattern(640, 360, (uint)k, format), rig.ReadPixels(), 640, $"real {k}");

                // Generated between k and k + 1.
                frames[(k + 1) % 3] = Draw(k + 1);
                var generated = rig.Bridge.AcquireSlot();
                g.Enqueue(frames[k % 3], frames[(k + 1) % 3], 0.5f, generated.ProducerView, generated.ProducerSignal);
                lastReader[k % 3] = generated.ProducerSignal;
                lastReader[(k + 1) % 3] = generated.ProducerSignal;
                rig.Present(generated);
                var expected = Reference.Blend(Reference.Pattern(640, 360, (uint)k, format), Reference.Pattern(640, 360, (uint)(k + 1), format), Reference.BlendWeight(0.5f), format);
                Interop.AssertPixels(expected, rig.ReadPixels(), 640, $"blend {k}/{k + 1}");
            }

            source.Synchronize();
        }
        finally
        {
            foreach (var h in history)
            {
                h?.Dispose();
            }

            foreach (var e in drawn)
            {
                e?.Dispose();
            }
        }
    });
}
