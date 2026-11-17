using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Tests.Support;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace FrameMotionPlus.Vulkan.Tests;

/// <summary>
/// The harness's synchronization validation must see hazards on shader accesses (storage buffer reads, storage image
/// writes), which is what the presenter's copy uses. Each test makes one deliberate hazard with the presenter's own
/// copy pipeline and expects a SYNC-HAZARD report.
/// </summary>
public unsafe class SyncValidationTests
{
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_transfer_write_into_a_slot_the_copy_shader_still_reads_is_reported() => Gpu.Run(ctx =>
    {
        using var copy = new CopyFixture(ctx.Device);
        BufferIo.SubmitAndWait(ctx.Device, cmd =>
        {
            copy.PrepareImage(cmd);
            copy.Dispatch(cmd);

            // Write-after-read on the slot buffer with no barrier after the dispatch that reads it.
            ctx.Device.Api.vkCmdFillBuffer(cmd, copy.Ring.Buffer(0), 0, VK_WHOLE_SIZE, 0);
        });
        ExpectHazard(ctx);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Two_copy_dispatches_writing_the_staged_image_without_a_barrier_are_reported() => Gpu.Run(ctx =>
    {
        using var copy = new CopyFixture(ctx.Device);
        BufferIo.SubmitAndWait(ctx.Device, cmd =>
        {
            copy.PrepareImage(cmd);
            copy.Dispatch(cmd);
            copy.Dispatch(cmd); // write-after-write on the storage image
        });
        ExpectHazard(ctx);
    });

    private static void ExpectHazard(GpuContext ctx)
    {
        var problems = ctx.Instance.Validation.Problems();
        Gpu.WriteLine(ctx.Instance.Validation.Describe());
        Assert.Contains(problems, static m => m.MessageIdName.StartsWith("SYNC-HAZARD", StringComparison.Ordinal));
        ctx.Instance.Validation.Clear();
    }

    /// <summary>A ring slot, an image like the presenter's staged image and the presenter's copy pipeline.</summary>
    private sealed class CopyFixture : IDisposable
    {
        private const int Size = 64;
        private readonly VulkanDevice _device;
        private readonly PresentPass _pass;
        private readonly VkImage _image;
        private readonly VkDeviceMemory _memory;
        private readonly VkImageView _words;

        public CopyFixture(VulkanDevice device)
        {
            _device = device;
            var api = device.Api;
            Ring = PresentationRing.Create(device, new PresentationRingConfig { Width = Size, Height = Size, Format = PixelFormat.Rgba8 });
            var formats = stackalloc VkFormat[2] { VkFormat.R8G8B8A8Unorm, VkFormat.R32Uint };
            var formatList = new VkImageFormatListCreateInfo { viewFormatCount = 2, pViewFormats = formats };
            var info = new VkImageCreateInfo
            {
                pNext = &formatList,
                flags = VkImageCreateFlags.MutableFormat | VkImageCreateFlags.ExtendedUsage,
                imageType = VkImageType.Image2D,
                format = VkFormat.R8G8B8A8Unorm,
                extent = new VkExtent3D(Size, Size, 1),
                mipLevels = 1,
                arrayLayers = 1,
                samples = VkSampleCountFlags.Count1,
                tiling = VkImageTiling.Optimal,
                usage = VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled,
                sharingMode = VkSharingMode.Exclusive,
            };
            VkImage image;
            VulkanException.Check(api.vkCreateImage(&info, &image), "vkCreateImage");
            _image = image;
            VkMemoryRequirements reqs;
            api.vkGetImageMemoryRequirements(image, &reqs);
            var alloc = new VkMemoryAllocateInfo
            {
                allocationSize = reqs.size,
                memoryTypeIndex = device.FindMemoryType(reqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal, VkMemoryPropertyFlags.HostVisible),
            };
            VkDeviceMemory memory;
            VulkanException.Check(api.vkAllocateMemory(&alloc, &memory), "vkAllocateMemory");
            _memory = memory;
            VulkanException.Check(api.vkBindImageMemory(image, memory, 0), "vkBindImageMemory");
            var storageUsage = new VkImageViewUsageCreateInfo { usage = VkImageUsageFlags.Storage };
            var viewInfo = new VkImageViewCreateInfo
            {
                pNext = &storageUsage,
                image = image,
                viewType = VkImageViewType.Image2D,
                format = VkFormat.R32Uint,
                subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
            };
            VkImageView words;
            VulkanException.Check(api.vkCreateImageView(&viewInfo, &words), "vkCreateImageView");
            _words = words;
            _pass = new PresentPass(device);
            _pass.BindCopy(Ring, words);
        }

        public PresentationRing Ring { get; }

        public void PrepareImage(VkCommandBuffer cmd)
        {
            var barrier = new VkImageMemoryBarrier2
            {
                dstStageMask = VkPipelineStageFlags2.ComputeShader,
                dstAccessMask = VkAccessFlags2.ShaderStorageWrite,
                oldLayout = VkImageLayout.Undefined,
                newLayout = VkImageLayout.General,
                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                image = _image,
                subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
            };
            var dep = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &barrier };
            _device.Api.vkCmdPipelineBarrier2(cmd, &dep);
        }

        public void Dispatch(VkCommandBuffer cmd)
        {
            var api = _device.Api;
            api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Compute, _pass.CopyPipeline);
            var set = _pass.CopySet(0);
            api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Compute, _pass.CopyLayout, 0, 1, &set, 0, null);
            var push = new CopyPushConstants { PitchWords = (uint)(Ring.PitchBytes / 4), Width = Size, Height = Size };
            api.vkCmdPushConstants(cmd, _pass.CopyLayout, VkShaderStageFlags.Compute, 0, (uint)sizeof(CopyPushConstants), &push);
            api.vkCmdDispatch(cmd, Size / 16, Size / 16, 1);
        }

        public void Dispose()
        {
            var api = _device.Api;
            _pass.Dispose();
            api.vkDestroyImageView(_words);
            api.vkDestroyImage(_image);
            api.vkFreeMemory(_memory);
            Ring.Dispose();
        }
    }
}
