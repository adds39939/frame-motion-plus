using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Gpu.Tests.Support;
using Reference = FrameMotionPlus.Cuda.Tests.Support.Reference;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>
/// The two timelines through real work, each with a control that removes the synchronisation and shows the test then
/// sees the failure (a test that cannot fail proves nothing).
/// <list type="bullet">
/// <item><em>written</em>, CUDA to Vulkan: CUDA writes a slot behind a long GPU delay; the CPU presents at once. With the
/// signal after the write, the presenter's GPU wait holds the read back and the image is right. Signalled before the
/// write instead (the dependency removed), the presenter reads the slot before CUDA has written it.</item>
/// <item><em>released</em>, Vulkan to CUDA: CUDA laps the ring before the presenter has been handed the slot's previous
/// write. With the wait, the write stays blocked GPU-side until the release and the presenter shows the old write. With
/// the wait removed, CUDA overwrites the slot the consumer still owns and the presenter shows the new write instead.</item>
/// </list>
/// </summary>
public class HandshakeTests
{
    private const int Width = 640;
    private const int Height = 360;
    private const long DelayNs = 250_000_000;

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(PixelFormat.Rgba8)]
    [InlineData(PixelFormat.Rgb10A2)]
    public void Presenter_waits_for_cuda_written_before_reading(PixelFormat format) => Interop.Run(Width, Height, format, rig =>
    {
        var (pixels, writtenAtPresent, readMs) = DelayedWrite(rig, format, signalBeforeWrite: false);
        Interop.WriteLine($"written at present: {writtenAtPresent}; read back after {readMs:F0} ms");
        Assert.Equal(1UL, writtenAtPresent); // the CPU did not wait: CUDA was still behind its delay
        Assert.True(readMs > 100, $"the present completed after {readMs:F0} ms, before CUDA could have written the slot");
        Interop.AssertPixels(Reference.Pattern(Width, Height, 222, format), pixels, Width, "write 2");
    });

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(PixelFormat.Rgba8)]
    [InlineData(PixelFormat.Rgb10A2)]
    public void Control_without_the_written_dependency_the_presenter_reads_an_unwritten_slot(PixelFormat format) => Interop.Run(Width, Height, format, rig =>
    {
        var (pixels, _, readMs) = DelayedWrite(rig, format, signalBeforeWrite: true);
        Interop.WriteLine($"read back after {readMs:F0} ms");
        var expected = Reference.Pattern(Width, Height, 222, format);
        Assert.False(expected.AsSpan().SequenceEqual(pixels), "the control saw the right image, so the positive test proves nothing");
        Assert.NotEqual(222u, Reference.DecodeCounter(pixels, Width, Height, format));
    });

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(PixelFormat.Rgba8)]
    [InlineData(PixelFormat.Rgb10A2)]
    public void Cuda_waits_for_vulkan_released_before_reusing_a_slot(PixelFormat format) => Interop.Run(Width, Height, format, rig =>
    {
        var (lap, firstWrite) = FillRingThenLap(rig, waitForRelease: true);

        // The lap is blocked GPU-side: the presenter has not even been handed write 1.
        Thread.Sleep(150);
        Assert.Equal((ulong)rig.Ring.SlotCount, rig.Ring.ReadWrittenValue());
        Assert.False(rig.Stream.IsIdle);

        rig.Present(firstWrite);
        Interop.AssertPixels(Reference.Pattern(Width, Height, 101, format), rig.ReadPixels(), Width, "write 1 after the lap was enqueued");

        // Released, so the lap runs; then it shows.
        Assert.True(rig.Ring.WaitWritten(lap.ProducerSignal.Value, 2_000_000_000), "the lap never ran after the release");
        rig.Present(lap);
        Interop.AssertPixels(Reference.Pattern(Width, Height, 999, format), rig.ReadPixels(), Width, "the lap");
    });

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(PixelFormat.Rgba8)]
    [InlineData(PixelFormat.Rgb10A2)]
    public void Control_without_the_released_wait_cuda_overwrites_a_slot_still_in_use(PixelFormat format) => Interop.Run(Width, Height, format, rig =>
    {
        var (lap, firstWrite) = FillRingThenLap(rig, waitForRelease: false);
        Assert.True(rig.Ring.WaitWritten(lap.ProducerSignal.Value, 2_000_000_000));

        rig.Present(firstWrite);
        var pixels = rig.ReadPixels();
        Assert.False(Reference.Pattern(Width, Height, 101, format).AsSpan().SequenceEqual(pixels),
            "the control saw write 1 intact, so the positive test proves nothing");
        Interop.AssertPixels(Reference.Pattern(Width, Height, 999, format), pixels, Width, "write 1's slot, overwritten by the lap");
    });

    /// <summary>
    /// Write 1 normally; write 2 behind a GPU delay, signalling written after (or, for the control, before) the
    /// pattern; present write 2 at once and read it back.
    /// </summary>
    private static (uint[] Pixels, ulong WrittenAtPresent, double ReadMs) DelayedWrite(InteropRig rig, PixelFormat format, bool signalBeforeWrite)
    {
        var first = rig.WritePattern(111);
        rig.Present(first);
        Interop.AssertPixels(Reference.Pattern(Width, Height, 111, format), rig.ReadPixels(), Width, "write 1");

        var slot = rig.Bridge.AcquireSlot();
        var s = rig.Stream;
        s.Wait(slot.ProducerWait);
        if (signalBeforeWrite)
        {
            s.Signal(slot.ProducerSignal);
        }

        rig.Kernels.EnqueueDelay(s, DelayNs);
        rig.Kernels.EnqueueTestPattern(s, slot.ProducerView, 222);
        if (!signalBeforeWrite)
        {
            s.Signal(slot.ProducerSignal);
        }

        var writtenAtPresent = rig.Ring.ReadWrittenValue();
        rig.Present(slot);
        var start = Stopwatch.GetTimestamp();
        var pixels = rig.ReadPixels();
        var readMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        s.Synchronize();
        return (pixels, writtenAtPresent, readMs);
    }

    /// <summary>
    /// Writes 1..N (patterns 101..), none presented yet, then laps: write N + 1 into slot 0 with pattern 999, waiting
    /// for the release of write 1 or (control) not.
    /// </summary>
    private static (BridgeSlot Lap, BridgeSlot First) FillRingThenLap(InteropRig rig, bool waitForRelease)
    {
        var n = rig.Ring.SlotCount;
        var first = default(BridgeSlot);
        for (var i = 0; i < n; i++)
        {
            var slot = rig.WritePattern((uint)(101 + i));
            first = i == 0 ? slot : first;
        }

        Assert.True(rig.Ring.WaitWritten((ulong)n, 2_000_000_000));
        var lap = rig.Bridge.AcquireSlot();
        Assert.Equal(first.Index, lap.Index);
        Assert.Equal(GpuSignalKind.TimelineSemaphore, lap.ProducerWait.Kind);
        Assert.Equal(MemoryDomain.CudaDevice, lap.ProducerWait.Domain);
        Assert.Equal(1UL, lap.ProducerWait.Value);
        Assert.Equal(lap.ProducerWait, lap.ProducerView.Ready);
        if (waitForRelease)
        {
            rig.Stream.Wait(lap.ProducerWait);
        }

        rig.Kernels.EnqueueTestPattern(rig.Stream, lap.ProducerView, 999);
        rig.Stream.Signal(lap.ProducerSignal);
        return (lap, first);
    }
}
