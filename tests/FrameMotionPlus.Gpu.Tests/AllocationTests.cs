using System.Runtime;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Gpu.Tests.Support;
using FrameMotionPlus.Vulkan;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>
/// Hot-path rule: the whole per-frame path, produce (a CUDA pattern into a ring slot, or a generator writing one) →
/// signal → present, allocates nothing. Measured with the thread's allocation counter, exact here because this project
/// runs non-concurrent GC and its tests one at a time. Every path the loop is meant to cover is counted inside the
/// measured windows and asserted, so a loop that skips work (and so trivially allocates nothing) fails.
/// </summary>
public class AllocationTests
{
    private const int Windows = 3;
    private const int FramesPerWindow = 240;

    /// <summary>The csproj's TieredPGO=false reaches the test host (see the comment there).</summary>
    [Fact]
    public void Test_host_runs_without_dynamic_pgo() =>
        Assert.True(AppContext.TryGetSwitch("System.Runtime.TieredPGO", out var pgo) && !pgo, "System.Runtime.TieredPGO is not false in this host");

    [Fact]
    public void Test_host_runs_without_background_gc() => Assert.Equal(GCLatencyMode.Batch, GCSettings.LatencyMode);

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(PixelFormat.Rgba8)]
    [InlineData(PixelFormat.Rgb10A2)]
    public void Produce_signal_present_allocates_nothing(PixelFormat format) => Interop.Run(1280, 720, format, rig =>
    {
        var cuda = rig.Cuda;
        var stream = rig.Stream;
        var bridge = rig.Bridge;
        var presenter = rig.Presenter;
        var kernels = rig.Kernels;

        // Two source frames for the generator, with their events as Ready; ring writes all on one stream.
        using var a = CudaDeviceBuffer.AllocateImage(cuda, 1280, 720, format);
        using var b = CudaDeviceBuffer.AllocateImage(cuda, 1280, 720, format);
        using var aDrawn = CudaEvent.Create(cuda);
        using var bDrawn = CudaEvent.Create(cuda);
        kernels.EnqueueTestPattern(stream, a.AsFrame(), 1);
        aDrawn.Record(stream);
        kernels.EnqueueTestPattern(stream, b.AsFrame(), 2);
        bDrawn.Record(stream);
        var prev = a.AsFrame(contentTimeNs: 1, ready: aDrawn.Signal);
        var curr = b.AsFrame(contentTimeNs: 2, ready: bDrawn.Signal);
        using var blend = new BlendGenerator(cuda, kernels, stream);
        IFrameGenerator generator = blend;
        generator.Configure(new FrameGenConfig { Width = 1280, Height = 720, Format = format });
        using var timer = CudaSpanTimerRing.Create(cuda, 16);

        // The generator's GPU-side semaphore wait (output.Ready = the slot's release) and signal (done = the slot's
        // written point) are counted around each Enqueue: the CUDA tests cannot cover these branches (no timeline
        // semaphore there), so this is where they are proven to run allocation-free.
        long generatorWaits = 0, generatorSignals = 0, acquired = 0, maxOutstanding = 0;
        long presented = 0, released = 0, generated = 0, patterns = 0, gpuTimes = 0, feedback = 0, copyTimes = 0;
        long outputSlot = 0;
        void Window()
        {
            for (var i = 0; i < FramesPerWindow; i++)
            {
                // The producer's acquire path with back-pressure: the refusable acquire and the lead read.
                if (!bridge.TryAcquireSlot(out var slot))
                {
                    continue;
                }

                acquired++;
                maxOutstanding = Math.Max(maxOutstanding, bridge.Outstanding);
                released += slot.ProducerWait.IsNone ? 0 : 1;
                if ((i & 1) == 0)
                {
                    stream.Wait(slot.ProducerWait);
                    timer.Begin(stream);
                    kernels.EnqueueTestPattern(stream, slot.ProducerView, (uint)i);
                    timer.End(stream);
                    stream.Signal(slot.ProducerSignal);
                    patterns++;
                }
                else
                {
                    var waits = blend.Stream.SemaphoreWaits;
                    var signals = blend.Stream.SemaphoreSignals;
                    generator.Enqueue(prev, curr, 0.5f, slot.ProducerView, slot.ProducerSignal);
                    generatorWaits += blend.Stream.SemaphoreWaits - waits;
                    generatorSignals += blend.Stream.SemaphoreSignals - signals;
                    generated++;
                }

                rig.WaitReady();
                if (presenter.TryPresent(bridge.CreatePresentRequest(slot, outputSlot++, 0)))
                {
                    presented++;
                }

                while (presenter.TryGetFeedback(out _))
                {
                    feedback++;
                }

                while (timer.TryRead(out _))
                {
                    gpuTimes++;
                }

                while (blend.TryReadGpuTime(out _))
                {
                    gpuTimes++;
                }

                copyTimes += presenter.Stats.LastCopyGpuNs > 0 ? 1 : 0;
            }

            StallPhase();
        }

        // Back-pressure: with the presenter stalled the writes past the first lap wait for releases, the bridge reads
        // 'written' and refuses at its limit; one present catches everything up and acquiring resumes.
        var n = rig.Ring.SlotCount;
        var limit = (int)bridge.MaxOutstanding;
        long stallAcquired = 0, stallRefused = 0, stallSpins = 0, stallPresented = 0;
        void StallPhase()
        {
            BridgeSlot newest = default;
            for (var k = 0; k < n + limit + 2; k++)
            {
                if (bridge.TryAcquireSlot(out var s))
                {
                    stream.Wait(s.ProducerWait);
                    kernels.EnqueueTestPattern(stream, s.ProducerView, 7);
                    stream.Signal(s.ProducerSignal);
                    newest = s;
                    stallAcquired++;
                }
                else
                {
                    stallRefused++;
                }

                if (k == n - 1)
                {
                    stallSpins += SpinUntilFinished(bridge); // the first lap finishes; the rest waits for releases
                }
            }

            rig.WaitReady();
            if (presenter.TryPresent(bridge.CreatePresentRequest(newest, outputSlot++, 0)))
            {
                stallPresented++;
            }

            stallSpins += SpinUntilFinished(bridge);
        }

        // Every path of the measured windows runs in the warm-up window first (the stall phase's refusals and catch-up
        // included), so no first use (under the JIT: binding a P/Invoke, which allocates its library name once) can land
        // in a measured window. The one branch a warm-up window need not reach is rig.WaitReady's not-ready spin: its
        // calls, once, here.
        _ = System.Diagnostics.Stopwatch.GetElapsedTime(System.Diagnostics.Stopwatch.GetTimestamp()).TotalMilliseconds;
        Thread.SpinWait(64);
        Window(); // warm-up: JIT, first-use paths
        presented = released = generated = patterns = gpuTimes = feedback = copyTimes = generatorWaits = generatorSignals = acquired = maxOutstanding = 0;
        stallAcquired = stallRefused = stallPresented = 0;
        var streamWaits = stream.SemaphoreWaits;
        var streamSignals = stream.SemaphoreSignals;
        var collections = GC.CollectionCount(0);
        long allocated = 0;
        for (var w = 0; w < Windows; w++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Window();
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        collections = GC.CollectionCount(0) - collections;
        streamWaits = stream.SemaphoreWaits - streamWaits;
        streamSignals = stream.SemaphoreSignals - streamSignals;
        Assert.True(presenter.WaitIdle(2_000_000_000));
        Interop.WriteLine($"stall phases: {stallAcquired} acquired, {stallRefused} refused at the limit, {stallPresented} catch-up presents; " +
                          $"{presented} presented ({patterns} patterns, {generated} generated, {released} waited on a release; " +
                          $"semaphore waits/signals: generator {generatorWaits}/{generatorSignals}, stream {streamWaits}/{streamSignals}), " +
                          $"{gpuTimes} GPU spans read, {feedback} feedback records; {allocated} bytes allocated, {collections} collections; {presenter.Stats}");

        const int Measured = Windows * FramesPerWindow;
        Assert.Equal(Measured, acquired); // every frame went through TryAcquireSlot and got a slot
        Assert.InRange(maxOutstanding, 1, bridge.MaxOutstanding);
        Assert.Equal(Measured, presented);
        Assert.Equal(Measured / 2, patterns);
        Assert.Equal(Measured / 2, generated);
        Assert.Equal(Measured, released); // every measured write lapped the ring and waited on 'released'
        Assert.Equal(Measured / 2, generatorWaits); // Enqueue's wait on output.Ready, a CUDA timeline semaphore
        Assert.Equal(Measured / 2, generatorSignals); // Enqueue's signal of done, the same
        Assert.Equal(Windows * (n + limit), stallAcquired);
        Assert.Equal(Windows * 2, stallRefused); // the refusal path ran in every measured window
        Assert.Equal(Windows, stallPresented);
        Assert.Equal(Measured + stallAcquired, streamWaits); // the stall phase's writes wait and signal on the same stream
        Assert.Equal(Measured + stallAcquired, streamSignals);
        Assert.True(gpuTimes > Measured / 2, $"only {gpuTimes} GPU spans were read back");
        Assert.True(feedback > Measured / 2, $"only {feedback} feedback records");
        Assert.True(copyTimes > 0);
        Assert.Equal(0, allocated);
        Assert.Equal(0, collections);
    }, new RigOptions { TargetWidth = 1920, TargetHeight = 1080, Readback = false });

    /// <summary>Spins (allocation-free, bounded) until every write handed out has finished; returns the spins.</summary>
    private static long SpinUntilFinished(CudaVulkanBridge bridge)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        long spins = 0;
        while (bridge.Outstanding > 0)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds > 5)
            {
                throw new TimeoutException("the writes never finished");
            }

            Thread.SpinWait(64);
            spins++;
        }

        return spins;
    }
}
