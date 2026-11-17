using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Gpu.Tests.Support;
using FrameMotionPlus.Vulkan;
using Reference = FrameMotionPlus.Cuda.Tests.Support.Reference;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>
/// How far a producer may run ahead, end to end. The bridge's lead is the writes handed out that the GPU has not
/// finished; it never exceeds <see cref="CudaVulkanBridge.MaxOutstanding"/>, a limit chosen so the producer's stream
/// cannot fill and the presenter's bounds cannot be reached. Everything handed out can be presented, released and
/// drained, and nothing here may hang: every potentially blocking call runs under a bounded wait that fails the test.
/// </summary>
public class LeadTests
{
    private const int Width = 320;
    private const int Height = 180;

    public static TheoryData<int, int, int> Leads => new()
    {
        // writes handed out, MaxLead (0 = 2N), operations per write (0 = 16)
        { 12, 0, 0 },     // 3N with the default limit: N finished, 2N waiting for releases
        { 33, 29, 16 },   // the review's count
        { 254, 250, 3 },  // the ceiling: wait + kernel + signal = 750 queued operations of the 960 allowed
    };

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [MemberData(nameof(Leads))]
    public void A_producer_far_ahead_of_a_stalled_presenter_is_caught_up_and_shown_bit_exact(int lead, int maxLead, int operations) => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        var n = rig.Ring.SlotCount;
        var bridge = rig.Bridge;
        Assert.Equal(maxLead == 0 ? 2 * n : maxLead, bridge.MaxOutstanding);
        var newest = WriteWithPresenterStalled(rig, lead);
        Assert.Equal(lead - n, bridge.Outstanding);
        Assert.Equal(bridge.MaxOutstanding, lead - n); // every case runs right up to the limit
        Assert.False(bridge.TryAcquireSlot(out _), "the limit was not enforced");

        var watch = Stopwatch.StartNew();
        Bounded("presenting the newest write", () => rig.Present(newest));
        var presentMs = watch.Elapsed.TotalMilliseconds;
        Interop.AssertPixels(Reference.Pattern(Width, Height, (uint)(1000 + lead), PixelFormat.Rgba8), rig.ReadPixels(), Width, $"write {lead}");
        Interop.WriteLine($"lead {lead}: present (with {(lead - 1) / n} catch-up release steps) took {presentMs:F2} ms; {rig.Presenter.Stats}");
        Assert.True(rig.Ring.WaitWritten(bridge.LastAcquiredWrite, 2_000_000_000));
        Assert.Equal(0, bridge.Outstanding);
        Assert.Equal(0, rig.Presenter.Stats.RejectedRequests);

        // And the ring goes on normally.
        var next = rig.WritePattern(7);
        rig.Present(next);
        Interop.AssertPixels(Reference.Pattern(Width, Height, 7, PixelFormat.Rgba8), rig.ReadPixels(), Width, "the write after");
    }, new RigOptions { Bridge = BridgeCompat.Options(maxLead, operations) });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void TryAcquireSlot_refuses_at_the_limit_and_hands_out_again_once_the_work_finishes() => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        var bridge = rig.Bridge;
        var n = rig.Ring.SlotCount;
        var limit = (int)bridge.MaxOutstanding;
        Assert.Equal(2 * n, limit);
        var newest = WriteWithPresenterStalled(rig, n + limit);
        Assert.Equal(limit, bridge.Outstanding);
        Assert.False(bridge.TryAcquireSlot(out var refused));
        Assert.Equal(default, refused);
        var e = Assert.Throws<InvalidOperationException>(() => bridge.AcquireSlot());
        Assert.Contains("TryAcquireSlot", e.Message, StringComparison.Ordinal);
        Assert.Equal((ulong)(n + limit), bridge.LastAcquiredWrite); // refusals hand out nothing

        // A present releases the backlog, the writes finish, and acquiring resumes.
        rig.Present(newest);
        Assert.True(rig.Ring.WaitWritten(newest.Write, 2_000_000_000));
        Assert.Equal(0, bridge.Outstanding);
        var again = rig.WritePattern(99);
        Assert.Equal(newest.Write + 1, again.Write);
        rig.Present(again);
        Interop.AssertPixels(Reference.Pattern(Width, Height, 99, PixelFormat.Rgba8), rig.ReadPixels(), Width, "after the limit");

        // So does a release without a present (the pipeline deciding not to show those frames).
        Assert.True(rig.Ring.WaitWritten(again.Write, 2_000_000_000));
        for (var i = 0; i < n; i++)
        {
            newest = rig.WritePattern((uint)i);
        }

        Assert.True(rig.Ring.WaitWritten(newest.Write, 2_000_000_000));
        for (var i = 0; i < limit; i++)
        {
            newest = rig.WritePattern((uint)i);
        }

        Assert.False(bridge.TryAcquireSlot(out _));
        rig.Presenter.Release(rig.Request(newest));
        Assert.True(rig.Ring.WaitWritten(newest.Write, 2_000_000_000));
        Assert.True(bridge.TryAcquireSlot(out var afterRelease));
        rig.Stream.Signal(afterRelease.ProducerSignal);
        Assert.Equal(0, rig.Presenter.Stats.RejectedRequests);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Configure_and_Dispose_drain_the_maximum_lead() => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        var n = rig.Ring.SlotCount;
        var limit = (int)rig.Bridge.MaxOutstanding;
        WriteWithPresenterStalled(rig, n + limit);
        Assert.False(rig.Bridge.TryAcquireSlot(out _));
        Bounded("Configure with the maximum lead outstanding", () => rig.Reconfigure(640, 360, PixelFormat.Rgb10A2));
        Assert.True(rig.Stream.IsIdle);
        Assert.Equal(0, rig.Bridge.AbandonedImports);

        var newest = WriteWithPresenterStalled(rig, n + limit);
        Assert.False(rig.Bridge.TryAcquireSlot(out _));
        rig.Present(newest);
        Interop.AssertPixels(Reference.Pattern(640, 360, (uint)(1000 + n + limit), PixelFormat.Rgb10A2), rig.ReadPixels(), 640, "on the new ring");
        Assert.True(rig.Ring.WaitWritten(newest.Write, 2_000_000_000));
        WriteWithPresenterStalled(rig, n + limit);
        Bounded("Dispose with the maximum lead outstanding", rig.Bridge.Dispose);
        Assert.True(rig.Stream.IsIdle);
        Assert.Equal(0, rig.Bridge.AbandonedImports);
        Assert.Equal(0, rig.Presenter.Stats.RejectedRequests);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Configure_refuses_a_limit_that_could_fill_the_stream_or_reach_the_presenters_bounds() => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        void Attempt(int maxLead, int operations, bool accepted)
        {
            using var target = new OffscreenTarget(rig.Device, new OffscreenTargetOptions { Width = Width, Height = Height });
            using var presenter = new VulkanPresenter(rig.Device, target);
            using var bridge = new CudaVulkanBridge(rig.Cuda, presenter, BridgeCompat.Options(maxLead, operations));
            var config = new BridgeConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 };
            if (accepted)
            {
                bridge.Configure(config);
                Assert.Equal(maxLead, bridge.MaxOutstanding);
            }
            else
            {
                var e = Assert.Throws<ArgumentOutOfRangeException>(() => bridge.Configure(config));
                Interop.WriteLine(e.Message);
                Assert.False(bridge.IsConfigured);
            }
        }

        Assert.Equal(960, CudaVulkanBridgeOptions.StreamQueueOperations);
        Assert.Equal(250, CudaVulkanBridgeOptions.MaxLeadCeiling);
        Attempt(60, 16, accepted: true);    // 960 operations: the most
        Attempt(61, 16, accepted: false);   // 976
        Attempt(250, 3, accepted: true);    // the ceiling
        Attempt(251, 1, accepted: false);   // past it: the presenter's waiting bound could be reached
        Attempt(1, 960, accepted: true);
        Attempt(1, 961, accepted: false);
        Assert.True(CudaVulkanBridgeOptions.MaxLeadCeiling + rig.Ring.SlotCount <= RingProtocol.MaxWriteLead(rig.Ring.SlotCount));
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_presenter_that_cannot_release_does_not_hang_and_its_import_is_reaped_once_the_presenter_lets_go() => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        // The presenter owns the ring but its release fails. The bridge must not release from the CUDA side behind its
        // back (the presenter's own releases could then signal lower values): Configure times out, bounded, and keeps
        // the ring; Dispose returns at once and hands the stuck import to the reaper, which releases from CUDA only once
        // the presenter has let go of the ring, lets the queued work finish and frees the import.
        rig.Bridge.Dispose();
        rig.Bridge = new CudaVulkanBridge(rig.Cuda, rig.Presenter, BridgeCompat.Options(drainTimeoutNs: 300_000_000), new BridgeTestHooks
        {
            BeforeRelease = _ => throw new InvalidOperationException("injected presenter failure"),
        });
        rig.Bridge.Configure(new BridgeConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 });
        WriteWithPresenterStalled(rig, 3 * rig.Ring.SlotCount);
        Assert.False(rig.Stream.IsIdle);
        Bounded("Configure when the presenter cannot release", () => Assert.Throws<TimeoutException>(() => rig.Reconfigure(640, 360, PixelFormat.Rgba8)));
        Assert.True(rig.Bridge.IsConfigured);
        Assert.Equal(rig.Presenter.LastReleasedWrite, rig.Ring.ReadReleasedValue()); // nobody signalled 'released' behind the presenter

        var watch = Stopwatch.StartNew();
        Bounded("Dispose when the presenter cannot release", rig.Bridge.Dispose);
        Assert.True(watch.Elapsed.TotalSeconds < 2, $"Dispose took {watch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(1, rig.Bridge.AbandonedImports);
        Assert.False(rig.Stream.IsIdle);

        Bounded("disposing the presenter", rig.Presenter.Dispose);
        Assert.True(BridgeCompat.WaitForReaper(TimeSpan.FromSeconds(10)), "the reaper never freed the import");
        Assert.True(rig.Stream.IsIdle);
    }, new RigOptions { Bridge = BridgeCompat.Options(maxLead: 16) });

    /// <summary>
    /// Hands out <paramref name="count"/> writes with the presenter stalled (none presented), each drawing pattern
    /// 1000 + i: the first N finish (waited for), the rest wait for releases. Returns the newest slot.
    /// </summary>
    private static BridgeSlot WriteWithPresenterStalled(InteropRig rig, int count)
    {
        var n = rig.Ring.SlotCount;
        var first = rig.Bridge.LastAcquiredWrite + 1;
        BridgeSlot slot = default;
        for (var i = 1; i <= count; i++)
        {
            if (i == n + 1)
            {
                // From here on the writes wait for releases; make sure the first lap's have finished, so the lead the
                // bridge sees is exactly the waiting ones.
                Assert.True(rig.Ring.WaitWritten(first + (ulong)n - 1, 2_000_000_000));
            }

            Assert.True(rig.Bridge.TryAcquireSlot(out slot), $"write {i} of {count} refused (outstanding {rig.Bridge.Outstanding} of {rig.Bridge.MaxOutstanding})");
            rig.Stream.Wait(slot.ProducerWait);
            rig.Kernels.EnqueueTestPattern(rig.Stream, slot.ProducerView, (uint)(1000 + i));
            rig.Stream.Signal(slot.ProducerSignal);
        }

        return slot;
    }

    /// <summary>Runs <paramref name="action"/> on its own thread and fails the test if it does not finish in 20 s.</summary>
    private static void Bounded(string what, Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        }) { IsBackground = true, Name = "fmp-bounded" };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), $"{what} did not finish within 20 s (hung)");
        failure?.Throw();
    }
}
