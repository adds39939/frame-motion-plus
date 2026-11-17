using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Gpu.Tests.Support;
using Reference = FrameMotionPlus.Cuda.Tests.Support.Reference;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>
/// Regressions for the join review's findings, each reproducing the reviewer's scenario. The loops here present every
/// frame they produce at once, with no readiness gate and no retry of a refusal (unlike fmp pattern's demo pacing), so
/// the bridge's and presenter's own rules are what keep the pipeline going. Every potentially blocking step is bounded.
/// </summary>
public class ReviewRegressionTests
{
    private const int W = 320;
    private const int H = 180;
    private const long Period240 = 4_166_667;

    public static TheoryData<long> Stalls => new() { 500_000_000L, 2_500_000_000L, 10_000_000_000L };

    /// <summary>Finding 1 / I1: after the producer's GPU work stalls (any length) the loop resumes by itself.</summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [MemberData(nameof(Stalls))]
    public void After_a_producer_stall_the_loop_resumes_showing_correct_frames(long stallNs) => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        var stats = RunLoop(rig, stallNs, producerStall: true);
        AssertRecovered(rig, stats, stallNs);
    });

    /// <summary>
    /// I1: after the presenter stalls (not called for a while, the producer going on, its newest write waiting in the
    /// mailbox as a separate present thread's would) the loop resumes by itself.
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [MemberData(nameof(Stalls))]
    public void After_a_presenter_stall_the_loop_resumes_showing_correct_frames(long stallNs) => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        var stats = RunLoop(rig, stallNs, producerStall: false);
        AssertRecovered(rig, stats, stallNs);
    });

    /// <summary>
    /// Finding 1, the review's exact scenario: the GPU produces nothing for a while (one long delay at the head of the
    /// producer stream) while the pipeline keeps going at full speed, acquire → signal → TryPresent, never retrying a
    /// refusal; then, once the queued work has run, a normal loop of 240 frames. Before the fix the presenter refused
    /// writes past its waiting bound without releasing them and the bridge refused every acquire from then on.
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(2_500_000_000L, 800)]
    [InlineData(1_000_000_000L, 300)]
    public void After_a_producer_stall_at_full_speed_the_pipeline_recovers(long stallNs, int framesDuringStall) => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        var bridge = rig.Bridge;
        long held = 0, shown = 0, maxOutstanding = 0;
        Bounded("the full-speed stall", () =>
        {
            rig.Kernels.EnqueueDelay(rig.Stream, stallNs);
            for (var i = 1; i <= framesDuringStall; i++)
            {
                if (!bridge.TryAcquireSlot(out var slot))
                {
                    held++;
                    continue;
                }

                rig.Stream.Signal(slot.ProducerSignal); // content does not matter during the stall
                maxOutstanding = Math.Max(maxOutstanding, bridge.Outstanding);
                shown += rig.Presenter.TryPresent(rig.Request(slot)) ? 1 : 0;
            }

            Assert.True(rig.Ring.WaitWritten(bridge.LastAcquiredWrite, (stallNs * 2) + 2_000_000_000), "the queued work never ran");
            Assert.True(rig.Presenter.WaitIdle(5_000_000_000));
        });

        var during = $"during a {stallNs / 1e9:F1} s stall, {framesDuringStall} frames: acquired {bridge.LastAcquiredWrite}, held back {held}, shown {shown}, outstanding at most {maxOutstanding} of {bridge.MaxOutstanding}; ";
        long acquired = 0, heldAfter = 0, shownAfter = 0;
        Bounded("the loop after the stall", () =>
        {
            for (var i = 1; i <= 240; i++)
            {
                Thread.Sleep(2);
                if (!bridge.TryAcquireSlot(out var slot))
                {
                    heldAfter++;
                    continue;
                }

                acquired++;
                rig.Stream.Wait(slot.ProducerWait);
                rig.Kernels.EnqueueTestPattern(rig.Stream, slot.ProducerView, (uint)(5000 + i));
                rig.Stream.Signal(slot.ProducerSignal);
                rig.WaitReady();
                if (rig.Presenter.TryPresent(rig.Request(slot)))
                {
                    shownAfter++;
                    Assert.Equal((uint)(5000 + i), Reference.DecodeCounter(rig.ReadPixels(), W, H, PixelFormat.Rgba8));
                }
            }
        });

        Interop.WriteLine(during + $"then 240 frames: {acquired} acquired, {heldAfter} held back, {shownAfter} shown; outstanding {bridge.Outstanding}; {rig.Presenter.Stats}");
        Assert.Equal(0, rig.Presenter.Stats.RejectedRequests);
        Assert.True(maxOutstanding <= bridge.MaxOutstanding);
        Assert.True(acquired >= 230, $"only {acquired} of 240 frames acquired after the stall: the bridge stayed stuck");
        Assert.True(shownAfter >= 230, $"only {shownAfter} of 240 frames shown after the stall");
    });

    /// <summary>Finding 3 / I2: a slow producer (6 ms of GPU work per write at 240 Hz) is held back, not queued.</summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_slow_producer_is_held_back_instead_of_building_a_backlog() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        var bridge = rig.Bridge;
        long held = 0, acquired = 0, maxBacklog = 0;
        Bounded("the slow-producer loop", () =>
        {
            var next = Stopwatch.GetTimestamp();
            for (var i = 1; i <= 600; i++)
            {
                next = Pace(next, Period240);
                if (!bridge.TryAcquireSlot(out var slot))
                {
                    held++;
                    continue;
                }

                acquired++;
                rig.Stream.Wait(slot.ProducerWait);
                rig.Kernels.EnqueueDelay(rig.Stream, 6_000_000);
                rig.Kernels.EnqueueTestPattern(rig.Stream, slot.ProducerView, (uint)i);
                rig.Stream.Signal(slot.ProducerSignal);
                _ = rig.Presenter.TryPresent(rig.Request(slot));
                maxBacklog = Math.Max(maxBacklog, (long)(bridge.LastAcquiredWrite - rig.Ring.ReadWrittenValue()));
            }
        });

        Interop.WriteLine($"600 slots at 240 Hz, 6 ms per write: {acquired} acquired, {held} held back; GPU backlog (handed out - written) at most {maxBacklog} of {bridge.MaxOutstanding}; {rig.Presenter.Stats}");
        Assert.True(maxBacklog <= bridge.MaxOutstanding, $"the backlog reached {maxBacklog} writes, past the limit of {bridge.MaxOutstanding}");
        Assert.True(held > 0, "nothing was held back, so a 6 ms producer at 240 Hz must have queued");
        Assert.Equal(0, rig.Presenter.Stats.RejectedRequests);
        Assert.True(rig.Ring.WaitWritten(bridge.LastAcquiredWrite, 5_000_000_000));
    }, new RigOptions { Readback = false });

    /// <summary>
    /// Finding 3 / I3: within the allowed lead the producer never blocks inside a CUDA enqueue, even with the most
    /// operations per write the limit allows and a GPU that takes 20 ms per write (a block would last about that long).
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Within_the_limit_enqueueing_never_blocks_under_a_slow_GPU() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        const int Operations = 16;
        var maxLead = 960 / Operations;
        rig.Bridge.Dispose();
        rig.Bridge = new CudaVulkanBridge(rig.Cuda, rig.Presenter, BridgeCompat.Options(maxLead, Operations));
        rig.Bridge.Configure(new BridgeConfig { Width = W, Height = H, Format = PixelFormat.Rgba8 });
        using var scratch = CudaEvent.Create(rig.Cuda);
        var bridge = rig.Bridge;
        long worstTicks = 0, acquired = 0, held = 0;
        Bounded("the slow-GPU loop", () =>
        {
            var end = Stopwatch.GetTimestamp() + (2 * Stopwatch.Frequency);
            for (var i = 1; Stopwatch.GetTimestamp() < end; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                if (!bridge.TryAcquireSlot(out var slot))
                {
                    held++;
                    worstTicks = Math.Max(worstTicks, Stopwatch.GetTimestamp() - t0);
                    Thread.Sleep(1);
                    continue;
                }

                // Exactly 16 operations: wait, delay, pattern, 12 event records, signal.
                rig.Stream.Wait(slot.ProducerWait);
                rig.Kernels.EnqueueDelay(rig.Stream, 20_000_000);
                rig.Kernels.EnqueueTestPattern(rig.Stream, slot.ProducerView, (uint)i);
                for (var k = 0; k < 12; k++)
                {
                    scratch.Record(rig.Stream);
                }

                rig.Stream.Signal(slot.ProducerSignal);
                worstTicks = Math.Max(worstTicks, Stopwatch.GetTimestamp() - t0);
                acquired++;
                _ = rig.Presenter.TryPresent(rig.Request(slot));
            }
        });

        var worstMs = worstTicks * 1000.0 / Stopwatch.Frequency;
        Interop.WriteLine($"lead limit {bridge.MaxOutstanding}, 16 operations per write, 20 ms of GPU work each: {acquired} acquired, {held} held back; worst acquire + enqueue {worstMs:F3} ms");
        Assert.True(acquired >= maxLead, "the loop never reached the limit");
        Assert.True(worstMs < 5, $"an acquire + enqueue took {worstMs:F1} ms: the producer blocked inside CUDA");
        Assert.True(rig.Ring.WaitWritten(bridge.LastAcquiredWrite, 10_000_000_000));
    }, new RigOptions { Readback = false });

    /// <summary>Finding 2 / I4: numbering continues on a ring with history (new bridge, or a failed Configure).</summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_numbers_continue_on_a_ring_with_history(bool viaFailedConfigure) => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        for (var i = 1; i <= 25; i++)
        {
            rig.Present(rig.WritePattern((uint)i));
        }

        Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
        var ring = rig.Ring;
        if (viaFailedConfigure)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => rig.Bridge.Configure(new BridgeConfig { Width = 0, Height = H, Format = PixelFormat.Rgba8 }));
            Assert.True(rig.Bridge.IsConfigured, "a Configure refused for its arguments must leave the bridge as it was");
        }
        else
        {
            rig.Bridge.Dispose();
            rig.Bridge = new CudaVulkanBridge(rig.Cuda, rig.Presenter);
        }

        rig.Bridge.Configure(new BridgeConfig { Width = W, Height = H, Format = PixelFormat.Rgba8 });
        Assert.Same(ring, rig.Ring);
        ulong previousWritten = rig.Ring.ReadWrittenValue();
        for (var i = 1; i <= 40; i++)
        {
            var slot = rig.WritePattern((uint)(1000 + i));
            Assert.True(slot.Write > 25, $"write number {slot.Write} reused on a ring that has seen 25");
            Assert.True(rig.Ring.WaitWritten(slot.Write, 2_000_000_000));
            var written = rig.Ring.ReadWrittenValue();
            Assert.True(written >= previousWritten, $"written went down from {previousWritten} to {written}");
            previousWritten = written;
            rig.Present(slot);
            Interop.AssertPixels(Reference.Pattern(W, H, (uint)(1000 + i), PixelFormat.Rgba8), rig.ReadPixels(), W, $"frame {i} after the re-import");
        }

        Assert.Equal(0, rig.Presenter.Stats.StaleRequests);
    });

    /// <summary>
    /// Finding 4: a drain that cannot finish (the producer stalled for 3 s, longer than the drain timeout) never signals
    /// 'released' from the CUDA side while the presenter owns the ring: no validation error, 'released' never goes down,
    /// and the ring that stays imported keeps working.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_drain_that_times_out_never_signals_released_out_of_turn() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        var bridge = rig.Bridge;
        rig.Kernels.EnqueueDelay(rig.Stream, 3_000_000_000);
        for (var i = 1; i <= 800; i++)
        {
            if (bridge.TryAcquireSlot(out var slot))
            {
                rig.Stream.Signal(slot.ProducerSignal);
                _ = rig.Presenter.TryPresent(rig.Request(slot));
            }
        }

        var last = bridge.LastAcquiredWrite;
        Bounded("Configure during the stall", () => Assert.Throws<TimeoutException>(() => rig.Reconfigure(640, 360, PixelFormat.Rgba8)));
        var releasedAfterConfigure = rig.Ring.ReadReleasedValue();
        Assert.True(releasedAfterConfigure <= rig.Presenter.LastReleasedWrite, $"released {releasedAfterConfigure} is past what the presenter released ({rig.Presenter.LastReleasedWrite}): someone else signalled it");

        Assert.True(rig.Ring.WaitWritten(last, 5_000_000_000), "the producer never caught up");
        Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
        Assert.True(rig.Ring.ReadReleasedValue() >= releasedAfterConfigure);
        for (var i = 0; i < 12; i++)
        {
            var slot = rig.WritePattern((uint)(100 + i));
            rig.Present(slot);
            Interop.AssertPixels(Reference.Pattern(W, H, (uint)(100 + i), PixelFormat.Rgba8), rig.ReadPixels(), W, $"frame {i} on the ring that stayed imported");
        }

        Bounded("Configure after the stall", () => rig.Reconfigure(640, 360, PixelFormat.Rgba8));
        Assert.Equal(0, rig.Presenter.Stats.RejectedRequests);
    });

    /// <summary>Finding 6, consumer side: a stale slot never becomes a present request.</summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_stale_slot_is_refused_by_CreatePresentRequest_and_IsCurrent() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        for (var r = 0; r < 8; r++)
        {
            var stale = rig.WritePattern(1);
            rig.Present(stale);
            Assert.True(rig.Bridge.IsCurrent(stale));
            rig.Reconfigure(r % 2 == 0 ? 640 : W, r % 2 == 0 ? 360 : H, PixelFormat.Rgba8);
            var fresh = rig.WritePattern(2);
            Assert.False(rig.Bridge.IsCurrent(stale));
            Assert.True(rig.Bridge.IsCurrent(fresh));
            Assert.Throws<InvalidOperationException>(() => rig.Bridge.CreatePresentRequest(stale, 0, 0));
            rig.Present(fresh);
        }

        Assert.False(rig.Bridge.IsCurrent(default));
        Assert.Throws<InvalidOperationException>(() => rig.Bridge.CreatePresentRequest(default, 0, 0));
    });

    /// <summary>
    /// Finding 6, producer side: a stale ProducerSignal can never reach the new ring, because the old ring's semaphores
    /// stay alive (so the driver cannot give their handles to the new ring's import). 60 recreations; the review saw
    /// 2 collisions in 24.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_stale_ProducerSignal_never_reaches_the_new_ring() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        var collisions = 0;
        for (var r = 0; r < 60; r++)
        {
            BridgeSlot stale = default;
            for (var i = 1; i <= 10; i++)
            {
                stale = rig.WritePattern((uint)i);
                rig.Present(stale);
            }

            var (w, h) = r % 2 == 0 ? (640, 360) : (W, H);
            rig.Reconfigure(w, h, PixelFormat.Rgba8);
            var fresh = rig.Bridge.AcquireSlot();
            collisions += fresh.ProducerSignal.Handle == stale.ProducerSignal.Handle ? 1 : 0;
            var before = rig.Ring.ReadWrittenValue();
            try
            {
                rig.Stream.Signal(stale.ProducerSignal); // the old ring's written := 10
                rig.Stream.Synchronize();
            }
            catch (ObjectDisposedException)
            {
                // Refused outright: fine as well.
            }

            Assert.Equal(before, rig.Ring.ReadWrittenValue());
            rig.Kernels.EnqueueDelay(rig.Stream, 30_000_000);
            rig.Kernels.EnqueueTestPattern(rig.Stream, fresh.ProducerView, 77);
            rig.Stream.Signal(fresh.ProducerSignal);
            rig.Present(fresh);
            Assert.Equal(77u, Reference.DecodeCounter(rig.ReadPixels(), w, h, PixelFormat.Rgba8));
            rig.Stream.Synchronize();
        }

        Assert.Equal(0, collisions);
    });

    /// <summary>
    /// Finding 7: a merely slow producer's import is handed to the reaper (Dispose returns within its drain timeout and
    /// forces nothing) and freed once the work is done, the presenter still alive. (Since the final review the guarantee
    /// is "once the work is done and no bridge is live on the context": here the disposed bridge was the only one.)
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_slow_producers_import_is_reaped_after_its_work_and_nothing_is_forced() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        var bridge = rig.Bridge;
        ulong last = 0;
        for (var i = 1; i <= 2 * rig.Ring.SlotCount; i++)
        {
            Assert.True(bridge.TryAcquireSlot(out var slot));
            rig.Stream.Wait(slot.ProducerWait);
            rig.Kernels.EnqueueDelay(rig.Stream, 600_000_000);
            rig.Kernels.EnqueueTestPattern(rig.Stream, slot.ProducerView, (uint)i);
            rig.Stream.Signal(slot.ProducerSignal);
            _ = rig.Presenter.TryPresent(rig.Request(slot));
            last = slot.Write;
        }

        var watch = Stopwatch.StartNew();
        Bounded("Dispose with 4.8 s of work queued", bridge.Dispose);
        var disposeMs = watch.Elapsed.TotalMilliseconds;
        var written = rig.Ring.ReadWrittenValue();
        Interop.WriteLine($"Dispose took {disposeMs:F0} ms; written {written} of {last}; stream idle {rig.Stream.IsIdle}; abandoned {bridge.AbandonedImports}; pending reaps {BridgeCompat.PendingReaps}");
        Assert.True(disposeMs < 3_000, $"Dispose took {disposeMs:F0} ms");
        Assert.True(written < last && !rig.Stream.IsIdle, "written reached the last write while the producer was still working: it was forced");
        Assert.Equal(1, bridge.AbandonedImports);
        Assert.True(BridgeCompat.WaitForReaper(TimeSpan.FromSeconds(15)), "the reaper never freed the import");
        Assert.True(rig.Stream.IsIdle);
        Assert.Equal(last, rig.Ring.ReadWrittenValue());
        Assert.Equal(0, BridgeCompat.PendingReaps);
    }, new RigOptions { Readback = false });

    /// <summary>
    /// Re-review A: after a Dispose that abandons its import (a slot acquired and never signalled, as when an exception
    /// falls between acquire and signal) while the presenter still owns the ring, nothing is parked on the GPU on the
    /// import's behalf. A free, a stream creation, a kernel enqueue and an allocation on other threads complete at once
    /// (each bounded at 3 s, so a block fails the test instead of hanging it); the import is still held (its write never
    /// finished); a new bridge on the same ring shows every frame, bit-exact, while the import stays held (the reaper
    /// frees nothing while a bridge is live on the context), and is freed, the presenter alive, once that bridge is
    /// disposed; and the natural teardown order (bridge, CUDA buffers, presenter, then the CUDA
    /// context object) completes, the reaper freeing the import rather than dropping it. With the reaper's old GPU-side
    /// wait for the never-signalled write, the free blocked until the presenter was disposed, and the other three calls
    /// behind it.
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(false)]
    [InlineData(true)]
    public void An_abandoned_import_holds_up_no_other_CUDA_work_while_the_presenter_owns_the_ring(bool thenANewBridge)
    {
        CudaContext? context = null;
        try
        {
            Interop.Run(W, H, PixelFormat.Rgba8, rig =>
            {
                // A context object of the test's own (the same primary context), so the natural order can end with it.
                var ctx = CudaContext.RetainPrimary(rig.Cuda.Device);
                context = ctx;
                var options = BridgeCompat.Options(drainTimeoutNs: 300_000_000);
                var abandoned = new CudaVulkanBridge(ctx, rig.Presenter, options);
                rig.Bridge = abandoned;
                abandoned.Configure(new BridgeConfig { Width = W, Height = H, Format = PixelFormat.Rgba8 });
                for (var i = 1; i <= 3; i++)
                {
                    rig.Present(rig.WritePattern((uint)i));
                }

                Assert.True(rig.Ring.WaitWritten(3, 2_000_000_000));
                var buffer = CudaDeviceBuffer.AllocateImage(ctx, 512, 512, PixelFormat.Rgba8); // 1 MiB, freed below
                var target = CudaDeviceBuffer.AllocateImage(ctx, 64, 64, PixelFormat.Rgba8);
                var pendingBefore = BridgeCompat.PendingReaps;
                _ = abandoned.AcquireSlot(); // and never signalled
                Bounded("the abandoning Dispose", abandoned.Dispose);
                Assert.Equal(1, abandoned.AbandonedImports);
                Thread.Sleep(100); // the reaper has looked at the import many times by now

                var blocked = new List<string>();
                CudaDeviceBuffer? allocated = null;
                var freeMs = Timed("a 1 MiB cuMemFree", buffer.Dispose, blocked);
                var streamMs = Timed("a stream creation", () => CudaStream.Create(ctx).Dispose(), blocked);
                var kernelMs = Timed("a kernel enqueue and its completion", () =>
                {
                    using var stream = CudaStream.Create(ctx);
                    rig.Kernels.EnqueueTestPattern(stream, target.AsFrame(), 5);
                    stream.Synchronize();
                }, blocked);
                var allocMs = Timed("an allocation", () => allocated = CudaDeviceBuffer.AllocateImage(ctx, 512, 512, PixelFormat.Rgba8), blocked);
                Interop.WriteLine($"with the import abandoned: free {freeMs:F2} ms, stream {streamMs:F2} ms, kernel {kernelMs:F2} ms, " +
                                  $"allocation {allocMs:F2} ms; pending reaps {BridgeCompat.PendingReaps}");
                Assert.True(blocked.Count == 0, "blocked for 3 s or more: " + string.Join(", ", blocked));
                Assert.Equal(pendingBefore + 1, BridgeCompat.PendingReaps); // held, but holding nothing else up

                if (thenANewBridge)
                {
                    var fresh = new CudaVulkanBridge(ctx, rig.Presenter, options);
                    rig.Bridge = fresh;
                    fresh.Configure(new BridgeConfig { Width = W, Height = H, Format = PixelFormat.Rgba8 });
                    Assert.True(fresh.FirstWrite > abandoned.LastAcquiredWrite, $"the new bridge starts at write {fresh.FirstWrite}");
                    var shown = 0;
                    for (var i = 0; i < 20; i++)
                    {
                        var slot = rig.WritePattern((uint)(200 + i));
                        rig.Present(slot);
                        Interop.AssertPixels(Reference.Pattern(W, H, (uint)(200 + i), PixelFormat.Rgba8), rig.ReadPixels(), W, $"frame {i} of the new bridge");
                        shown++;
                    }

                    Interop.WriteLine($"a new bridge on the same ring: {shown} of 20 frames shown bit-exact, from write {fresh.FirstWrite}");

                    // Its writes carried 'written' past the abandoned write, so that import's work is done; but the
                    // reaper frees nothing while a bridge is live on the context, so it is held until this one goes.
                    Thread.Sleep(50);
                    Assert.Equal(pendingBefore + 1, BridgeCompat.PendingReaps);
                    Bounded("disposing the new bridge", fresh.Dispose);
                    Assert.Equal(0, fresh.AbandonedImports);
                    Assert.True(BridgeCompat.WaitForReaper(TimeSpan.FromSeconds(10)), "the reaper did not free the import once no bridge was live");
                }

                // The natural order: bridge (done), CUDA buffers, presenter, CUDA context object; then the reaper frees.
                var dropped = CudaVulkanBridge.DroppedReaps;
                var watch = Stopwatch.StartNew();
                Bounded("freeing the CUDA buffers", () =>
                {
                    target.Dispose();
                    allocated?.Dispose();
                });
                var buffersMs = watch.Elapsed.TotalMilliseconds;
                Bounded("disposing the presenter", rig.Presenter.Dispose);
                var presenterMs = watch.Elapsed.TotalMilliseconds - buffersMs;
                Bounded("disposing the CUDA context object", ctx.Dispose);
                Assert.True(BridgeCompat.WaitForReaper(TimeSpan.FromSeconds(10)), "the reaper never freed the import");
                Interop.WriteLine($"natural teardown: buffers {buffersMs:F1} ms, presenter {presenterMs:F0} ms, import freed after {watch.Elapsed.TotalMilliseconds:F0} ms");
                Assert.Equal(dropped, CudaVulkanBridge.DroppedReaps); // freed, not dropped
                Assert.Equal(pendingBefore, BridgeCompat.PendingReaps);
            }, new RigOptions { CreateBridge = false });
        }
        finally
        {
            context?.Dispose();
        }

    }

    /// <summary>
    /// Re-review C: an abandoned import whose CUDA context is gone (the driver reports it destroyed; injected here) is
    /// dropped by the reaper, not polled for ever. (The import is then never freed: its few CUDA objects stay in the
    /// process's context until it exits.)
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void An_abandoned_import_whose_context_is_gone_is_dropped() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        rig.Bridge.Dispose();
        rig.Bridge = new CudaVulkanBridge(rig.Cuda, rig.Presenter, BridgeCompat.Options(drainTimeoutNs: 300_000_000), new BridgeTestHooks
        {
            BeforeReapStep = static () => throw new CudaException(CudaResult.ErrorContextIsDestroyed, "cuStreamQuery"),
        });
        rig.Bridge.Configure(new BridgeConfig { Width = W, Height = H, Format = PixelFormat.Rgba8 });
        rig.Present(rig.WritePattern(1));
        _ = rig.Bridge.AcquireSlot(); // never signalled
        var dropped = CudaVulkanBridge.DroppedReaps;
        Bounded("the abandoning Dispose", rig.Bridge.Dispose);
        Assert.Equal(1, rig.Bridge.AbandonedImports);
        Assert.True(BridgeCompat.WaitForReaper(TimeSpan.FromSeconds(5)), "the reaper kept polling an import whose context is gone");
        Assert.Equal(dropped + 1, CudaVulkanBridge.DroppedReaps);
    });

    /// <summary>
    /// Re-review C: the other way a context is gone for the reaper. Its CudaContext object was disposed before the
    /// reaper's first look, so once the presenter lets go (here without ever having waited for the write: releasing
    /// through it fails) the reaper cannot create the stream it needs to release and wait from CUDA. While the presenter
    /// owns the ring it keeps polling (that needs no CUDA object); then it drops the entry instead of retrying for ever.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void An_abandoned_import_whose_context_object_was_disposed_first_is_dropped()
    {
        var context = (CudaContext?)null;
        using var firstLook = new ManualResetEventSlim();
        try
        {
            Interop.Run(W, H, PixelFormat.Rgba8, rig =>
            {
                var ctx = CudaContext.RetainPrimary(rig.Cuda.Device);
                context = ctx;
                rig.Bridge = new CudaVulkanBridge(ctx, rig.Presenter, BridgeCompat.Options(drainTimeoutNs: 300_000_000), new BridgeTestHooks
                {
                    BeforeRelease = static _ => throw new InvalidOperationException("injected: releasing through the presenter fails"),
                    BeforeReapStep = () => firstLook.Wait(TimeSpan.FromSeconds(10)),
                });
                rig.Bridge.Configure(new BridgeConfig { Width = W, Height = H, Format = PixelFormat.Rgba8 });
                rig.Present(rig.WritePattern(1));
                _ = rig.Bridge.AcquireSlot(); // never signalled
                var dropped = CudaVulkanBridge.DroppedReaps;
                var pendingBefore = BridgeCompat.PendingReaps;
                Bounded("the abandoning Dispose", rig.Bridge.Dispose);
                Assert.Equal(1, rig.Bridge.AbandonedImports);
                ctx.Dispose(); // before the reaper's first look
                firstLook.Set();
                Thread.Sleep(100);
                Assert.Equal(pendingBefore + 1, BridgeCompat.PendingReaps); // still polled while the presenter owns the ring
                Assert.Equal(dropped, CudaVulkanBridge.DroppedReaps);
                Bounded("disposing the presenter", rig.Presenter.Dispose);
                Assert.True(BridgeCompat.WaitForReaper(TimeSpan.FromSeconds(5)), "the reaper kept retrying an import it can no longer release");
                Assert.Equal(dropped + 1, CudaVulkanBridge.DroppedReaps);
            }, new RigOptions { CreateBridge = false });
        }
        finally
        {
            firstLook.Set();
            context?.Dispose();
        }
    }

    /// <summary>
    /// Final review, item 1: after a bridge abandons a never-signalled write, a new bridge on the same ring runs a
    /// single-threaded loop that keeps <paramref name="ahead"/> enqueued writes waiting to be presented. The reaper must
    /// not free the abandoned import while that bridge is live: on this driver, while a <c>cuMemFree</c> waits for queued
    /// work, enqueues on other threads block, and the loop's queued writes wait for releases only its own next present
    /// gives. With the reaper freeing as soon as <em>written</em> passed the abandoned write, N + 1 ahead deadlocked every
    /// run, N ahead (as <c>StressTests</c> runs) about one run in nine, presenting each write at once (as
    /// <c>fmp pattern</c> does) never. Each cycle is bounded (a loop stuck for 10 s is unstuck from the test thread and
    /// fails the test), checks a frame bit-exact afterwards, and the import is freed once the new bridge is disposed.
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(5, 3)] // N + 1 ahead (N = 4)
    [InlineData(4, 10)] // N ahead
    [InlineData(0, 3)] // each write presented at once
    public void A_new_bridge_running_ahead_of_its_presents_never_waits_behind_the_reaper(int ahead, int cycles) => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        const int Iterations = 150;
        var options = BridgeCompat.Options(drainTimeoutNs: 50_000_000);
        var config = new BridgeConfig { Width = W, Height = H, Format = PixelFormat.Rgba8 };
        var stuck = new List<string>();
        for (var c = 0; c < cycles; c++)
        {
            var abandoned = new CudaVulkanBridge(rig.Cuda, rig.Presenter, options);
            rig.Bridge = abandoned;
            abandoned.Configure(config);
            rig.Present(rig.WritePattern(1));
            _ = abandoned.AcquireSlot(); // never signalled
            Bounded("the abandoning Dispose", abandoned.Dispose);
            Assert.Equal(1, abandoned.AbandonedImports);

            var bridge = new CudaVulkanBridge(rig.Cuda, rig.Presenter, options);
            rig.Bridge = bridge;
            bridge.Configure(config);
            var (finished, iterations) = RunAhead(rig, bridge, ahead, Iterations, 1000);
            if (!finished)
            {
                stuck.Add($"cycle {c} stuck after {iterations} of {Iterations} iterations");
            }

            var check = rig.WritePattern((uint)(5000 + c));
            rig.Present(check);
            Interop.AssertPixels(Reference.Pattern(W, H, (uint)(5000 + c), PixelFormat.Rgba8), rig.ReadPixels(), W, $"the frame after cycle {c}");
            Bounded("disposing the new bridge", bridge.Dispose);
            Assert.Equal(0, bridge.AbandonedImports);
            Assert.True(BridgeCompat.WaitForReaper(TimeSpan.FromSeconds(5)), $"cycle {c}: the abandoned import was not freed once no bridge was live");
        }

        Interop.WriteLine($"{ahead} ahead, {cycles} cycles of {Iterations} iterations: {(stuck.Count == 0 ? "none stuck" : string.Join("; ", stuck))}");
        Assert.True(stuck.Count == 0, "the loop waited behind the reaper: " + string.Join("; ", stuck));
        Assert.Equal(0, rig.Presenter.Stats.RejectedRequests);
    }, new RigOptions { CreateBridge = false });

    /// <summary>
    /// Final review, item 2: releasing through the presenter fails and the abandoned write is never signalled, so the
    /// presenter neither waits for it nor forces it, and once the presenter is disposed nothing ever will. The reaper must
    /// park nothing on the GPU for it: a 1 MiB free and a stream creation afterwards complete at once, and the import is
    /// freed (not dropped). Before, the reaper's GPU-side wait for that write made the free spin for good.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void After_the_presenter_lets_go_a_write_that_never_comes_wedges_nothing() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        var bridge = new CudaVulkanBridge(rig.Cuda, rig.Presenter, BridgeCompat.Options(drainTimeoutNs: 300_000_000), new BridgeTestHooks
        {
            BeforeRelease = static _ => throw new InvalidOperationException("injected: releasing through the presenter fails"),
        });
        rig.Bridge = bridge;
        bridge.Configure(new BridgeConfig { Width = W, Height = H, Format = PixelFormat.Rgba8 });
        rig.Present(rig.WritePattern(1));
        Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
        var buffer = CudaDeviceBuffer.AllocateImage(rig.Cuda, 512, 512, PixelFormat.Rgba8); // 1 MiB
        _ = bridge.AcquireSlot(); // never signalled
        Bounded("the abandoning Dispose", bridge.Dispose);
        Assert.Equal(1, bridge.AbandonedImports);
        var dropped = CudaVulkanBridge.DroppedReaps;
        Bounded("disposing the presenter", rig.Presenter.Dispose);
        Thread.Sleep(100); // the reaper has seen the presenter let go
        var blocked = new List<string>();
        var freeMs = Timed("a 1 MiB cuMemFree", buffer.Dispose, blocked);
        var streamMs = Timed("a stream creation", () => CudaStream.Create(rig.Cuda).Dispose(), blocked);
        Interop.WriteLine($"after the presenter let go: free {freeMs:F2} ms, stream {streamMs:F2} ms; pending reaps {BridgeCompat.PendingReaps}");
        Assert.True(blocked.Count == 0, "blocked for 3 s or more: " + string.Join(", ", blocked));
        Assert.True(BridgeCompat.WaitForReaper(TimeSpan.FromSeconds(5)), "the reaper never freed the import");
        Assert.Equal(dropped, CudaVulkanBridge.DroppedReaps);
    }, new RigOptions { CreateBridge = false });

    /// <summary>
    /// Final review, item 3: an abandoned import whose ring a later bridge went on writing (and the presenter releasing,
    /// up to write 8) is never released from CUDA to its own last write (2) when the presenter lets go of that ring: that
    /// would signal <em>released</em> lower than the presenter did. The reaper is held at the start of its look (a stand-in
    /// for one 5 ms gap) while the later bridge writes and the presenter replaces the ring.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void The_reaper_never_releases_below_what_a_later_bridge_reached_on_the_ring() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        using var gate = new ManualResetEventSlim(true);
        var signalled = new List<ulong>();
        var options = BridgeCompat.Options(drainTimeoutNs: 300_000_000);
        var a = new CudaVulkanBridge(rig.Cuda, rig.Presenter, options, new BridgeTestHooks
        {
            BeforeRelease = static _ => throw new InvalidOperationException("injected: releasing through the presenter fails"),
            BeforeReapStep = () => gate.Wait(TimeSpan.FromSeconds(20)),
            BeforeReapRelease = v =>
            {
                lock (signalled)
                {
                    signalled.Add(v);
                }
            },
        });
        try
        {
            rig.Bridge = a;
            a.Configure(new BridgeConfig { Width = W, Height = H, Format = PixelFormat.Rgba8 });
            rig.Present(rig.WritePattern(1));
            Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
            var abandonedWrite = a.AcquireSlot().Write; // never signalled
            Bounded("the abandoning Dispose", a.Dispose);
            Assert.Equal(1, a.AbandonedImports);
            Thread.Sleep(50);
            gate.Reset(); // the reaper stops at the start of its next look
            Thread.Sleep(30);
            var b = new CudaVulkanBridge(rig.Cuda, rig.Presenter, options);
            rig.Bridge = b;
            b.Configure(new BridgeConfig { Width = W, Height = H, Format = PixelFormat.Rgba8 });
            for (var i = 0; i < 6; i++)
            {
                rig.Present(rig.WritePattern((uint)(10 + i)));
            }

            Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
            var presenterReleased = rig.Presenter.LastReleasedWrite;
            rig.Reconfigure(640, 360, PixelFormat.Rgba8); // the presenter replaces the ring: it lets go of the old one
            gate.Set();
            Thread.Sleep(200); // the reaper's look at the old ring, let go
            ulong[] seen;
            lock (signalled)
            {
                seen = [.. signalled];
            }

            Interop.WriteLine($"abandoned write {abandonedWrite}; the presenter had released {presenterReleased} of that ring; the reaper released from CUDA: [{string.Join(", ", seen)}]");
            Assert.True(seen.All(v => v >= presenterReleased), $"the reaper signalled released := {string.Join(", ", seen)} on a ring the presenter had released to {presenterReleased}");
            rig.Present(rig.WritePattern(99)); // the later bridge goes on, on the new ring
            Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
            Bounded("disposing the later bridge", b.Dispose);
            Assert.True(BridgeCompat.WaitForReaper(TimeSpan.FromSeconds(5)), "the reaper never freed the import");
        }
        finally
        {
            gate.Set();
        }
    }, new RigOptions { CreateBridge = false, Readback = false });

    /// <summary>Finding 8: writes signalled out of order (two streams) are noticed by the bridge.</summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Writes_signalled_out_of_order_are_detected() => Interop.Run(W, H, PixelFormat.Rgba8, rig =>
    {
        using var other = CudaStream.Create(rig.Cuda);
        var s1 = rig.Bridge.AcquireSlot();
        var s2 = rig.Bridge.AcquireSlot();
        rig.Kernels.EnqueueDelay(rig.Stream, 300_000_000);
        rig.Kernels.EnqueueTestPattern(rig.Stream, s1.ProducerView, 11);
        rig.Stream.Signal(s1.ProducerSignal);
        rig.Kernels.EnqueueTestPattern(other, s2.ProducerView, 22);
        other.Signal(s2.ProducerSignal);
        other.Synchronize();
        _ = rig.Bridge.Outstanding; // sees written = 2
        rig.Stream.Synchronize();
        _ = rig.Bridge.Outstanding; // sees written = 1
        Assert.True(BridgeCompat.OutOfOrderSignals(rig.Bridge) > 0, "the bridge did not notice written going backwards");
        rig.Present(rig.WritePattern(33)); // and the ring goes on once the writes are in order again
        Assert.Equal(33u, Reference.DecodeCounter(rig.ReadPixels(), W, H, PixelFormat.Rgba8));
    });

    /// <summary>
    /// The loop the review used: 240 Hz, acquire → enqueue (wait + pattern + signal) per tick, no readiness gate, no
    /// retry of a refusal. The newest write goes into a one-entry mailbox and the presenter takes it (TryPresent) on the
    /// same tick, unless it is stalled; a newer write replaces an untaken one (presenting the newest covers the older).
    /// A stall of <paramref name="stallNs"/> starts after 0.25 s: the producer's GPU work stops (one long delay at the
    /// head of its stream) or the presenter is not called. Runs 1.5 s past the stall's end.
    /// </summary>
    private static LoopStats RunLoop(InteropRig rig, long stallNs, bool producerStall)
    {
        var bridge = rig.Bridge;
        var s = new LoopStats();
        Bounded("the stall loop", () =>
        {
            var f = (double)Stopwatch.Frequency;
            var start = Stopwatch.GetTimestamp();
            var stallStart = start + (long)(0.25 * f);
            var stallEnd = stallStart + (long)(stallNs / 1e9 * f);
            var end = stallEnd + (long)(1.5 * f);
            var next = start;
            var stalled = false;
            BridgeSlot mailbox = default;
            var waiting = false;
            for (var i = 1; ; i++)
            {
                next = Pace(next, Period240);
                var now = Stopwatch.GetTimestamp();
                if (now >= end)
                {
                    break;
                }

                if (producerStall && !stalled && now >= stallStart)
                {
                    rig.Kernels.EnqueueDelay(rig.Stream, stallNs);
                    stalled = true;
                }

                var presenterStalled = !producerStall && now >= stallStart && now < stallEnd;
                var afterStall = now >= stallEnd + (long)(0.5 * f); // the queued work has had time to run
                if (bridge.TryAcquireSlot(out var slot))
                {
                    rig.Stream.Wait(slot.ProducerWait);
                    rig.Kernels.EnqueueTestPattern(rig.Stream, slot.ProducerView, (uint)i);
                    rig.Stream.Signal(slot.ProducerSignal);
                    s.Outstanding = Math.Max(s.Outstanding, bridge.Outstanding);
                    s.AcquiredAfter += afterStall ? 1 : 0;
                    mailbox = slot;
                    waiting = true;
                }
                else
                {
                    s.Held++;
                }

                if (waiting && !presenterStalled)
                {
                    waiting = false;
                    if (rig.Presenter.TryPresent(rig.Request(mailbox)))
                    {
                        s.Shown++;
                        s.ShownAfter += afterStall ? 1 : 0;
                    }
                }
            }
        });

        s.EndOutstanding = rig.Bridge.Outstanding;
        return s;
    }

    private static void AssertRecovered(InteropRig rig, LoopStats s, long stallNs)
    {
        Interop.WriteLine($"stall {stallNs / 1e9:F1} s: held back {s.Held}, shown {s.Shown}; in the last second: {s.AcquiredAfter} acquired, {s.ShownAfter} shown; " +
                          $"outstanding at most {s.Outstanding} of {rig.Bridge.MaxOutstanding}, {s.EndOutstanding} at the end; {rig.Presenter.Stats}");
        Assert.True(s.AcquiredAfter >= 150, $"only {s.AcquiredAfter} frames acquired in the second after the stall: the pipeline did not resume");
        Assert.True(s.ShownAfter >= 100, $"only {s.ShownAfter} frames shown in the second after the stall");
        Assert.True(s.Outstanding <= rig.Bridge.MaxOutstanding);
        Assert.InRange(s.EndOutstanding, 0, 2);
        Assert.Equal(0, rig.Presenter.Stats.RejectedRequests);

        // And a frame shown now is exactly the one written.
        Assert.True(rig.Ring.WaitWritten(rig.Bridge.LastAcquiredWrite, 2_000_000_000));
        var slot = rig.WritePattern(4242);
        rig.Present(slot);
        Interop.AssertPixels(Reference.Pattern(W, H, 4242, PixelFormat.Rgba8), rig.ReadPixels(), W, "a frame after the stall");
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

    /// <summary>
    /// One producer/presenter thread, as a real pipeline: acquire, enqueue (wait + pattern + signal), and present the
    /// oldest enqueued write once more than <paramref name="ahead"/> are waiting to be presented, for
    /// <paramref name="iterations"/> iterations; then the rest are presented. Bounded: if it has not finished after
    /// 10 s it is unstuck from this thread (its queued writes presented, as the reviewer did; presenter calls are
    /// serialised with the loop's, which stops) and reported unfinished.
    /// </summary>
    private static (bool Finished, long Iterations) RunAhead(InteropRig rig, CudaVulkanBridge bridge, int ahead, int iterations, uint firstFrame)
    {
        var pending = new Queue<BridgeSlot>();
        var presenting = new object(); // the presenter is single-threaded: the loop's presents and an unstick's never overlap
        var stop = false;
        long done = 0;
        ExceptionDispatchInfo? failure = null;
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < iterations && !Volatile.Read(ref stop); i++)
                {
                    // Back-pressure is not a failure here: when the GPU is shared (another test assembly presenting
                    // at the same time) earlier writes may not have finished yet. Wait for room rather than throw;
                    // a loop that never gets room is still caught by the 10 s bound below.
                    BridgeSlot slot;
                    while (!bridge.TryAcquireSlot(out slot))
                    {
                        if (Volatile.Read(ref stop))
                        {
                            break;
                        }

                        Thread.Yield();
                    }

                    if (Volatile.Read(ref stop))
                    {
                        break;
                    }

                    rig.Stream.Wait(slot.ProducerWait);
                    rig.Kernels.EnqueueTestPattern(rig.Stream, slot.ProducerView, firstFrame + (uint)i);
                    rig.Stream.Signal(slot.ProducerSignal);
                    lock (pending)
                    {
                        pending.Enqueue(slot);
                    }

                    PresentWhileMoreThan(ahead);
                    Interlocked.Increment(ref done);
                }

                PresentWhileMoreThan(0);
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }

            finished.Set();
        }) { IsBackground = true, Name = "fmp-loop-ahead" };
        thread.Start();
        var ok = finished.Wait(TimeSpan.FromSeconds(10));
        if (!ok)
        {
            Volatile.Write(ref stop, true);
            PresentWhileMoreThan(0);
            Assert.True(finished.Wait(TimeSpan.FromSeconds(15)), "the loop stayed stuck even after its queued writes were presented from another thread");
        }

        failure?.Throw();
        return (ok, Interlocked.Read(ref done));

        void PresentWhileMoreThan(int count)
        {
            lock (presenting)
            {
                while (true)
                {
                    BridgeSlot oldest;
                    lock (pending)
                    {
                        if (pending.Count <= count)
                        {
                            return;
                        }

                        oldest = pending.Dequeue();
                    }

                    rig.Present(oldest);
                }
            }
        }
    }

    /// <summary>Waits (spinning) until <paramref name="previous"/> + <paramref name="periodNs"/>; returns that deadline.</summary>
    private static long Pace(long previous, long periodNs)
    {
        var deadline = previous + (periodNs * Stopwatch.Frequency / 1_000_000_000);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Thread.SpinWait(32);
        }

        return deadline;
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

    private sealed class LoopStats
    {
        public long Held { get; set; }

        public long Shown { get; set; }

        public long ShownAfter { get; set; }

        public long AcquiredAfter { get; set; }

        public long Outstanding { get; set; }

        public long EndOutstanding { get; set; }
    }
}
