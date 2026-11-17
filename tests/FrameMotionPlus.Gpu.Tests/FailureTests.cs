using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Gpu.Tests.Support;
using FrameMotionPlus.Vulkan;
using Microsoft.Win32.SafeHandles;
using Reference = FrameMotionPlus.Cuda.Tests.Support.Reference;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>
/// Device mismatch, import failures at every step (injected and real), misuse, and teardown in awkward states. FD
/// ownership is checked two ways: the process's descriptor count must not change, and canary descriptors opened at the
/// moment of failure, which take the numbers CUDA freed when it closed the descriptors it had imported, must still be
/// open afterwards (a second close of a transferred descriptor would close a canary).
/// </summary>
public class FailureTests
{
    private const int Width = 320;
    private const int Height = 180;

    private sealed class InjectedFault : Exception
    {
        public InjectedFault()
            : base("injected import failure")
        {
        }
    }

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Cuda_and_vulkan_report_the_same_device_uuid() => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        var cudaDevice = rig.Cuda.Device;
        var uuid = GpuIdentity.UuidOf(cudaDevice);
        Interop.WriteLine($"CUDA {cudaDevice.Name}: {uuid} (Guid form {cudaDevice.Uuid}); Vulkan {rig.Device.PhysicalDevice}");
        Assert.Equal(rig.Device.PhysicalDevice.Uuid, uuid);
        Assert.Equal(uuid, rig.Ring.DeviceUuid);
        Assert.Equal(uuid, rig.Bridge.DeviceUuid);
        Assert.Equal(cudaDevice.Uuid, new Guid(uuid.ToArray(), bigEndian: true));
        Assert.Equal(cudaDevice.Uuid.ToString(), uuid.ToString());
        Assert.True(CudaDriver.TryFindDevice(new Guid(rig.Device.PhysicalDevice.Uuid.ToArray(), bigEndian: true), out var found));
        Assert.Equal(cudaDevice.Ordinal, found.Ordinal);
        GpuIdentity.EnsureSameDevice(uuid, rig.Device.PhysicalDevice.Uuid, "test");
        Assert.Throws<GpuDeviceMismatchException>(() => GpuIdentity.EnsureSameDevice(default, default, "empty UUIDs"));
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_ring_on_another_gpu_is_refused_before_anything_is_imported() => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        // One GPU here, so the CUDA side's UUID is replaced with another GPU's; everything else is real.
        var otherGpu = DeviceUuid.Parse("11111111-2222-3333-4444-555555555555");
        var hooks = new BridgeTestHooks { CudaUuid = otherGpu };

        // The presenter already has a ring: refused at construction.
        var early = Assert.Throws<GpuDeviceMismatchException>(() => new CudaVulkanBridge(rig.Cuda, rig.Presenter, null, hooks));
        Assert.Equal(otherGpu, early.CudaUuid);
        Assert.Equal(rig.Ring.DeviceUuid, early.VulkanUuid);
        Interop.WriteLine(early.Message);

        // A bridge created before the presenter had a ring: refused when the ring is about to be imported, with no
        // descriptor left behind (the ring itself, created first, holds driver descriptors of its own).
        using var target = new OffscreenTarget(rig.Device, new OffscreenTargetOptions { Width = Width, Height = Height });
        using var presenter = new VulkanPresenter(rig.Device, target);
        using var bridge = new CudaVulkanBridge(rig.Cuda, presenter, null, hooks);
        presenter.Configure(new PresenterConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 });
        var fds = RecreationTests.OpenDescriptors();
        var late = Assert.Throws<GpuDeviceMismatchException>(() => bridge.Configure(new BridgeConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 }));
        Assert.Contains(otherGpu.ToString(), late.Message, StringComparison.Ordinal);
        Assert.Contains(rig.Ring.DeviceUuid.ToString(), late.Message, StringComparison.Ordinal);
        Assert.False(bridge.IsConfigured);
        Assert.Equal(0, bridge.LiveImportObjects);
        Assert.Equal(fds, RecreationTests.OpenDescriptors());
        Assert.Throws<InvalidOperationException>(() => bridge.AcquireSlot());
    }, new RigOptions());

    public static TheoryData<int, int> Steps => new()
    {
        { (int)ImportStep.SlotMemory, 0 },
        { (int)ImportStep.SlotMemory, 2 },
        { (int)ImportStep.SlotMapping, 0 },
        { (int)ImportStep.SlotMapping, 3 },
        { (int)ImportStep.WrittenSemaphore, -1 },
        { (int)ImportStep.ReleasedSemaphore, -1 },
    };

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [MemberData(nameof(Steps))]
    public void A_failure_part_way_through_the_import_leaks_nothing_and_closes_nothing_twice(int step, int index) =>
        WithHookedBridge((ImportStep)step, index, fault: true);

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData((int)ImportStep.SlotMemory, 0)]
    [InlineData((int)ImportStep.SlotMemory, 3)]
    [InlineData((int)ImportStep.WrittenSemaphore, -1)]
    [InlineData((int)ImportStep.ReleasedSemaphore, -1)]
    public void A_descriptor_the_driver_rejects_stays_with_its_owner(int step, int index) =>
        WithHookedBridge((ImportStep)step, index, fault: false);

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Misuse_is_refused_with_a_clear_message() => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        using var target = new OffscreenTarget(rig.Device, new OffscreenTargetOptions { Width = Width, Height = Height });
        using var presenter = new VulkanPresenter(rig.Device, target);
        using var bridge = new CudaVulkanBridge(rig.Cuda, presenter);
        Assert.Equal(MemoryDomain.CudaDevice, bridge.ProducerDomain);
        Assert.Equal(MemoryDomain.Vulkan, bridge.ConsumerDomain);
        Assert.Equal(0, bridge.SlotCount);
        Assert.Contains("Configure", Assert.Throws<InvalidOperationException>(() => bridge.AcquireSlot()).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => bridge.Configure(new BridgeConfig { Width = Width, Height = Height, Format = PixelFormat.Bgra8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => bridge.Configure(new BridgeConfig { Width = 0, Height = Height, Format = PixelFormat.Rgba8 }));
        var slots = Assert.Throws<ArgumentException>(() => bridge.Configure(new BridgeConfig { SlotCount = 6, Width = Width, Height = Height, Format = PixelFormat.Rgba8 }));
        Assert.Contains("4 slots", slots.Message, StringComparison.Ordinal);
        Assert.False(bridge.IsConfigured);

        bridge.Configure(new BridgeConfig { SlotCount = 4, Width = Width, Height = Height, Format = PixelFormat.Rgba8 });
        Assert.Equal(4, bridge.SlotCount);
        var ring = bridge.Ring;
        bridge.Configure(new BridgeConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 });
        Assert.Same(ring, bridge.Ring); // the same shape again changes nothing

        // Reconfiguring the presenter behind the bridge's back is caught on the next acquire, and Configure recovers. The
        // old ring's writes are released from CUDA and its import goes to the reaper (bridge backlog item 2: with the
        // presenter gone from that ring, only a GPU-side wait could tell when they finish).
        var slot = bridge.AcquireSlot();
        rig.Stream.Signal(slot.ProducerSignal);
        presenter.Configure(new PresenterConfig { Width = Width * 2, Height = Height, Format = PixelFormat.Rgba8 });
        Assert.Contains("CudaVulkanBridge.Configure", Assert.Throws<InvalidOperationException>(() => bridge.AcquireSlot()).Message, StringComparison.Ordinal);
        bridge.Configure(new BridgeConfig { Width = Width * 2, Height = Height, Format = PixelFormat.Rgba8 });
        Assert.Equal(1, bridge.AbandonedImports);
        Assert.Same(presenter.Ring, bridge.Ring);
        var first = bridge.AcquireSlot();
        Assert.Equal(1UL, first.ProducerSignal.Value); // a fresh ring starts at write 1
        rig.Stream.Signal(first.ProducerSignal);

        bridge.Dispose();
        Assert.Equal(1, bridge.AbandonedImports); // the old ring's, from the Configure above; this ring's drained

        // The old import is held while the rig's own bridge is live on the context, and freed when that one goes too.
        rig.Bridge.Dispose();
        Assert.True(CudaVulkanBridge.WaitForReaper(TimeSpan.FromSeconds(5)), "the old ring's import was not freed once no bridge was live");
        Assert.Throws<ObjectDisposedException>(() => bridge.AcquireSlot());
        Assert.Throws<ObjectDisposedException>(() => bridge.Configure(new BridgeConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 }));
        bridge.Dispose();
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void An_acquired_write_that_is_never_signalled_times_out_clearly_and_still_tears_down() => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        rig.Present(rig.WritePattern(5));
        _ = rig.Bridge.AcquireSlot(); // and never signalled
        var start = Stopwatch.GetTimestamp();
        var e = Assert.Throws<TimeoutException>(() => rig.Reconfigure(Width * 2, Height, PixelFormat.Rgba8));
        Interop.WriteLine($"{Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms: {e.Message}");
        Assert.Contains("ProducerSignal", e.Message, StringComparison.Ordinal);
        Assert.True(rig.Bridge.IsConfigured, "a failed reconfigure must leave the current ring usable or disposable");

        // Dispose cannot tell a write that will never be signalled from a slow one: it returns within its timeout and
        // hands the import to the reaper. The presenter's own Dispose then forces the writes it waits for (its last
        // resort), and the reaper frees the import.
        start = Stopwatch.GetTimestamp();
        rig.Bridge.Dispose();
        Assert.True(Stopwatch.GetElapsedTime(start).TotalSeconds < 3, "Dispose hung on the unsignalled write");
        Assert.Equal(0, rig.Bridge.LiveImportObjects);
        Assert.Equal(1, rig.Bridge.AbandonedImports);
        rig.Presenter.Dispose();
        Assert.True(CudaVulkanBridge.WaitForReaper(TimeSpan.FromSeconds(10)), "the reaper never freed the import");
    }, new RigOptions { Bridge = new CudaVulkanBridgeOptions { DrainTimeoutNs = 300_000_000 } });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Dispose_waits_for_writes_still_running_on_the_gpu() => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        var slot = rig.Bridge.AcquireSlot();
        rig.Stream.Wait(slot.ProducerWait);
        rig.Kernels.EnqueueDelay(rig.Stream, 300_000_000);
        rig.Kernels.EnqueueTestPattern(rig.Stream, slot.ProducerView, 3);
        rig.Stream.Signal(slot.ProducerSignal);
        rig.Present(slot);
        var ring = rig.Ring;
        Assert.Equal(0UL, ring.ReadWrittenValue());

        var start = Stopwatch.GetTimestamp();
        rig.Bridge.Dispose();
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Interop.WriteLine($"Dispose took {ms:F0} ms");
        Assert.True(rig.Stream.IsIdle, "the imports were destroyed with CUDA work still queued against them");
        Assert.Equal(1UL, ring.ReadWrittenValue());
        Assert.True(ms > 150);
        Interop.AssertPixels(Reference.Pattern(Width, Height, 3, PixelFormat.Rgba8), rig.ReadPixels(), Width, "the write that was in flight");
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_bridge_disposed_after_its_presenter_still_drains_and_does_not_hang() => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        // Wrong order on purpose. Writes 1..N are never presented and write N + 1 waits for the release of write 1,
        // which the presenter, disposed first, will never give. The bridge must let that write finish itself.
        var n = rig.Ring.SlotCount;
        for (var i = 0; i <= n; i++)
        {
            rig.WritePattern((uint)i);
        }

        rig.Presenter.Dispose();
        var start = Stopwatch.GetTimestamp();
        rig.Bridge.Dispose();
        Assert.True(Stopwatch.GetElapsedTime(start).TotalSeconds < 3, "Dispose hung");

        // The bridge released every write from CUDA and handed the import to the reaper without waiting GPU-side (bridge
        // backlog item 2), so the last write finishes on its own, shortly after.
        var deadline = Stopwatch.GetTimestamp() + (2 * Stopwatch.Frequency);
        while (!rig.Stream.IsIdle && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(1);
        }

        Assert.True(rig.Stream.IsIdle, "the producer's last write never finished");
        Assert.Equal(0, rig.Bridge.LiveImportObjects);
        Assert.Equal(1, rig.Bridge.AbandonedImports);
        Assert.True(CudaVulkanBridge.WaitForReaper(TimeSpan.FromSeconds(5)), "the reaper never freed the import");
    });

    /// <summary>
    /// Configure with a hook at (<paramref name="step"/>, <paramref name="index"/>): an injected exception
    /// (<paramref name="fault"/>), or a /dev/null descriptor in place of the exported one so the driver itself rejects
    /// it. Then checks descriptors and objects, and that a clean Configure afterwards works.
    /// </summary>
    private static void WithHookedBridge(ImportStep step, int index, bool fault) => Interop.Run(Width, Height, PixelFormat.Rgba8, rig =>
    {
        var armed = true;
        var canaries = new List<SafeFileHandle>();
        using var devNull = File.OpenHandle("/dev/null");
        var devNullFd = (int)devNull.DangerousGetHandle();
        var hooks = new BridgeTestHooks
        {
            BeforeImport = (s, i) =>
            {
                if (armed && fault && s == step && i == index)
                {
                    // Take every descriptor number CUDA freed by closing what it imported so far.
                    for (var k = 0; k < 8; k++)
                    {
                        canaries.Add(File.OpenHandle("/dev/null"));
                    }

                    throw new InjectedFault();
                }
            },
            FdOverride = (s, i, fd) => armed && !fault && s == step && i == index ? devNullFd : fd,
        };

        using var target = new OffscreenTarget(rig.Device, new OffscreenTargetOptions { Width = Width, Height = Height });
        using var presenter = new VulkanPresenter(rig.Device, target);
        presenter.Configure(new PresenterConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 });
        using var bridge = new CudaVulkanBridge(rig.Cuda, presenter, null, hooks);
        var before = RecreationTests.OpenDescriptors();
        var config = new BridgeConfig { Width = Width, Height = Height, Format = PixelFormat.Rgba8 };
        try
        {
            if (fault)
            {
                Assert.Throws<InjectedFault>(() => bridge.Configure(config));
            }
            else
            {
                var e = Assert.Throws<CudaException>(() => bridge.Configure(config));
                Interop.WriteLine($"driver: {e.Message}");
            }

            Assert.False(bridge.IsConfigured);
            Assert.Equal(0, bridge.LiveImportObjects);
            foreach (var canary in canaries)
            {
                var fd = (int)canary.DangerousGetHandle();
                Assert.True(new FileInfo($"/proc/self/fd/{fd}").LinkTarget == "/dev/null", $"canary fd {fd} was closed behind its owner's back");
            }

            Assert.Equal("/dev/null", new FileInfo($"/proc/self/fd/{devNullFd}").LinkTarget);
            Assert.Equal(before + canaries.Count, RecreationTests.OpenDescriptors());
        }
        finally
        {
            foreach (var canary in canaries)
            {
                canary.Dispose();
            }
        }

        // Nothing is left half-imported: a clean attempt imports the same ring and works.
        armed = false;
        bridge.Configure(config);
        Assert.Equal((2 * bridge.SlotCount) + 2, bridge.LiveImportObjects);
        var slot = bridge.AcquireSlot();
        rig.Stream.Wait(slot.ProducerWait);
        rig.Kernels.EnqueueTestPattern(rig.Stream, slot.ProducerView, 77);
        rig.Stream.Signal(slot.ProducerSignal);
        Assert.True(presenter.TryPresent(bridge.CreatePresentRequest(slot, 0, 0)));
        var bytes = new byte[target.ReadbackSizeBytes];
        Assert.NotEqual(0UL, target.ReadLatest(bytes));
        Interop.AssertPixels(Reference.Pattern(Width, Height, 77, PixelFormat.Rgba8), System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes).ToArray(), Width, "after recovery");
        bridge.Dispose();
        Assert.Equal(before, RecreationTests.OpenDescriptors());
    });
}
