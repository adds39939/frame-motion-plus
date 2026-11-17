using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Gpu.Tests.Support;
using Reference = FrameMotionPlus.Cuda.Tests.Support.Reference;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>Many frames with slot reuse, the producer as far ahead as the ring allows.</summary>
public class StressTests
{
    private const int Width = 480;
    private const int Height = 270;

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(PixelFormat.Rgba8, 4)]
    [InlineData(PixelFormat.Rgb10A2, 6)]
    public void Every_presented_frame_is_the_expected_one_with_the_producer_a_full_ring_ahead(PixelFormat format, int slots) => Interop.Run(Width, Height, format, rig =>
    {
        // Write i + N is enqueued before write i is presented, so it waits GPU-side for the release of write i in the
        // very slot the presenter is about to read: any early reuse shows up as a frame from the future, any missed or
        // late write as a stale one, a partial one as a torn image. Every frame is compared whole with the reference.
        const int Frames = 600;
        var n = rig.Ring.SlotCount;
        Assert.Equal(slots, n);
        var writes = new BridgeSlot[Frames + 1];
        for (var w = 1; w <= n; w++)
        {
            writes[w] = rig.WritePattern(Frame(w));
        }

        for (var i = 1; i <= Frames; i++)
        {
            if (i + n <= Frames)
            {
                writes[i + n] = rig.WritePattern(Frame(i + n));
                Assert.Equal((ulong)i, writes[i + n].ProducerWait.Value);

                // Write i has not even been handed to the presenter, so its release cannot have happened and the lap
                // into its slot cannot have run.
                Assert.True(rig.Ring.ReadWrittenValue() < (ulong)(i + n), $"write {i + n} finished before write {i} was released");
            }

            rig.Present(writes[i]);
            var pixels = rig.ReadPixels();
            var counter = Reference.DecodeCounter(pixels, Width, Height, format);
            Assert.True(counter == Frame(i), $"write {i}: counter {counter}, expected {Frame(i)} ({(counter > Frame(i) ? "overwritten by a later write" : "stale")})");
            Interop.AssertPixels(Reference.Pattern(Width, Height, Frame(i), format), pixels, Width, $"write {i}");
        }

        Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
        var stats = rig.Presenter.Stats;
        Interop.WriteLine(stats.ToString());
        Assert.Equal(Frames, stats.Presented);
        Assert.Equal(0, stats.Dropped);
        Assert.Equal(0, stats.StaleRequests);
        Assert.Equal((ulong)Frames, rig.Ring.ReadWrittenValue());
        Assert.Equal((ulong)Frames, rig.Ring.ReadReleasedValue());
    }, new RigOptions { Presenter = new Vulkan.VulkanPresenterOptions { SlotCount = slots } });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Dropped_presents_never_stall_a_full_speed_producer() => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        // No waiting for a free frame slot, so some presents are refused. A refused write must not hold its slot for
        // good (the presenter releases it, at the latest when a newer write is handed to it), or the producer, lapping
        // the ring as fast as it can enqueue, would stall. Refusals are forced rather than hoped for: every 16th write
        // spends 3 ms on the GPU first, the presents waiting on it keep both frames in flight, and the CPU, which never
        // waits, runs into them.
        // The producer acquires with back-pressure: while the GPU is behind (the delays) the bridge holds it back, and it
        // retries until it gets the slot (bounded), so all 2000 frames are produced; held back is not stalled.
        const int Frames = 2000;
        var presented = 0;
        var dropped = 0;
        long held = 0;
        BridgeSlot slot = default;
        for (var i = 1; i <= Frames; i++)
        {
            if (i % 16 == 0)
            {
                rig.Kernels.EnqueueDelay(rig.Stream, 3_000_000);
            }

            var start = Stopwatch.GetTimestamp();
            while (!rig.Bridge.TryAcquireSlot(out slot))
            {
                held++;
                Assert.True(Stopwatch.GetElapsedTime(start).TotalSeconds < 2, $"write {i} was held back for 2 s: the producer stalled");
                Thread.SpinWait(64);
            }

            rig.Stream.Wait(slot.ProducerWait);
            rig.Kernels.EnqueueTestPattern(rig.Stream, slot.ProducerView, Frame(i));
            rig.Stream.Signal(slot.ProducerSignal);
            if (i == Frames)
            {
                rig.WaitReady();
            }

            if (rig.Presenter.TryPresent(rig.Request(slot)))
            {
                presented++;
            }
            else
            {
                dropped++;
            }

            while (rig.Presenter.TryGetFeedback(out _))
            {
            }
        }

        Assert.True(rig.Ring.WaitReleased(Frames, 5_000_000_000), $"released stopped at {rig.Ring.ReadReleasedValue()}");
        var stats = rig.Presenter.Stats;
        Interop.WriteLine($"{presented} presented, {dropped} dropped, {held} polls held back by the bridge; {stats}");
        Assert.Equal(Frames, presented + dropped);
        Assert.Equal(dropped, stats.Dropped);
        Assert.Equal(0, stats.RejectedRequests);
        Assert.True(dropped > 0, "nothing was dropped; the test did not exercise the drop path");
        Assert.Equal(Frame(Frames), Reference.DecodeCounter(rig.ReadPixels(), Width, Height, PixelFormat.Rgba8));
    });

    private static uint Frame(int write) => (uint)write + 7u;
}
