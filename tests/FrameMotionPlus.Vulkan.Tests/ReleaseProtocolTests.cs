using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Diagnostics;
using FrameMotionPlus.Vulkan.Tests.Support;
using Vortice.Vulkan;

namespace FrameMotionPlus.Vulkan.Tests;

/// <summary>
/// The release side of <see cref="RingProtocol"/>: releases go out in steps of at most N (so presenting or releasing a
/// write far ahead cannot deadlock the two queues), a refused request keeps its write presentable, and the release is
/// never signalled before the copy has read the slot.
/// </summary>
public class ReleaseProtocolTests
{
    private const long Second = 1_000_000_000;

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(false)]
    [InlineData(true)]
    public void Presenting_or_releasing_a_write_N_ahead_of_the_last_release_completes(bool releaseOnly) => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgba8, presenter: new VulkanPresenterOptions { DrainTimeoutNs = 300_000_000 });
        var n = rig.Ring.SlotCount;
        for (var i = 1; i <= n + 1; i++)
        {
            rig.Producer.WritePattern(TestPattern.Ramp, i); // write N+1 waits GPU-side for released >= 1
        }

        Assert.True(rig.Producer.WaitWritten((ulong)n, Second));
        var target = (ulong)(n + 1);
        if (releaseOnly)
        {
            rig.Presenter.ReleaseThrough(target);
        }
        else
        {
            Assert.True(rig.Presenter.TryPresent(rig.Request(target)));
        }

        Assert.True(rig.Presenter.WaitIdle(Second), $"stuck: written {rig.Producer.ReadWrittenValue()}, released {rig.Producer.ReadReleasedValue()}");
        Assert.Equal(target, rig.Producer.ReadWrittenValue());
        Assert.Equal(target, rig.Producer.ReadReleasedValue());
        if (!releaseOnly)
        {
            Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 9, 9, 160, 90, n + 1), rig.ReadPixels()[(9 * 160) + 9]);
        }

        // And the ring can still be replaced afterwards (this used to time out).
        rig.Configure(320, 180, PixelFormat.Rgba8);
        rig.WriteAndPresent(TestPattern.Ramp, 1);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Releasing_far_ahead_of_unwritten_writes_goes_out_in_steps_of_N() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 64, 36, PixelFormat.Rgba8);
        var n = (ulong)rig.Ring.SlotCount;
        var releasesBefore = rig.Presenter.Stats.Releases;
        rig.Presenter.ReleaseThrough(5 * n);
        Assert.Equal(5, rig.Presenter.Stats.Releases - releasesBefore);
        Assert.Equal(5 * n, rig.Presenter.LastReleasedWrite);

        // The producer, which had not written anything yet, can now write all of them; each release follows.
        for (var i = 1UL; i <= 5 * n; i++)
        {
            rig.Producer.WriteStaged();
        }

        Assert.True(rig.Producer.WaitReleased(5 * n, Second));
        Assert.True(rig.Presenter.WaitIdle(Second));
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_refused_write_stays_presentable_and_a_retry_shows_it() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgba8);
        rig.WriteAndPresent(TestPattern.Ramp, 1);
        Assert.True(rig.Presenter.WaitIdle(Second));

        // Two frames in flight on writes that do not exist yet, so the third request is refused.
        Assert.True(rig.Presenter.TryPresent(rig.Request(2)));
        Assert.True(rig.Presenter.TryPresent(rig.Request(3)));
        Assert.False(rig.Presenter.TryPresent(rig.Request(4)));
        Assert.Equal(4UL, rig.Presenter.RetainedWrite);
        for (var i = 2; i <= 4; i++)
        {
            rig.Producer.WritePattern(TestPattern.Ramp, i);
        }

        rig.WaitReady();
        Assert.True(rig.Presenter.TryPresent(rig.Request(4)), "the retained write must still be presentable");
        Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 3, 4, 160, 90, 4), rig.ReadPixels()[(4 * 160) + 3]);
        Assert.Equal(0UL, rig.Presenter.RetainedWrite);
        Assert.True(rig.Producer.WaitReleased(4, Second));

        // Repeating the newest frame, as a scheduler does through a hold, keeps working.
        var accepted = 0;
        for (var i = 0; i < 20; i++)
        {
            rig.WaitReady();
            accepted += rig.Presenter.TryPresent(rig.Request(4)) ? 1 : 0;
        }

        Assert.Equal(20, accepted);
        var stats = rig.Presenter.Stats;
        Assert.Equal(1, stats.RetainedShown);
        Assert.Equal(20, stats.Repeats);
        Assert.Equal(0, stats.StaleRequests);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_newer_write_or_a_release_lets_the_retained_write_go() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgba8);
        var p = rig.Presenter;
        void FillFramesInFlight(ulong future)
        {
            Assert.True(p.TryPresent(rig.Request(future)));
            Assert.True(p.TryPresent(rig.Request(future)), "repeat of the same write as the second frame");
        }

        // Refused 2, then a newer write is shown: 2 is released with it.
        FillFramesInFlight(1);
        Assert.False(p.TryPresent(rig.Request(2)));
        Assert.Equal((2UL, 1UL), (p.RetainedWrite, p.LastReleasedWrite));
        rig.Producer.WritePattern(TestPattern.Ramp, 1);
        rig.Producer.WritePattern(TestPattern.Ramp, 2);
        rig.Producer.WritePattern(TestPattern.Ramp, 3);
        rig.WaitReady();
        Assert.True(p.TryPresent(rig.Request(3)));
        Assert.Equal((0UL, 3UL), (p.RetainedWrite, p.LastReleasedWrite));
        Assert.True(rig.Producer.WaitReleased(3, Second));

        // Refused 4, then a newer write 6 is refused too: 4 and 5 are released, 6 is kept.
        FillFramesInFlight(4);
        Assert.False(p.TryPresent(rig.Request(5)));
        Assert.Equal(5UL, p.RetainedWrite);
        Assert.False(p.TryPresent(rig.Request(6)));
        Assert.Equal((6UL, 5UL), (p.RetainedWrite, p.LastReleasedWrite));
        Assert.False(p.TryPresent(rig.Request(5)), "5 was released when the newer 6 was handed over");
        Assert.Equal(1, p.Stats.StaleRequests);

        // An explicit release (through IPresenter) covers the retained write.
        IPresenter core = p;
        core.Release(rig.Request(6));
        Assert.Equal((0UL, 6UL), (p.RetainedWrite, p.LastReleasedWrite));
        for (var i = 4; i <= 6; i++)
        {
            rig.Producer.WritePattern(TestPattern.Ramp, i);
        }

        Assert.True(rig.Producer.WaitReleased(6, Second));
        Assert.False(p.TryPresent(rig.Request(6)), "released writes are stale");
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void The_producer_is_held_back_by_at_most_the_retained_write() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgba8);
        var p = rig.Presenter;
        var n = rig.Ring.SlotCount;

        // Frames 1 and 2 wait for write 1, so the request for write 2 is refused and write 2 retained.
        Assert.True(p.TryPresent(rig.Request(1)));
        Assert.True(p.TryPresent(rig.Request(1)));
        Assert.False(p.TryPresent(rig.Request(2)));
        Assert.Equal(2UL, p.RetainedWrite);

        // The producer can still write N - 1 frames past the retained one; only the write reusing its slot waits.
        for (var i = 1; i <= n + 2; i++)
        {
            rig.Producer.WritePattern(TestPattern.Ramp, i);
        }

        var lastFree = (ulong)(n + 1);
        Assert.True(rig.Producer.WaitWritten(lastFree, Second), $"written stopped at {rig.Producer.ReadWrittenValue()}");
        Thread.Sleep(100);
        Assert.Equal(lastFree, rig.Producer.ReadWrittenValue());

        rig.WaitReady();
        Assert.True(p.TryPresent(rig.Request(2)));
        Assert.True(rig.Producer.WaitWritten(lastFree + 1, Second), "showing the retained write must let the producer go on");
        p.ReleaseThrough(lastFree + 1);
        Assert.True(p.WaitIdle(Second));
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public unsafe void Released_is_never_signalled_before_the_copy_has_read_the_slot() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 640, 360, PixelFormat.Rgba8);
        var api = ctx.Device.Api;
        var type = new VkSemaphoreTypeCreateInfo { semaphoreType = VkSemaphoreType.Timeline };
        var info = new VkSemaphoreCreateInfo { pNext = &type };
        VkSemaphore gate;
        VulkanException.Check(api.vkCreateSemaphore(&info, &gate), "vkCreateSemaphore");
        try
        {
            // The copy batch also waits for the gate: until it opens, the copy cannot have run, so nothing may be released.
            rig.Presenter.CopyGate = gate;
            rig.Presenter.CopyGateValue = 1;
            var n = rig.Producer.WritePattern(TestPattern.Ramp, 1);
            Assert.True(rig.Producer.WaitWritten(n, Second));
            Assert.True(rig.Presenter.TryPresent(rig.Request(n)));
            Thread.Sleep(100);
            Assert.Equal(0UL, rig.Ring.ReadReleasedValue());
            BufferIo.HostSignal(ctx.Device, gate, 1);
            Assert.True(rig.Ring.WaitReleased(n, Second));
            Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 17, 9, 640, 360, 1), rig.ReadPixels()[(9 * 640) + 17]);
            Assert.True(rig.Presenter.WaitIdle(Second));
        }
        finally
        {
            // Open the gate whatever happened, so nothing is left waiting on it when it is destroyed.
            if (BufferIo.Counter(ctx.Device, gate) < 1)
            {
                BufferIo.HostSignal(ctx.Device, gate, 1);
            }

            rig.Presenter.CopyGate = default;
            _ = rig.Presenter.WaitIdle(Second);
            api.vkDestroySemaphore(gate);
        }
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_resize_with_an_undelivered_write_never_blocks_the_present_thread() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 320, 180, PixelFormat.Rgba8);
        var p = rig.Presenter;
        Assert.True(p.TryPresent(rig.Request(1))); // waits on the GPU for a write that has not been made
        rig.Target.Resize(400, 300);
        var worst = TimeSpan.Zero;
        for (var i = 0; i < 20; i++)
        {
            var t = Stopwatch.GetTimestamp();
            Assert.False(p.IsReadyForPresent());
            Assert.False(p.TryPresent(rig.Request(2)), "the rebuild has to wait for the frame in flight");
            var elapsed = Stopwatch.GetElapsedTime(t);
            worst = elapsed > worst ? elapsed : worst;
        }

        Gpu.WriteLine($"worst refused TryPresent during a deferred rebuild: {worst.TotalMilliseconds:F3} ms");
        Assert.True(worst.TotalMilliseconds < 5, $"TryPresent blocked for {worst.TotalMilliseconds:F1} ms");
        Assert.Equal(0, p.Stats.TargetRecreations);
        Assert.Equal(2UL, p.RetainedWrite);

        rig.Producer.WritePattern(TestPattern.Ramp, 1);
        rig.Producer.WritePattern(TestPattern.Ramp, 2);
        rig.WaitReady();
        Assert.True(p.TryPresent(rig.Request(2)));
        Assert.Equal(1, p.Stats.TargetRecreations);
        Assert.Equal(400, rig.Target.Width);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Writes_far_past_the_last_release_are_refused_before_anything_is_submitted() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgba8);
        var p = rig.Presenter;
        IPresenter core = p;
        var lead = (ulong)RingProtocol.MaxWriteLead(rig.Ring.SlotCount);
        Assert.Equal(500, RingProtocol.MaxWaitingReleases);
        Assert.Equal(500, RingProtocol.MaxLeadInSlots);
        Assert.Equal(500UL * (ulong)rig.Ring.SlotCount, lead);
        rig.WriteAndPresent(TestPattern.Ramp, 1);
        var before = p.Stats;

        // Every entry point, with numbers just past the limit and absurdly far (the review lost the device at 40,000).
        foreach (var far in new[] { 1 + lead + 1, 40_000UL, 80_000UL, ulong.MaxValue / 2 })
        {
            var watch = Stopwatch.StartNew();
            Assert.False(core.TryPresent(rig.Request(far)));
            core.Release(rig.Request(far));
            Assert.Throws<ArgumentOutOfRangeException>(() => p.ReleaseThrough(far));
            Assert.True(watch.Elapsed.TotalMilliseconds < 5, $"refusing {far} took {watch.Elapsed.TotalMilliseconds:F1} ms");
        }

        // The refused-request (drop) path: with both frame slots busy, a far write is still refused, not retained.
        Assert.True(p.TryPresent(rig.Request(2)));
        Assert.True(p.TryPresent(rig.Request(2)));
        Assert.False(p.TryPresent(rig.Request(2 + lead + 1)));
        Assert.Equal(0UL, p.RetainedWrite);

        var after = p.Stats;
        Assert.Equal(9, after.RejectedRequests - before.RejectedRequests);
        Assert.Equal(0, after.Releases - before.Releases);
        Assert.Equal(0, after.Dropped - before.Dropped);
        Assert.Equal(2UL, p.LastReleasedWrite);

        // The limit itself is fine: 500 release steps in one call (the most any call submits), and the device and
        // ring stay healthy and usable.
        rig.Producer.WritePattern(TestPattern.Ramp, 2);
        Assert.True(p.WaitIdle(Second));
        var releasesBefore = p.Stats.Releases;
        var limitWatch = Stopwatch.StartNew();
        p.ReleaseThrough(2 + lead);
        var limitMs = limitWatch.Elapsed.TotalMilliseconds;
        Assert.Equal(RingProtocol.MaxLeadInSlots, p.Stats.Releases - releasesBefore);
        Gpu.WriteLine($"ReleaseThrough at the limit: {p.Stats.Releases - releasesBefore} submissions in {limitMs:F1} ms (validation layer on)");
        for (var i = 3UL; i <= 2 + lead; i++)
        {
            rig.Producer.WriteStaged();
        }

        Assert.True(rig.Producer.WaitReleased(2 + lead, Second));
        var n = rig.WriteAndPresent(TestPattern.Ramp, 99);
        Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 5, 6, 160, 90, 99), rig.ReadPixels()[(6 * 160) + 5]);
        Assert.Equal(3 + lead, n);
    });

    /// <summary>
    /// The review's cold-queue failure: a far present that was the first command buffer the queue ever ran lost the
    /// device once 511 releases waited ahead of it (leads of 2,048 to 4,096 at N = 4). On a fresh device each time:
    /// leads past the bound are refused at once, the bound itself (499 releases ahead of the copy) is accepted with
    /// nothing written, anything on top of it is refused, and once the producer has written everything the frame is
    /// shown and the ring keeps working.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_far_present_on_a_cold_queue_is_refused_past_the_bound_and_the_device_stays_healthy()
    {
        for (var run = 0; run < 3; run++)
        {
            var frame = 7 + run;
            Gpu.Run(ctx =>
            {
                using var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgba8);
                var p = rig.Presenter;
                IPresenter core = p;
                var slots = (ulong)rig.Ring.SlotCount;
                var lead = (ulong)RingProtocol.MaxWriteLead(rig.Ring.SlotCount);
                foreach (var far in new[] { lead + 1, lead + slots, 2048UL, 4096UL, 2 * lead })
                {
                    if (far <= lead)
                    {
                        continue;
                    }

                    var watch = Stopwatch.StartNew();
                    Assert.False(core.TryPresent(rig.Request(far)));
                    core.Release(rig.Request(far));
                    Assert.Throws<ArgumentOutOfRangeException>(() => p.ReleaseThrough(far));
                    Assert.True(watch.Elapsed.TotalMilliseconds < 5, $"refusing {far} took {watch.Elapsed.TotalMilliseconds:F1} ms");
                }

                Assert.Equal(0, p.Stats.Releases);
                Assert.Equal(0, p.Stats.Presented);

                // Still the queue's first command buffer, with nothing written: 499 catch-up releases, then the copy.
                Assert.True(core.TryPresent(rig.Request(lead)));
                Assert.Equal(RingProtocol.MaxWaitingReleases - 1, p.Stats.Releases);

                // All of them wait for the producer, so anything that would add one is refused, at once.
                var rejected = p.Stats.RejectedRequests;
                var refuse = Stopwatch.StartNew();
                Assert.False(core.TryPresent(rig.Request(lead + 1)));
                core.Release(rig.Request(lead + 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => p.ReleaseThrough(lead + slots));
                Assert.True(refuse.Elapsed.TotalMilliseconds < 5, $"refusing took {refuse.Elapsed.TotalMilliseconds:F1} ms");
                Assert.Equal(rejected + 2, p.Stats.RejectedRequests);
                Assert.Equal(RingProtocol.MaxWaitingReleases - 1, p.Stats.Releases);

                // The producer catches up: every waiting release runs, the far write is shown, the ring keeps working.
                for (var i = 1UL; i < lead; i++)
                {
                    rig.Producer.WriteStaged();
                }

                Assert.Equal(lead, rig.Producer.WritePattern(TestPattern.Ramp, frame));
                Assert.True(rig.Producer.WaitReleased(lead, Second));
                Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 5, 6, 160, 90, frame), rig.ReadPixels()[(6 * 160) + 5]);
                Assert.Equal(lead + 1, rig.WriteAndPresent(TestPattern.Ramp, frame + 10));
                Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 5, 6, 160, 90, frame + 10), rig.ReadPixels()[(6 * 160) + 5]);
                Assert.True(p.WaitIdle(Second));
            });
        }
    }

    /// <summary>
    /// The review's cumulative failure: bound-sized releases repeated with nothing written piled up waiting releases
    /// (the bound was measured from the last release submitted) until the device was lost at about 6,550, in a call that
    /// blocked for 11 s. Counted from the ring's actual released value, every further one is refused at once, and so is
    /// any other request that would add a release; after the producer has written them, the next bound-sized release is
    /// accepted again. Repeated, with the device healthy and the ring usable throughout.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Far_releases_with_nothing_written_cannot_pile_up() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgba8);
        var p = rig.Presenter;
        IPresenter core = p;
        var slots = (ulong)rig.Ring.SlotCount;
        var lead = (ulong)RingProtocol.MaxWriteLead(rig.Ring.SlotCount);
        rig.WriteAndPresent(TestPattern.Ramp, 1);
        Assert.True(p.WaitIdle(Second));
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var releases = p.Stats.Releases;
            var rejected = p.Stats.RejectedRequests;
            var start = p.LastReleasedWrite;
            p.ReleaseThrough(start + lead);
            Assert.Equal(RingProtocol.MaxWaitingReleases, p.Stats.Releases - releases);

            for (var attempt = 0; attempt < 20; attempt++)
            {
                var far = p.LastReleasedWrite + (attempt % 2 == 0 ? lead : slots);
                var watch = Stopwatch.StartNew();
                Assert.Throws<ArgumentOutOfRangeException>(() => p.ReleaseThrough(far));
                core.Release(rig.Request(far));
                Assert.False(core.TryPresent(rig.Request(far)));
                Assert.False(core.TryPresent(rig.Request(p.LastReleasedWrite + 1))); // an ordinary present, too
                Assert.True(watch.Elapsed.TotalMilliseconds < 5, $"refusing {far} took {watch.Elapsed.TotalMilliseconds:F1} ms");
            }

            Assert.Equal(RingProtocol.MaxWaitingReleases, p.Stats.Releases - releases);
            Assert.Equal(rejected + (20 * 3), p.Stats.RejectedRequests);
            Assert.Equal(start + lead, p.LastReleasedWrite);

            for (var i = start + 1; i <= start + lead; i++)
            {
                rig.Producer.WriteStaged();
            }

            Assert.True(rig.Producer.WaitReleased(start + lead, Second));
            Assert.Equal(start + lead + 1, rig.WriteAndPresent(TestPattern.Ramp, 10 + cycle));
            Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 5, 6, 160, 90, 10 + cycle), rig.ReadPixels()[(6 * 160) + 5]);
            Assert.True(p.WaitIdle(Second));
        }
    });

    /// <summary>
    /// The same bound one frame at a time: while the producer is stalled, frames handed to the presenter are refused
    /// (both frame slots wait for writes) and each releases the one before it. Those releases count as well: they stop at
    /// <see cref="RingProtocol.MaxWaitingReleases"/> waiting, and everything resumes once the producer writes.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Releases_of_refused_frames_from_a_stalled_producer_are_bounded_too() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgba8);
        var p = rig.Presenter;
        var start = rig.WriteAndPresent(TestPattern.Ramp, 1);
        Assert.True(p.WaitIdle(Second));
        var before = p.Stats;
        var n = start;
        var accepted = 0;
        while (p.Stats.RejectedRequests == before.RejectedRequests && n < start + (2 * RingProtocol.MaxWaitingReleases))
        {
            n++;
            accepted += p.TryPresent(rig.Request(n)) ? 1 : 0;
        }

        // Two copies (both frame slots) and the releases of the refused frames but the newest (retained): 500 waiting.
        var after = p.Stats;
        Assert.Equal(2, accepted);
        Assert.Equal(1, after.RejectedRequests - before.RejectedRequests);
        Assert.Equal(RingProtocol.MaxWaitingReleases - 2, after.Releases - before.Releases);
        Assert.Equal(n - 1, p.RetainedWrite);
        for (var i = 0; i < 20; i++)
        {
            Assert.False(p.TryPresent(rig.Request(n + (ulong)i)));
        }

        Assert.Equal(after.Releases, p.Stats.Releases);
        Assert.Equal(n - 1, p.RetainedWrite);

        for (var i = start + 1; i < n; i++)
        {
            rig.Producer.WriteStaged();
        }

        Assert.True(rig.Producer.WaitReleased(n - 2, Second));
        Assert.Equal(n, rig.WriteAndPresent(TestPattern.Ramp, 3));
        Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 5, 6, 160, 90, 3), rig.ReadPixels()[(6 * 160) + 5]);
        Assert.True(p.WaitIdle(Second));
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_request_from_a_replaced_ring_is_refused() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 320, 180, PixelFormat.Rgba8);
        rig.WriteAndPresent(TestPattern.Ramp, 1);
        var old = rig.Request(5000); // a stale request from the first ring, with a write number far ahead of the new one
        rig.Configure(640, 360, PixelFormat.Rgba8);
        var before = rig.Presenter.Stats;
        Assert.False(rig.Presenter.TryPresent(old));
        rig.Presenter.Release(old);
        Assert.Equal(2, rig.Presenter.Stats.RejectedRequests - before.RejectedRequests);
        Assert.Equal(0, rig.Presenter.Stats.Releases - before.Releases);
        rig.Target.Resize(640, 360);
        rig.WriteAndPresent(TestPattern.Ramp, 2);
        Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 639, 359, 640, 360, 2), rig.ReadPixels()[^1]);
    });

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(false)]
    [InlineData(true)]
    public void A_producer_waiting_on_the_retained_write_finishes_when_the_presenter_lets_go(bool dispose) => Gpu.Run(ctx =>
    {
        var rig = OffscreenRig.Create(ctx, 160, 90, PixelFormat.Rgba8);
        try
        {
            var p = rig.Presenter;
            var producer = rig.Producer;
            var n = rig.Ring.SlotCount;

            // Write 2 is refused and retained; the producer's write N + 2 reuses its slot and waits for its release.
            Assert.True(p.TryPresent(rig.Request(1)));
            Assert.True(p.TryPresent(rig.Request(1)));
            Assert.False(p.TryPresent(rig.Request(2)));
            Assert.Equal(2UL, p.RetainedWrite);
            for (var i = 1; i <= n + 2; i++)
            {
                producer.WriteStaged();
            }

            Assert.True(producer.WaitWritten((ulong)(n + 1), Second));
            Thread.Sleep(50);
            Assert.Equal((ulong)(n + 1), producer.ReadWrittenValue());

            if (dispose)
            {
                p.Dispose();
            }
            else
            {
                // Replacing the ring: nothing on the old one will be shown, so its producer must be let go.
                p.Configure(new PresenterConfig { Width = 320, Height = 180, Format = PixelFormat.Rgba8 });
            }

            Assert.True(producer.WaitWritten((ulong)(n + 2), Second), $"the producer is still waiting on the retained write (written {producer.ReadWrittenValue()})");
        }
        finally
        {
            rig.Dispose();
        }
    });

    /// <summary>
    /// Random interleavings of writes, presents (of any unreleased write, far ahead included), repeats, stale requests,
    /// explicit releases, requests for writes not yet made, resizes and ring replacements, with the screen compared
    /// bit-exact against what must be shown. No ordering restriction to N steps any more.
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Random_orderings_keep_content_and_validation_clean(int seed) => Gpu.Run(ctx =>
    {
        var rng = new Random(seed);
        (int W, int H, PixelFormat F)[] shapes = [(320, 180, PixelFormat.Rgba8), (333, 77, PixelFormat.Rgb10A2), (640, 360, PixelFormat.Rgba8)];
        var shape = shapes[0];
        using var rig = OffscreenRig.Create(ctx, shape.W, shape.H, shape.F, presenter: new VulkanPresenterOptions { SlotCount = 4 + (seed % 3) });
        var presenter = rig.Presenter;
        ulong shown = 0;
        int checks = 0, drops = 0, futures = 0, reconfigs = 0, resizes = 0, retainedShown = 0;
        var targetMatches = true;

        void Verify()
        {
            if (shown == 0 || !targetMatches)
            {
                return;
            }

            var pixels = rig.ReadPixels();
            for (var y = 0; y < shape.H; y++)
            {
                for (var x = 0; x < shape.W; x++)
                {
                    var expected = TestPatterns.Pixel(TestPattern.Ramp, shape.F, x, y, shape.W, shape.H, (int)shown);
                    if (pixels[(y * shape.W) + x] != expected)
                    {
                        Assert.Fail($"seed {seed}: after showing write {shown}, pixel ({x},{y}) = {pixels[(y * shape.W) + x]:X8}, expected {expected:X8}");
                    }
                }
            }

            checks++;
        }

        // The diagnostic producer waits on the CPU before reusing a staging buffer, so keep it within 2N of the releases.
        bool CanWrite() => rig.Producer.NextWrite <= presenter.LastReleasedWrite + (ulong)(2 * rig.Ring.SlotCount);

        void Write()
        {
            var n = rig.Producer.NextWrite;
            Assert.Equal(n, rig.Producer.WritePattern(TestPattern.Ramp, (int)n));
        }

        for (var step = 0; step < 4000; step++)
        {
            var next = rig.Producer.NextWrite;
            var released = presenter.LastReleasedWrite;
            var copied = presenter.LastPresentedWrite;
            var r = rng.Next(100);
            if (r < 35)
            {
                if (CanWrite())
                {
                    Write();
                }
            }
            else if (r < 65)
            {
                if (next - 1 > released)
                {
                    var m = rng.Next(3) == 0 ? released + 1 + (ulong)rng.NextInt64((long)(next - 1 - released)) : next - 1;
                    if (!presenter.IsReadyForPresent() && rng.Next(2) == 0)
                    {
                        continue;
                    }

                    var wasRetained = m == presenter.RetainedWrite;
                    if (presenter.TryPresent(rig.Request(m)))
                    {
                        shown = m;
                        retainedShown += wasRetained ? 1 : 0;
                    }
                    else
                    {
                        drops++;
                        Assert.True(presenter.LastReleasedWrite >= m - 1, "everything older than a refused write is released");
                        Assert.True(presenter.RetainedWrite == m || presenter.RetainedWrite > m || presenter.LastReleasedWrite >= m);
                    }
                }
            }
            else if (r < 75)
            {
                if (copied > 0 && presenter.TryPresent(rig.Request(copied)))
                {
                    shown = copied;
                }
            }
            else if (r < 80)
            {
                if (next - 1 > released)
                {
                    presenter.ReleaseThrough(released + 1 + (ulong)rng.NextInt64((long)(next - 1 - released)));
                }
            }
            else if (r < 85)
            {
                if (released > 1)
                {
                    var k = 1 + (ulong)rng.NextInt64((long)released);
                    if (k != copied)
                    {
                        Assert.False(presenter.TryPresent(rig.Request(k)));
                    }
                }
            }
            else if (r < 90)
            {
                // A request for a write that does not exist yet: the GPU waits, then the producer writes it.
                if (CanWrite() && presenter.IsReadyForPresent())
                {
                    if (presenter.TryPresent(rig.Request(next)))
                    {
                        shown = next;
                        futures++;
                    }

                    Write();
                }
            }
            else if (r < 96)
            {
                Verify();
            }
            else if (r < 98)
            {
                if (rng.Next(2) == 0)
                {
                    rig.Target.Resize(shape.W + 16, shape.H + 8);
                    targetMatches = false;
                }
                else
                {
                    rig.Target.Resize(shape.W, shape.H);
                    targetMatches = true;
                }

                shown = 0;
                resizes++;
            }
            else if (r < 99)
            {
                // Ring replacement with work in flight: release everything first (any distance) so the old producer finishes.
                if (next > 1)
                {
                    presenter.ReleaseThrough(next - 1);
                }

                shape = shapes[rng.Next(shapes.Length)];
                rig.Target.Resize(shape.W, shape.H);
                targetMatches = true;
                rig.Configure(shape.W, shape.H, shape.F);
                shown = 0;
                reconfigs++;
            }
        }

        var last = rig.Producer.NextWrite - 1;
        if (last > 0)
        {
            presenter.ReleaseThrough(last);
        }

        Assert.True(presenter.WaitIdle(3 * Second));
        Verify();
        Gpu.WriteLine($"seed {seed}: {checks} content checks, {drops} drops, {retainedShown} retained writes shown, {futures} future presents, {resizes} resizes, {reconfigs} ring replacements; {presenter.Stats}");
        Assert.True(drops > 0 && checks > 50);
        Assert.InRange(presenter.Stats.MaxPresentsInFlight, 1, 2);
    });
}
