using FrameMotionPlus.Core.Frames;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace FrameMotionPlus.Vulkan.Diagnostics;

/// <summary>
/// TEST AND DIAGNOSTIC PRODUCER. Fills presentation-ring slots from the CPU without CUDA, so the presenter can be
/// exercised and measured standalone, and so the join step has a working reference for the producer side.
/// <para>
/// It behaves like an external API: it creates its <em>own</em> logical device on the ring's physical device and
/// imports the ring only through a <see cref="PresentationRingExport"/> (memory FDs, the two timeline semaphore FDs,
/// sizes, pitch), exactly as the CUDA bridge will. Each write n follows <see cref="RingProtocol"/>: the GPU waits until
/// <em>released</em> ≥ n − N, copies a host-visible staging buffer into slot (n − 1) mod N (taking the buffer over from
/// and handing it back to the external queue family), then signals <em>written</em> = n.
/// </para>
/// <para>
/// Unlike the real pipeline it blocks the CPU before reusing a staging buffer (until the copy that last used it is
/// done). Not for production use. Single-threaded.
/// </para>
/// </summary>
public sealed unsafe class DiagnosticRingProducer : IDisposable
{
    private const long WaitTimeoutNs = 5_000_000_000;
    private const VkBufferUsageFlags RingUsage =
        VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.StorageBuffer;

    private readonly VulkanDevice _device;
    private readonly VkDeviceApi _api;
    private readonly VkBuffer[] _slots;
    private readonly VkDeviceMemory[] _slotMemory;
    private readonly VkBuffer[] _staging;
    private readonly VkDeviceMemory[] _stagingMemory;
    private readonly byte*[] _stagingMapped;
    private readonly VkCommandBuffer[] _cmd;
    private VkSemaphore _written;
    private VkSemaphore _released;
    private VkCommandPool _pool;
    private ulong _next = RingProtocol.FirstWrite;
    private bool _disposed;

    private DiagnosticRingProducer(VulkanDevice device, PresentationRingExport export)
    {
        _device = device;
        _api = device.Api;
        SlotCount = export.SlotCount;
        Width = export.Width;
        Height = export.Height;
        PitchBytes = export.PitchBytes;
        Format = export.Format;
        ContentSizeBytes = export.ContentSizeBytes;
        _slots = new VkBuffer[SlotCount];
        _slotMemory = new VkDeviceMemory[SlotCount];
        _staging = new VkBuffer[SlotCount];
        _stagingMemory = new VkDeviceMemory[SlotCount];
        _stagingMapped = new byte*[SlotCount];
        _cmd = new VkCommandBuffer[SlotCount];
    }

    public int SlotCount { get; }

    public int Width { get; }

    public int Height { get; }

    public int PitchBytes { get; }

    public PixelFormat Format { get; }

    public long ContentSizeBytes { get; }

    /// <summary>The number the next write will get.</summary>
    public ulong NextWrite => _next;

    /// <summary>The producer's own logical device (separate from the presenter's).</summary>
    public VulkanDevice Device => _device;

    /// <summary>
    /// Imports the ring described by <paramref name="export"/> into a new logical device on the same physical device.
    /// Each FD whose import succeeds is marked transferred (the producer's device now owns it).
    /// </summary>
    public static DiagnosticRingProducer Create(VulkanInstance instance, PresentationRingExport export)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(export);
        var device = VulkanDevice.Create(instance, DeviceSelector.ByUuid(export.DeviceUuid));
        var producer = new DiagnosticRingProducer(device, export);
        try
        {
            producer.Import(export);
            producer.CreateStaging();
            return producer;
        }
        catch
        {
            producer.Dispose();
            throw;
        }
    }

    /// <summary>Writes a <see cref="TestPattern"/> frame; returns its write number n. Allocation-free.</summary>
    public ulong WritePattern(TestPattern pattern, int frame)
    {
        var n = _next;
        var k = BeginWrite(n);
        TestPatterns.Fill(new Span<byte>(_stagingMapped[k], (int)ContentSizeBytes), pattern, Format, Width, Height, PitchBytes, frame);
        return Submit(n, k);
    }

    /// <summary>Writes caller-supplied pixels in ring layout (<see cref="PitchBytes"/> per row); returns n.</summary>
    public ulong WritePixels(ReadOnlySpan<byte> pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels.Length, (int)ContentSizeBytes, nameof(pixels));
        var n = _next;
        var k = BeginWrite(n);
        pixels[..(int)ContentSizeBytes].CopyTo(new Span<byte>(_stagingMapped[k], (int)ContentSizeBytes));
        return Submit(n, k);
    }

    /// <summary>Writes whatever the slot's staging buffer last held (no CPU fill): GPU copy plus signals only. Returns n.</summary>
    public ulong WriteStaged()
    {
        var n = _next;
        var k = BeginWrite(n);
        return Submit(n, k);
    }

    /// <summary>
    /// Fills every staging buffer with <paramref name="pattern"/> (for <see cref="WriteStaged"/>): the next write gets
    /// frame <paramref name="frame"/>, the one after it frame + <paramref name="frameStep"/>, and so on round the ring,
    /// so with a step of 1 staged writes show a moving pattern without any per-frame CPU work.
    /// </summary>
    public void FillAllStaging(TestPattern pattern, int frame, int frameStep = 0)
    {
        for (var k = 0; k < SlotCount; k++)
        {
            // Write number _next + k uses staging k' = SlotOf(_next + k); make sure none is still being copied from.
            var n = _next + (ulong)k;
            var slot = RingProtocol.SlotOf(n, SlotCount);
            WaitStagingFree(n);
            TestPatterns.Fill(new Span<byte>(_stagingMapped[slot], (int)ContentSizeBytes), pattern, Format, Width, Height, PitchBytes, frame + (k * frameStep));
        }
    }

    /// <summary><em>written</em> as this producer's device sees it.</summary>
    public ulong ReadWrittenValue() => Read(_written);

    /// <summary><em>released</em> as this producer's device sees it (the presenter's signals, across devices).</summary>
    public ulong ReadReleasedValue() => Read(_released);

    public bool WaitWritten(ulong value, long timeoutNs) => Wait(_written, value, timeoutNs);

    public bool WaitReleased(ulong value, long timeoutNs) => Wait(_released, value, timeoutNs);

    /// <summary>
    /// Waits for this producer's writes, then destroys its device. If a write is still waiting for a release that
    /// will never come (the presenter is gone), the producer signals <em>released</em> itself to let it finish.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            var last = _next - 1;
            if (last > 0 && !_written.IsNull && !_released.IsNull && !Wait(_written, last, WaitTimeoutNs))
            {
                var info = new VkSemaphoreSignalInfo { semaphore = _released, value = Math.Max(Read(_released) + 1, last) };
                _ = _api.vkSignalSemaphore(&info);
                _ = Wait(_written, last, WaitTimeoutNs);
            }
        }
        finally
        {
            Destroy();
        }
    }

    private int BeginWrite(ulong n)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var k = RingProtocol.SlotOf(n, SlotCount);
        WaitStagingFree(n);
        return k;
    }

    private void WaitStagingFree(ulong n)
    {
        // Staging k and command buffer k were last used by write n − N; that copy is done once written ≥ n − N.
        var previous = RingProtocol.ReleasedValueBeforeWrite(n, SlotCount);
        if (previous > 0 && !Wait(_written, previous, WaitTimeoutNs))
        {
            throw new TimeoutException($"Write {previous} never completed; is the presenter releasing slots?");
        }
    }

    private ulong Submit(ulong n, int k)
    {
        var cmd = _cmd[k];
        VulkanException.Check(_api.vkResetCommandBuffer(cmd, VkCommandBufferResetFlags.None), "vkResetCommandBuffer");
        var begin = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        VulkanException.Check(_api.vkBeginCommandBuffer(cmd, &begin), "vkBeginCommandBuffer");
        var barrier = new VkBufferMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.None,
            srcAccessMask = VkAccessFlags2.None,
            dstStageMask = VkPipelineStageFlags2.Copy,
            dstAccessMask = VkAccessFlags2.TransferWrite,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_EXTERNAL,
            dstQueueFamilyIndex = _device.QueueFamily,
            buffer = _slots[k],
            offset = 0,
            size = VK_WHOLE_SIZE,
        };
        var dep = new VkDependencyInfo { bufferMemoryBarrierCount = 1, pBufferMemoryBarriers = &barrier };
        _api.vkCmdPipelineBarrier2(cmd, &dep);
        var region = new VkBufferCopy { srcOffset = 0, dstOffset = 0, size = (ulong)ContentSizeBytes };
        _api.vkCmdCopyBuffer(cmd, _staging[k], _slots[k], 1, &region);
        barrier = new VkBufferMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.Copy,
            srcAccessMask = VkAccessFlags2.TransferWrite,
            dstStageMask = VkPipelineStageFlags2.None,
            dstAccessMask = VkAccessFlags2.None,
            srcQueueFamilyIndex = _device.QueueFamily,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_EXTERNAL,
            buffer = _slots[k],
            offset = 0,
            size = VK_WHOLE_SIZE,
        };
        _api.vkCmdPipelineBarrier2(cmd, &dep);
        VulkanException.Check(_api.vkEndCommandBuffer(cmd), "vkEndCommandBuffer");

        var wait = new VkSemaphoreSubmitInfo
        {
            semaphore = _released,
            value = RingProtocol.ReleasedValueBeforeWrite(n, SlotCount),
            stageMask = VkPipelineStageFlags2.Copy,
        };
        var signal = new VkSemaphoreSubmitInfo { semaphore = _written, value = n, stageMask = VkPipelineStageFlags2.AllCommands };
        var cmdInfo = new VkCommandBufferSubmitInfo { commandBuffer = cmd };
        var submit = new VkSubmitInfo2
        {
            waitSemaphoreInfoCount = wait.value > 0 ? 1u : 0u,
            pWaitSemaphoreInfos = &wait,
            commandBufferInfoCount = 1,
            pCommandBufferInfos = &cmdInfo,
            signalSemaphoreInfoCount = 1,
            pSignalSemaphoreInfos = &signal,
        };
        VulkanException.Check(_api.vkQueueSubmit2(_device.Queue, 1, &submit, default), "vkQueueSubmit2");
        _next = n + 1;
        return n;
    }

    private void Import(PresentationRingExport export)
    {
        for (var i = 0; i < SlotCount; i++)
        {
            var external = new VkExternalMemoryBufferCreateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.OpaqueFD };
            var bufferInfo = new VkBufferCreateInfo
            {
                pNext = &external,
                size = (ulong)ContentSizeBytes,
                usage = RingUsage,
                sharingMode = VkSharingMode.Exclusive,
            };
            VkBuffer buffer;
            VulkanException.Check(_api.vkCreateBuffer(&bufferInfo, &buffer), "vkCreateBuffer");
            _slots[i] = buffer;
            VkMemoryRequirements reqs;
            _api.vkGetBufferMemoryRequirements(buffer, &reqs);
            if ((reqs.memoryTypeBits & (1u << (int)export.MemoryTypeIndex)) == 0)
            {
                throw new VulkanException("The importing buffer cannot use the exporter's memory type.");
            }

            var fd = export.SlotMemory[i];
            var import = new VkImportMemoryFdInfoKHR { handleType = VkExternalMemoryHandleTypeFlags.OpaqueFD, fd = fd.Fd };
            var dedicated = new VkMemoryDedicatedAllocateInfo { pNext = &import, buffer = buffer };
            var allocInfo = new VkMemoryAllocateInfo
            {
                pNext = &dedicated,
                allocationSize = (ulong)export.AllocationSizeBytes,
                memoryTypeIndex = export.MemoryTypeIndex,
            };
            VkDeviceMemory memory;
            VulkanException.Check(_api.vkAllocateMemory(&allocInfo, &memory), "vkAllocateMemory (import)");
            fd.MarkTransferred();
            _slotMemory[i] = memory;
            VulkanException.Check(_api.vkBindBufferMemory(buffer, memory, 0), "vkBindBufferMemory");
        }

        _written = ImportTimeline(export.WrittenSemaphore);
        _released = ImportTimeline(export.ReleasedSemaphore);
    }

    private VkSemaphore ImportTimeline(ExportedFd fd)
    {
        var type = new VkSemaphoreTypeCreateInfo { semaphoreType = VkSemaphoreType.Timeline, initialValue = 0 };
        var info = new VkSemaphoreCreateInfo { pNext = &type };
        VkSemaphore semaphore;
        VulkanException.Check(_api.vkCreateSemaphore(&info, &semaphore), "vkCreateSemaphore");
        var import = new VkImportSemaphoreFdInfoKHR
        {
            semaphore = semaphore,
            handleType = VkExternalSemaphoreHandleTypeFlags.OpaqueFD,
            fd = fd.Fd,
        };
        var result = _api.vkImportSemaphoreFdKHR(&import);
        if (result < 0)
        {
            _api.vkDestroySemaphore(semaphore);
            VulkanException.Check(result, "vkImportSemaphoreFdKHR");
        }

        fd.MarkTransferred();
        return semaphore;
    }

    private void CreateStaging()
    {
        var poolInfo = new VkCommandPoolCreateInfo
        {
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = _device.QueueFamily,
        };
        VkCommandPool pool;
        VulkanException.Check(_api.vkCreateCommandPool(&poolInfo, &pool), "vkCreateCommandPool");
        _pool = pool;
        var allocInfo = new VkCommandBufferAllocateInfo { commandPool = pool, level = VkCommandBufferLevel.Primary, commandBufferCount = (uint)SlotCount };
        fixed (VkCommandBuffer* p = _cmd)
        {
            VulkanException.Check(_api.vkAllocateCommandBuffers(&allocInfo, p), "vkAllocateCommandBuffers");
        }

        for (var i = 0; i < SlotCount; i++)
        {
            var info = new VkBufferCreateInfo { size = (ulong)ContentSizeBytes, usage = VkBufferUsageFlags.TransferSrc, sharingMode = VkSharingMode.Exclusive };
            VkBuffer buffer;
            VulkanException.Check(_api.vkCreateBuffer(&info, &buffer), "vkCreateBuffer");
            _staging[i] = buffer;
            VkMemoryRequirements reqs;
            _api.vkGetBufferMemoryRequirements(buffer, &reqs);
            var alloc = new VkMemoryAllocateInfo
            {
                allocationSize = reqs.size,
                memoryTypeIndex = _device.FindMemoryType(reqs.memoryTypeBits, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent),
            };
            VkDeviceMemory memory;
            VulkanException.Check(_api.vkAllocateMemory(&alloc, &memory), "vkAllocateMemory");
            _stagingMemory[i] = memory;
            VulkanException.Check(_api.vkBindBufferMemory(buffer, memory, 0), "vkBindBufferMemory");
            void* mapped;
            VulkanException.Check(_api.vkMapMemory(memory, 0, VK_WHOLE_SIZE, 0, &mapped), "vkMapMemory");
            _stagingMapped[i] = (byte*)mapped;
        }
    }

    private ulong Read(VkSemaphore semaphore)
    {
        ulong value;
        VulkanException.Check(_api.vkGetSemaphoreCounterValue(semaphore, &value), "vkGetSemaphoreCounterValue");
        return value;
    }

    private bool Wait(VkSemaphore semaphore, ulong value, long timeoutNs)
    {
        var s = semaphore;
        var info = new VkSemaphoreWaitInfo { semaphoreCount = 1, pSemaphores = &s, pValues = &value };
        var result = _api.vkWaitSemaphores(&info, (ulong)Math.Max(0, timeoutNs));
        VulkanException.Check(result, "vkWaitSemaphores");
        return result == VkResult.Success;
    }

    private void Destroy()
    {
        if (_device.IsDisposed)
        {
            return;
        }

        _ = _api.vkDeviceWaitIdle();
        for (var i = 0; i < SlotCount; i++)
        {
            if (!_staging[i].IsNull)
            {
                _api.vkDestroyBuffer(_staging[i]);
            }

            if (!_stagingMemory[i].IsNull)
            {
                _api.vkFreeMemory(_stagingMemory[i]);
            }

            if (!_slots[i].IsNull)
            {
                _api.vkDestroyBuffer(_slots[i]);
            }

            if (!_slotMemory[i].IsNull)
            {
                _api.vkFreeMemory(_slotMemory[i]);
            }
        }

        if (!_pool.IsNull)
        {
            _api.vkDestroyCommandPool(_pool);
        }

        if (!_written.IsNull)
        {
            _api.vkDestroySemaphore(_written);
        }

        if (!_released.IsNull)
        {
            _api.vkDestroySemaphore(_released);
        }

        _device.Dispose();
    }
}
