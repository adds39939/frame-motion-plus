using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Gpu.Tests.Support;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>
/// The bridge backlog from the Phase 2 review (implementation plan, Phase 4), each item reproduced the way the reviewer
/// did (experiments X10 and X8) and asserted fixed. Every step that could block is bounded and run on its own thread.
/// </summary>
[Trait(Category.Name, Category.Gpu)]
public class BridgeBacklogTests
{
    private const int W = 320;
    private const int H = 180;
    private static readonly BridgeConfig Config = new() { Width = W, Height = H, Format = PixelFormat.Rgba8 };

    /// <summary>
    /// Item 1 (X10): an abandoned import whose work is done (e1) and a second abandoned import (e2) whose producer writes
    /// wait for releases that failed, with no bridge live. The reaper used to free e1 then: the free waited for e2's stuck
    /// writes, holding the lock a bridge constructor takes, so a new bridge waited until the presenter went (5 s and more
    /// seen, 60 s teardown bounds). Now no entry on a context is freed while another one there is not done, so the
    /// constructor returns at once; once the presenter has gone and the new bridge is disposed, both are freed.
    /// </summary>
    [Fact]
    public void A_finished_abandoned_import_is_not_freed_behind_another_ones_stuck_writes() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        var options = BridgeCompat.Options(drainTimeoutNs: 100_000_000);
        var pendingBefore = BridgeCompat.PendingReaps;

        // e1: one write never signalled.
        var a1 = new CudaVulkanBridge(rig.Cuda, rig.Presenter, options);
        rig.Bridge = a1;
        a1.Configure(Config);
        rig.Present(rig.WritePattern(1));
        Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
        _ = a1.AcquireSlot();
        Bounded("the first abandoning Dispose", a1.Dispose);
        Assert.Equal(1, a1.AbandonedImports);

        // e2: releasing through the presenter fails, and writes are queued that wait for releases.
        var a2 = new CudaVulkanBridge(rig.Cuda, rig.Presenter, options, new BridgeTestHooks
        {
            BeforeRelease = static _ => throw new InvalidOperationException("injected: releasing through the presenter fails"),
        });
        rig.Bridge = a2;
        a2.Configure(Config);
        for (var i = 0; i < 4; i++)
        {
            rig.Present(rig.WritePattern((uint)(10 + i))); // written passes e1's write: e1 is done, held while a2 is live
        }

        Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
        for (var i = 0; i < 6; i++)
        {
            _ = rig.WritePattern((uint)(20 + i)); // not presented: the last ones wait for releases that never come
        }

        Thread.Sleep(50);
        Assert.False(rig.Stream.IsIdle, "the producer's last writes were expected to wait for releases");
        Bounded("the second abandoning Dispose", a2.Dispose);
        Assert.Equal(1, a2.AbandonedImports);
        Thread.Sleep(100); // several of the reaper's looks with no bridge live

        var blocked = new List<string>();
        CudaVulkanBridge? fresh = null;
        var constructorMs = Timed("a new bridge's constructor", () => fresh = new CudaVulkanBridge(rig.Cuda, rig.Presenter, options), blocked);
        Interop.WriteLine($"a bridge constructed with one finished and one stuck abandoned import: {constructorMs:F1} ms; pending {BridgeCompat.PendingReaps - pendingBefore}");
        Assert.True(blocked.Count == 0, "the constructor waited behind the reaper's free: " + string.Join(", ", blocked));
        Assert.True(constructorMs < 1000, $"the constructor took {constructorMs:F0} ms");
        Assert.Equal(pendingBefore + 2, BridgeCompat.PendingReaps); // both held: e2 is not done, so e1 waits for it

        // The presenter lets go: the reaper releases e2's writes from CUDA, they finish, and both are freed once the new
        // bridge (live, so holding them) is disposed.
        Assert.NotNull(fresh);
        rig.Bridge = fresh;
        Bounded("disposing the presenter", rig.Presenter.Dispose);
        Thread.Sleep(50);
        Assert.True(SpinUntil(() => rig.Stream.IsIdle, TimeSpan.FromSeconds(5)), "e2's writes never finished after the reaper released them");
        Bounded("disposing the new bridge", fresh.Dispose);
        Assert.True(BridgeCompat.WaitForReaper(TimeSpan.FromSeconds(10)), "the abandoned imports were never freed");
        Assert.Equal(pendingBefore, BridgeCompat.PendingReaps);
    }, new RigOptions { CreateBridge = false, Readback = false });

    /// <summary>
    /// Item 2 (X8): the presenter is disposed first, then the bridge, with a write acquired but never signalled. The
    /// bridge's drain used to queue a GPU-side wait for that write on a stream of its own, which could never complete:
    /// every later CUDA free in the context blocked for good (a 1 MiB free never returned). Now the drain releases from
    /// CUDA and hands the import to the reaper with nothing queued: the free completes at once, and the import is freed,
    /// not dropped, by the free pass at the bridge's own disposal.
    /// </summary>
    [Fact]
    public void A_bridge_disposed_after_its_presenter_with_a_write_never_signalled_wedges_nothing() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        var pendingBefore = BridgeCompat.PendingReaps;
        var dropped = CudaVulkanBridge.DroppedReaps;
        var bridge = new CudaVulkanBridge(rig.Cuda, rig.Presenter, BridgeCompat.Options(drainTimeoutNs: 300_000_000));
        rig.Bridge = bridge;
        bridge.Configure(Config);
        rig.Present(rig.WritePattern(1));
        Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
        var buffer = CudaDeviceBuffer.AllocateImage(rig.Cuda, 512, 512, PixelFormat.Rgba8); // 1 MiB
        _ = bridge.AcquireSlot(); // never signalled, never handed to the presenter
        Bounded("disposing the presenter", rig.Presenter.Dispose);
        var watch = Stopwatch.StartNew();
        Bounded("disposing the bridge after the presenter", bridge.Dispose);
        var disposeMs = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(1, bridge.AbandonedImports);

        var blocked = new List<string>();
        var freeMs = Timed("a 1 MiB cuMemFree", buffer.Dispose, blocked);
        var streamMs = Timed("a stream creation", () => CudaStream.Create(rig.Cuda).Dispose(), blocked);
        Interop.WriteLine($"bridge disposed after its presenter: Dispose {disposeMs:F1} ms; then a free {freeMs:F2} ms, a stream {streamMs:F2} ms; pending {BridgeCompat.PendingReaps - pendingBefore}");
        Assert.True(blocked.Count == 0, "blocked for 3 s or more: " + string.Join(", ", blocked));
        Assert.True(disposeMs < 1000, $"Dispose took {disposeMs:F0} ms; with the presenter gone it waits for nothing");
        Assert.True(BridgeCompat.WaitForReaper(TimeSpan.FromSeconds(5)), "the reaper never freed the import");
        Assert.Equal(dropped, CudaVulkanBridge.DroppedReaps); // freed, not dropped
    }, new RigOptions { CreateBridge = false, Readback = false });

    /// <summary>
    /// Item 3: an application that restarts its bridge at once. An abandoned import (a write never signalled) is done once
    /// a later bridge's writes pass it, and held while that bridge is live. The reaper used to free it only at its next
    /// 5 ms look with no bridge live, so a bridge created right after the last one was disposed kept it (and the next
    /// restart the same) until shutdown. Now the last bridge's Dispose frees what is done before it returns. The reaper
    /// thread is held at the start of its looks meanwhile (a stand-in for "the new bridge came first"), so only the
    /// free at disposal can have done it.
    /// </summary>
    [Fact]
    public void Abandoned_imports_are_freed_when_the_last_bridge_is_disposed_even_if_a_new_one_follows_at_once() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        using var reaperLooks = new ManualResetEventSlim(true);
        var options = BridgeCompat.Options(drainTimeoutNs: 100_000_000);
        var pendingBefore = BridgeCompat.PendingReaps;
        try
        {
            var abandoned = new CudaVulkanBridge(rig.Cuda, rig.Presenter, options, new BridgeTestHooks
            {
                BeforeReapStep = () => reaperLooks.Wait(TimeSpan.FromSeconds(20)),
            });
            rig.Bridge = abandoned;
            abandoned.Configure(Config);
            rig.Present(rig.WritePattern(1));
            Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
            _ = abandoned.AcquireSlot(); // never signalled
            Bounded("the abandoning Dispose", abandoned.Dispose);
            Assert.Equal(1, abandoned.AbandonedImports);

            var running = new CudaVulkanBridge(rig.Cuda, rig.Presenter, options);
            rig.Bridge = running;
            running.Configure(Config);
            for (var i = 0; i < 6; i++)
            {
                rig.Present(rig.WritePattern((uint)(10 + i))); // written passes the abandoned write: it is done, and held
            }

            Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
            Thread.Sleep(50); // the reaper has seen it done
            Assert.Equal(pendingBefore + 1, BridgeCompat.PendingReaps);

            reaperLooks.Reset(); // from here the reaper thread stops at the start of its next look
            Thread.Sleep(20);
            Bounded("disposing the running bridge", running.Dispose);
            var pendingAfterDispose = BridgeCompat.PendingReaps;
            var restarted = new CudaVulkanBridge(rig.Cuda, rig.Presenter, options); // the application restarts at once
            rig.Bridge = restarted;
            restarted.Configure(Config);
            rig.Present(rig.WritePattern(99));
            Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
            Interop.WriteLine($"pending reaps: {pendingAfterDispose - pendingBefore} right after the last bridge's Dispose, {BridgeCompat.PendingReaps - pendingBefore} with the restarted bridge live");
            Assert.Equal(pendingBefore, pendingAfterDispose);
        }
        finally
        {
            reaperLooks.Set();
        }
    }, new RigOptions { CreateBridge = false, Readback = false });

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                return false;
            }

            Thread.Sleep(1);
        }

        return true;
    }

    /// <summary>Runs <paramref name="action"/> on another thread; a call still blocked after 3 s is recorded in <paramref name="slow"/> (and left to finish on its own).</summary>
    private static double Timed(string what, Action action, List<string> slow)
    {
        var watch = Stopwatch.StartNew();
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
        }) { IsBackground = true, Name = "fmp-other-cuda-work" };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(3)))
        {
            slow.Add(what);
            return watch.Elapsed.TotalMilliseconds;
        }

        failure?.Throw();
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Runs <paramref name="action"/> on its own thread and fails the test if it does not finish in 60 s.</summary>
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), $"{what} did not finish within 60 s (hung)");
        failure?.Throw();
    }
}
