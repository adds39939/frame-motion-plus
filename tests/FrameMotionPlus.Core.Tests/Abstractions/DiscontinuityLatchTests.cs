using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Simulation;

namespace FrameMotionPlus.Core.Tests.Abstractions;

/// <summary>
/// <see cref="IFrameGenerator.NotifyDiscontinuity"/> covers "the next pair": every call with the pair of the first call
/// after the notification, until a different pair. A pair is its frames (pixel address and content time).
/// </summary>
public class DiscontinuityLatchTests
{
    private static readonly GpuFrame A = new() { Address = 0x1000, ContentTimeNs = 100 };
    private static readonly GpuFrame B = new() { Address = 0x2000, ContentTimeNs = 200 };
    private static readonly GpuFrame C = new() { Address = 0x3000, ContentTimeNs = 300 };

    [Fact]
    public void Without_a_notification_nothing_is_discontinuous()
    {
        var latch = new DiscontinuityLatch();
        Assert.False(latch.Observe(A, B));
        Assert.False(latch.Observe(B, C));
        Assert.False(latch.IsActive);
    }

    [Fact]
    public void Every_call_with_the_next_pair_is_discontinuous_until_the_pair_changes()
    {
        var latch = new DiscontinuityLatch();
        Assert.False(latch.Observe(A, B));
        latch.Notify();
        Assert.True(latch.IsActive);

        // 4x across the cut: three generated frames of one pair, all discontinuous.
        Assert.True(latch.Observe(B, C));
        Assert.True(latch.Observe(B, C));
        Assert.True(latch.Observe(B, C));

        // The next pair ends it.
        Assert.False(latch.Observe(C, A));
        Assert.False(latch.Observe(B, C));
        Assert.False(latch.IsActive);
    }

    [Fact]
    public void A_buffer_reused_for_a_later_frame_is_a_different_pair()
    {
        var latch = new DiscontinuityLatch();
        latch.Notify();
        Assert.True(latch.Observe(A, B));
        Assert.False(latch.Observe(A, B with { ContentTimeNs = 999 })); // same memory, a later frame
        Assert.False(latch.Observe(A with { Offset = 64 }, B));         // same allocation, another view
    }

    [Fact]
    public void A_second_notification_moves_the_latch_to_the_next_pair()
    {
        var latch = new DiscontinuityLatch();
        latch.Notify();
        Assert.True(latch.Observe(A, B));
        latch.Notify();
        Assert.True(latch.Observe(B, C));
        Assert.True(latch.Observe(B, C));
        Assert.False(latch.Observe(C, A));
    }

    [Fact]
    public void Notifications_from_another_thread_are_seen()
    {
        var latch = new DiscontinuityLatch();
        var thread = new Thread(latch.Notify);
        thread.Start();
        thread.Join();
        Assert.True(latch.Observe(A, B));
    }

    [Fact]
    public void Generators_do_not_block_by_default()
    {
        Assert.False(default(FrameGenCapabilities).EnqueueMayBlock);
        using var g = new NullFrameGenerator();
        Assert.False(g.Capabilities.EnqueueMayBlock);
        var blocking = new FrameGenCapabilities { EnqueueMayBlock = true };
        Assert.True(blocking.EnqueueMayBlock);
        Assert.NotEqual(default, blocking);
    }
}
