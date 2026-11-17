using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Simulation;

namespace FrameMotionPlus.Core.Tests.Abstractions;

/// <summary>
/// <see cref="IFrameGenerator.NotifyDiscontinuity"/> names the first frame after a cut by its content time; a pair is
/// discontinuous when it spans that time (prev &lt; T ≤ curr), for every call with it, and the first pair that starts at or
/// after T ends the notification. Pairs are identified by content time only.
/// </summary>
public class DiscontinuityLatchTests
{
    private static readonly GpuFrame A = new() { Address = 0x1000, ContentTimeNs = 100 };
    private static readonly GpuFrame B = new() { Address = 0x2000, ContentTimeNs = 200 };
    private static readonly GpuFrame C = new() { Address = 0x3000, ContentTimeNs = 300 };
    private static readonly GpuFrame D = new() { Address = 0x1000, ContentTimeNs = 400 }; // A's buffer, reused

    [Fact]
    public void Without_a_notification_nothing_is_discontinuous()
    {
        var latch = new DiscontinuityLatch();
        Assert.False(latch.Observe(A, B));
        Assert.False(latch.Observe(B, C));
        Assert.False(latch.IsActive);
    }

    [Fact]
    public void Every_call_with_the_pair_across_the_cut_is_discontinuous_until_a_pair_starts_past_it()
    {
        var latch = new DiscontinuityLatch();
        Assert.False(latch.Observe(A, B));
        latch.Notify(C.ContentTimeNs); // C is the first frame after the cut
        Assert.True(latch.IsActive);

        // 4x across the cut: three generated frames of one pair, all discontinuous.
        Assert.True(latch.Observe(B, C));
        Assert.True(latch.Observe(B, C));
        Assert.True(latch.Observe(B, C));

        // The next pair starts at the cut: it ends the notification.
        Assert.False(latch.Observe(C, D));
        Assert.False(latch.IsActive);
    }

    /// <summary>
    /// Review trap (a): the scheduler shows real frames across a cut, so the pair across it is never enqueued. The old
    /// latch stayed armed and made the first pair after the cut output the current frame instead of interpolating; the
    /// notification now names the cut, and a pair that starts at it releases it without being affected.
    /// </summary>
    [Fact]
    public void A_cut_whose_pair_is_never_enqueued_does_not_touch_the_first_pair_after_it()
    {
        var latch = new DiscontinuityLatch();
        Assert.False(latch.Observe(A, B));
        latch.Notify(C.ContentTimeNs); // the cut at C; (B, C) is shown as real frames, never generated
        Assert.False(latch.Observe(C, D), "the first pair after the cut was treated as a cut");
        Assert.False(latch.IsActive, "a notification that can no longer apply stayed armed");
        Assert.False(latch.Observe(C, D));
    }

    /// <summary>
    /// Review trap (b): the source thread reports the cut at C while the generator thread still has frames of the old
    /// pair (A, B) to generate. The old latch bound the notification to (A, B), so the old pair was copied and the pair
    /// across the cut was interpolated; now the old pair is unaffected and the pair across the cut is not.
    /// </summary>
    [Fact]
    public void A_notification_made_mid_pair_from_another_thread_binds_to_the_pair_across_the_cut()
    {
        var latch = new DiscontinuityLatch();
        Assert.False(latch.Observe(A, B)); // t = 0.25 of the old pair
        var thread = new Thread(() => latch.Notify(C.ContentTimeNs));
        thread.Start();
        thread.Join();
        Assert.False(latch.Observe(A, B), "the old pair took a notification meant for the pair across the cut"); // t = 0.5
        Assert.False(latch.Observe(A, B)); // t = 0.75
        Assert.True(latch.Observe(B, C), "the pair across the cut was interpolated");
        Assert.True(latch.Observe(B, C));
        Assert.False(latch.Observe(C, D));
    }

    /// <summary>
    /// Review trap (c): with fixed buffers and no content times the pair key never changed, so every later run output the
    /// current frame. Pairs are now told apart by content time alone, and a pair without increasing times is refused.
    /// </summary>
    [Fact]
    public void Fixed_buffers_are_told_apart_by_content_time_and_frames_without_times_are_refused()
    {
        var latch = new DiscontinuityLatch();
        var prevBuffer = new GpuFrame { Address = 0x1000 };
        var currBuffer = new GpuFrame { Address = 0x2000 };
        latch.Notify(2_000);
        Assert.True(latch.Observe(prevBuffer with { ContentTimeNs = 1_000 }, currBuffer with { ContentTimeNs = 2_000 }));
        for (var k = 1; k < 5; k++)
        {
            // The same two buffers, rewritten with later frames.
            Assert.False(latch.Observe(prevBuffer with { ContentTimeNs = 1_000 + (k * 1_000) }, currBuffer with { ContentTimeNs = 2_000 + (k * 1_000) }));
        }

        Assert.Throws<ArgumentException>(() => DiscontinuityLatch.ValidatePair(prevBuffer, currBuffer)); // both 0
        Assert.Throws<ArgumentException>(() => DiscontinuityLatch.ValidatePair(B, A));                    // backwards
        Assert.Throws<ArgumentException>(() => DiscontinuityLatch.ValidatePair(A, A with { Address = 9 })); // same time
        DiscontinuityLatch.ValidatePair(A, B);
    }

    [Fact]
    public void A_pair_that_skips_over_the_cut_frame_still_spans_it()
    {
        var latch = new DiscontinuityLatch();
        latch.Notify(250); // the first frame after the cut never reached the generator
        Assert.False(latch.Observe(A, B));
        Assert.True(latch.Observe(B, C));
        Assert.False(latch.Observe(C, D));
    }

    [Fact]
    public void Several_pending_cuts_each_apply_to_their_own_pair()
    {
        var latch = new DiscontinuityLatch();
        latch.Notify(C.ContentTimeNs);
        latch.Notify(B.ContentTimeNs);
        latch.Notify(B.ContentTimeNs); // the same cut twice is one
        Assert.True(latch.Observe(A, B));
        Assert.True(latch.Observe(B, C));
        Assert.True(latch.Observe(B, C)); // the pair across the second cut, again
        Assert.False(latch.Observe(C, D));
        Assert.False(latch.IsActive);
    }

    [Fact]
    public void A_late_notification_applies_only_to_later_calls_with_its_pair_and_one_already_passed_is_dropped()
    {
        var latch = new DiscontinuityLatch();
        Assert.False(latch.Observe(B, C));
        latch.Notify(C.ContentTimeNs); // after the first call of the pair across the cut
        Assert.True(latch.Observe(B, C));
        Assert.False(latch.Observe(C, D));

        latch.Notify(B.ContentTimeNs); // passed already: released by the next call, affects nothing
        Assert.False(latch.Observe(C, D));
        Assert.False(latch.IsActive);
    }

    [Fact]
    public void More_pending_cuts_than_the_capacity_replace_the_earliest()
    {
        var latch = new DiscontinuityLatch();
        for (var i = 1; i <= DiscontinuityLatch.Capacity + 1; i++)
        {
            latch.Notify(i * 100);
        }

        // 100 gave way to 500; 200 .. 500 are kept.
        Assert.False(latch.Observe(new GpuFrame { ContentTimeNs = 50 }, new GpuFrame { ContentTimeNs = 100 }));
        Assert.True(latch.Observe(new GpuFrame { ContentTimeNs = 150 }, new GpuFrame { ContentTimeNs = 200 }));
        Assert.True(latch.Observe(new GpuFrame { ContentTimeNs = 450 }, new GpuFrame { ContentTimeNs = 500 }));
        Assert.False(latch.Observe(new GpuFrame { ContentTimeNs = 500 }, new GpuFrame { ContentTimeNs = 600 }));
        Assert.False(latch.IsActive);
    }

    /// <summary>
    /// Full, a notification earlier than every pending cut is the earliest of them all, so it is the one dropped: the four
    /// pending cuts are kept (it used to replace the earliest pending one, losing a cut the pairs had yet to reach for one
    /// they would reach no sooner).
    /// </summary>
    [Fact]
    public void Full_a_notification_earlier_than_every_pending_cut_is_the_one_dropped()
    {
        var latch = new DiscontinuityLatch();
        for (var i = 1; i <= DiscontinuityLatch.Capacity; i++)
        {
            latch.Notify(i * 100);
        }

        latch.Notify(50);
        Assert.False(latch.Observe(new GpuFrame { ContentTimeNs = 40 }, new GpuFrame { ContentTimeNs = 60 }));
        for (var i = 1; i <= DiscontinuityLatch.Capacity; i++)
        {
            Assert.True(latch.Observe(new GpuFrame { ContentTimeNs = (i * 100) - 10 }, new GpuFrame { ContentTimeNs = i * 100 }), $"cut {i * 100} was lost");
        }

        Assert.False(latch.Observe(new GpuFrame { ContentTimeNs = 400 }, new GpuFrame { ContentTimeNs = 500 }));
        Assert.False(latch.IsActive);
    }

    [Fact]
    public void Notifications_from_many_threads_are_all_kept()
    {
        var latch = new DiscontinuityLatch();
        var threads = Enumerable.Range(1, DiscontinuityLatch.Capacity)
            .Select(i => new Thread(() => latch.Notify(i * 1_000)))
            .ToArray();
        foreach (var t in threads)
        {
            t.Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        for (var i = 1; i <= DiscontinuityLatch.Capacity; i++)
        {
            Assert.True(latch.Observe(new GpuFrame { ContentTimeNs = (i * 1_000) - 1 }, new GpuFrame { ContentTimeNs = i * 1_000 }), $"cut {i} was lost");
        }
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
