using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace FrameMotionPlus.Vulkan.Tests.Support;

/// <summary>
/// Blocking buffer uploads/downloads and raw FD imports on a given device, for tests that check export and import
/// directly (without the presenter or the diagnostic producer).
/// </summary>
internal static unsafe class BufferIo
{
    /// <summary>Writes <paramref name="data"/> into <paramref name="buffer"/>. External buffers are taken from and handed back to the external queue family.</summary>
    public static void Upload(VulkanDevice device, VkBuffer buffer, ReadOnlySpan<byte> data, bool external)
    {
        using var staging = new HostBuffer(device, data.Length, VkBufferUsageFlags.TransferSrc);
        data.CopyTo(staging.Span);
        var length = (ulong)data.Length;
        SubmitAndWait(device, cmd =>
        {
            if (external)
            {
                OwnershipBarrier(device, cmd, buffer, acquire: true, VkAccessFlags2.TransferWrite);
            }

            var region = new VkBufferCopy { size = length };
            device.Api.vkCmdCopyBuffer(cmd, staging.Buffer, buffer, 1, &region);
            if (external)
            {
                OwnershipBarrier(device, cmd, buffer, acquire: false, VkAccessFlags2.TransferWrite);
            }
        });
    }

    public static void Download(VulkanDevice device, VkBuffer buffer, Span<byte> data, bool external)
    {
        using var staging = new HostBuffer(device, data.Length, VkBufferUsageFlags.TransferDst);
        SubmitAndWait(device, cmd =>
        {
            if (external)
            {
                OwnershipBarrier(device, cmd, buffer, acquire: true, VkAccessFlags2.TransferRead);
            }

            var region = new VkBufferCopy { size = (ulong)staging.Size };
            device.Api.vkCmdCopyBuffer(cmd, buffer, staging.Buffer, 1, &region);
            if (external)
            {
                OwnershipBarrier(device, cmd, buffer, acquire: false, VkAccessFlags2.TransferRead);
            }

            var toHost = new VkBufferMemoryBarrier2
            {
                srcStageMask = VkPipelineStageFlags2.Copy,
                srcAccessMask = VkAccessFlags2.TransferWrite,
                dstStageMask = VkPipelineStageFlags2.Host,
                dstAccessMask = VkAccessFlags2.HostRead,
                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                buffer = staging.Buffer,
                size = VK_WHOLE_SIZE,
            };
            var dep = new VkDependencyInfo { bufferMemoryBarrierCount = 1, pBufferMemoryBarriers = &toHost };
            device.Api.vkCmdPipelineBarrier2(cmd, &dep);
        });
        staging.Span[..data.Length].CopyTo(data);
    }

    /// <summary>Imports an exported ring slot into a new buffer on <paramref name="device"/> (marks the FD transferred).</summary>
    public static ImportedBuffer ImportBuffer(VulkanDevice device, ExportedFd fd, long contentSize, long allocationSize, uint memoryTypeIndex)
    {
        var api = device.Api;
        var external = new VkExternalMemoryBufferCreateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.OpaqueFD };
        var info = new VkBufferCreateInfo
        {
            pNext = &external,
            size = (ulong)contentSize,
            usage = VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.StorageBuffer,
            sharingMode = VkSharingMode.Exclusive,
        };
        VkBuffer buffer;
        VulkanException.Check(api.vkCreateBuffer(&info, &buffer), "vkCreateBuffer");
        var import = new VkImportMemoryFdInfoKHR { handleType = VkExternalMemoryHandleTypeFlags.OpaqueFD, fd = fd.Fd };
        var dedicated = new VkMemoryDedicatedAllocateInfo { pNext = &import, buffer = buffer };
        var alloc = new VkMemoryAllocateInfo { pNext = &dedicated, allocationSize = (ulong)allocationSize, memoryTypeIndex = memoryTypeIndex };
        VkDeviceMemory memory;
        var result = api.vkAllocateMemory(&alloc, &memory);
        if (result < 0)
        {
            api.vkDestroyBuffer(buffer);
            VulkanException.Check(result, "vkAllocateMemory (import)");
        }

        fd.MarkTransferred();
        VulkanException.Check(api.vkBindBufferMemory(buffer, memory, 0), "vkBindBufferMemory");
        return new ImportedBuffer(device, buffer, memory);
    }

    public static ImportedSemaphore ImportTimeline(VulkanDevice device, ExportedFd fd)
    {
        var api = device.Api;
        var type = new VkSemaphoreTypeCreateInfo { semaphoreType = VkSemaphoreType.Timeline };
        var info = new VkSemaphoreCreateInfo { pNext = &type };
        VkSemaphore semaphore;
        VulkanException.Check(api.vkCreateSemaphore(&info, &semaphore), "vkCreateSemaphore");
        var import = new VkImportSemaphoreFdInfoKHR { semaphore = semaphore, handleType = VkExternalSemaphoreHandleTypeFlags.OpaqueFD, fd = fd.Fd };
        VulkanException.Check(api.vkImportSemaphoreFdKHR(&import), "vkImportSemaphoreFdKHR");
        fd.MarkTransferred();
        return new ImportedSemaphore(device, semaphore);
    }

    public static ulong Counter(VulkanDevice device, VkSemaphore semaphore)
    {
        ulong value;
        VulkanException.Check(device.Api.vkGetSemaphoreCounterValue(semaphore, &value), "vkGetSemaphoreCounterValue");
        return value;
    }

    public static bool WaitCounter(VulkanDevice device, VkSemaphore semaphore, ulong value, long timeoutNs)
    {
        var s = semaphore;
        var info = new VkSemaphoreWaitInfo { semaphoreCount = 1, pSemaphores = &s, pValues = &value };
        var result = device.Api.vkWaitSemaphores(&info, (ulong)timeoutNs);
        VulkanException.Check(result, "vkWaitSemaphores");
        return result == VkResult.Success;
    }

    public static void HostSignal(VulkanDevice device, VkSemaphore semaphore, ulong value)
    {
        var info = new VkSemaphoreSignalInfo { semaphore = semaphore, value = value };
        VulkanException.Check(device.Api.vkSignalSemaphore(&info), "vkSignalSemaphore");
    }

    /// <summary>A submission with no commands that waits on one timeline value and signals another.</summary>
    public static void SubmitWaitSignal(VulkanDevice device, VkSemaphore wait, ulong waitValue, VkSemaphore signal, ulong signalValue)
    {
        var w = new VkSemaphoreSubmitInfo { semaphore = wait, value = waitValue, stageMask = VkPipelineStageFlags2.AllCommands };
        var s = new VkSemaphoreSubmitInfo { semaphore = signal, value = signalValue, stageMask = VkPipelineStageFlags2.AllCommands };
        var submit = new VkSubmitInfo2
        {
            waitSemaphoreInfoCount = wait.IsNull ? 0u : 1u,
            pWaitSemaphoreInfos = &w,
            signalSemaphoreInfoCount = 1,
            pSignalSemaphoreInfos = &s,
        };
        VulkanException.Check(device.Api.vkQueueSubmit2(device.Queue, 1, &submit, default), "vkQueueSubmit2");
    }

    private static void OwnershipBarrier(VulkanDevice device, VkCommandBuffer cmd, VkBuffer buffer, bool acquire, VkAccessFlags2 access)
    {
        var barrier = new VkBufferMemoryBarrier2
        {
            srcStageMask = acquire ? VkPipelineStageFlags2.None : VkPipelineStageFlags2.Copy,
            srcAccessMask = acquire ? VkAccessFlags2.None : access,
            dstStageMask = acquire ? VkPipelineStageFlags2.Copy : VkPipelineStageFlags2.None,
            dstAccessMask = acquire ? access : VkAccessFlags2.None,
            srcQueueFamilyIndex = acquire ? VK_QUEUE_FAMILY_EXTERNAL : device.QueueFamily,
            dstQueueFamilyIndex = acquire ? device.QueueFamily : VK_QUEUE_FAMILY_EXTERNAL,
            buffer = buffer,
            size = VK_WHOLE_SIZE,
        };
        var dep = new VkDependencyInfo { bufferMemoryBarrierCount = 1, pBufferMemoryBarriers = &barrier };
        device.Api.vkCmdPipelineBarrier2(cmd, &dep);
    }

    internal static void SubmitAndWait(VulkanDevice device, Action<VkCommandBuffer> record)
    {
        var api = device.Api;
        var poolInfo = new VkCommandPoolCreateInfo { flags = VkCommandPoolCreateFlags.Transient, queueFamilyIndex = device.QueueFamily };
        VkCommandPool pool;
        VulkanException.Check(api.vkCreateCommandPool(&poolInfo, &pool), "vkCreateCommandPool");
        var fenceInfo = new VkFenceCreateInfo();
        VkFence fence;
        VulkanException.Check(api.vkCreateFence(&fenceInfo, &fence), "vkCreateFence");
        try
        {
            var allocInfo = new VkCommandBufferAllocateInfo { commandPool = pool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
            VkCommandBuffer cmd;
            VulkanException.Check(api.vkAllocateCommandBuffers(&allocInfo, &cmd), "vkAllocateCommandBuffers");
            var begin = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
            VulkanException.Check(api.vkBeginCommandBuffer(cmd, &begin), "vkBeginCommandBuffer");
            record(cmd);
            VulkanException.Check(api.vkEndCommandBuffer(cmd), "vkEndCommandBuffer");
            var cmdInfo = new VkCommandBufferSubmitInfo { commandBuffer = cmd };
            var submit = new VkSubmitInfo2 { commandBufferInfoCount = 1, pCommandBufferInfos = &cmdInfo };
            VulkanException.Check(api.vkQueueSubmit2(device.Queue, 1, &submit, fence), "vkQueueSubmit2");
            VulkanException.Check(api.vkWaitForFences(1, &fence, true, 5_000_000_000), "vkWaitForFences");
        }
        finally
        {
            api.vkDestroyFence(fence);
            api.vkDestroyCommandPool(pool);
        }
    }

    private sealed class HostBuffer : IDisposable
    {
        private readonly VulkanDevice _device;
        private readonly VkDeviceMemory _memory;
        private readonly byte* _mapped;

        public HostBuffer(VulkanDevice device, long size, VkBufferUsageFlags usage)
        {
            _device = device;
            Size = size;
            var api = device.Api;
            var info = new VkBufferCreateInfo { size = (ulong)size, usage = usage, sharingMode = VkSharingMode.Exclusive };
            VkBuffer buffer;
            VulkanException.Check(api.vkCreateBuffer(&info, &buffer), "vkCreateBuffer");
            Buffer = buffer;
            VkMemoryRequirements reqs;
            api.vkGetBufferMemoryRequirements(buffer, &reqs);
            var alloc = new VkMemoryAllocateInfo
            {
                allocationSize = reqs.size,
                memoryTypeIndex = device.FindMemoryType(reqs.memoryTypeBits, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent),
            };
            VkDeviceMemory memory;
            VulkanException.Check(api.vkAllocateMemory(&alloc, &memory), "vkAllocateMemory");
            _memory = memory;
            VulkanException.Check(api.vkBindBufferMemory(buffer, memory, 0), "vkBindBufferMemory");
            void* mapped;
            VulkanException.Check(api.vkMapMemory(memory, 0, VK_WHOLE_SIZE, 0, &mapped), "vkMapMemory");
            _mapped = (byte*)mapped;
        }

        public VkBuffer Buffer { get; }

        public long Size { get; }

        public Span<byte> Span => new(_mapped, (int)Size);

        public void Dispose()
        {
            _device.Api.vkDestroyBuffer(Buffer);
            _device.Api.vkFreeMemory(_memory);
        }
    }
}

internal sealed class ImportedBuffer(VulkanDevice device, VkBuffer buffer, VkDeviceMemory memory) : IDisposable
{
    public VkBuffer Buffer { get; } = buffer;

    public void Dispose()
    {
        device.Api.vkDestroyBuffer(Buffer);
        device.Api.vkFreeMemory(memory);
    }
}

internal sealed class ImportedSemaphore(VulkanDevice device, VkSemaphore semaphore) : IDisposable
{
    public VkSemaphore Semaphore { get; } = semaphore;

    public void Dispose() => device.Api.vkDestroySemaphore(Semaphore);
}
