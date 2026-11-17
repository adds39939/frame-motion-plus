using System.Diagnostics;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Diagnostics;
using FrameMotionPlus.Vulkan.Tests.Support;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace FrameMotionPlus.Vulkan.Tests;

/// <summary>The Phase 2 review's probes, kept as regression tests (the release-protocol ones are in ReleaseProtocolTests).</summary>
public unsafe class ReviewRegressionTests
{
    private static int FdCount() => Directory.EnumerateFileSystemEntries("/proc/self/fd").Count();

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void File_descriptors_stay_stable_over_ring_replacement_and_exports() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 320, 180, PixelFormat.Rgba8);
        void Cycle(int i)
        {
            var (w, h, f) = (i % 3) switch
            {
                0 => (320, 180, PixelFormat.Rgb10A2),
                1 => (640, 360, PixelFormat.Rgba8),
                _ => (320, 180, PixelFormat.Rgba8),
            };
            rig.Configure(w, h, f);
            rig.WriteAndPresent(TestPattern.Ramp, i);
            Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
        }

        for (var i = 0; i < 6; i++)
        {
            Cycle(i);
        }

        var before = FdCount();
        for (var i = 0; i < 90; i++)
        {
            Cycle(i);
        }

        var afterCycles = FdCount();
        for (var i = 0; i < 200; i++)
        {
            using var export = rig.Ring.Export();
        }

        var afterExports = FdCount();
        for (var i = 0; i < 100; i++)
        {
            using var ring = PresentationRing.Create(ctx.Device, new PresentationRingConfig { Width = 64, Height = 64, Format = PixelFormat.Rgba8 });
            using var e = ring.Export();
        }

        var afterRings = FdCount();
        Gpu.WriteLine($"fds: before {before}, after 90 reconfigure cycles {afterCycles}, after 200 unimported exports {afterExports}, after 100 rings {afterRings}");
        Assert.Equal(before, afterCycles);
        Assert.Equal(before, afterExports);
        Assert.Equal(before, afterRings);
    });

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(PixelFormat.Rgba8)]
    [InlineData(PixelFormat.Rgb10A2)]
    public void Odd_sizes_and_tight_pitches_are_bit_exact_with_random_pixels(PixelFormat format) => Gpu.Run(ctx =>
    {
        var rng = new Random(7);
        (int, int)[] sizes = [(1, 1), (3, 1), (1, 5), (17, 33), (15, 15), (16, 16), (255, 3), (4097, 2), (2, 4099), (63, 65)];
        foreach (var (w, h) in sizes)
        {
            foreach (var alignment in new[] { 256, 4 })
            {
                using var rig = OffscreenRig.Create(ctx, w, h, format, presenter: new VulkanPresenterOptions { PitchAlignment = alignment });
                var pitch = rig.Ring.PitchBytes;
                var bytes = new byte[rig.Ring.ContentSizeBytes];
#pragma warning disable CA5394 // Test data, not security.
                rng.NextBytes(bytes); // row padding gets garbage too
#pragma warning restore CA5394
                rig.PresentPixels(bytes);
                var output = rig.ReadPixels();
                var alphaMask = format == PixelFormat.Rgb10A2 ? 0xC0000000u : 0xFF000000u;
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var src = BitConverter.ToUInt32(bytes, (y * pitch) + (x * 4));
                        var dst = output[(y * w) + x];
                        if ((src | alphaMask) != dst)
                        {
                            Assert.Fail($"{format} {w}x{h} pitch {pitch}: ({x},{y}) src {src:X8} out {dst:X8}");
                        }
                    }
                }
            }
        }
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_slot_is_not_overwritten_before_its_copy_completes_at_4k() => Gpu.Run(ctx =>
    {
        const int W = 3840;
        const int H = 2160;
        using var rig = OffscreenRig.Create(ctx, W, H, PixelFormat.Rgba8);
        var n = rig.Ring.SlotCount;
        for (var i = 1; i <= 40; i++)
        {
            // Queue writes up to i + N: write i + N targets the slot of write i and waits GPU-side for released >= i.
            while (rig.Producer.NextWrite <= (ulong)(i + n))
            {
                rig.Producer.WritePattern(TestPattern.Ramp, (int)rig.Producer.NextWrite);
            }

            rig.WaitReady();
            Assert.True(rig.Presenter.TryPresent(rig.Request((ulong)i)));
            var pixels = rig.ReadPixels();
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    if (pixels[(y * W) + x] != TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, x, y, W, H, i))
                    {
                        Assert.Fail($"write {i}: pixel ({x},{y}) wrong");
                    }
                }
            }
        }

        rig.Presenter.ReleaseThrough(rig.Producer.NextWrite - 1);
        Assert.True(rig.Presenter.WaitIdle(3_000_000_000));
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void An_8k_ring_is_bit_exact_at_the_corners() => Gpu.Run(ctx =>
    {
        const int W = 7680;
        const int H = 4320;
        using var rig = OffscreenRig.Create(ctx, W, H, PixelFormat.Rgb10A2);
        rig.WriteAndPresent(TestPattern.Ramp, 5);
        var pixels = rig.ReadPixels();
        foreach (var (x, y) in new[] { (0, 0), (W - 1, 0), (0, H - 1), (W - 1, H - 1), (W / 2, H / 2), (4095, 4095), (4096, 4096) })
        {
            Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgb10A2, x, y, W, H, 5), pixels[(y * W) + x]);
        }
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void The_compute_copy_is_several_times_faster_than_a_transfer_copy() => Gpu.Run(ctx =>
    {
        var device = ctx.Device;
        var api = device.Api;
        const int W = 3840;
        const int H = 2160;
        using var ring = PresentationRing.Create(device, new PresentationRingConfig { Width = W, Height = H, Format = PixelFormat.Rgba8 });
        var formats = stackalloc VkFormat[2] { VkFormat.R8G8B8A8Unorm, VkFormat.R32Uint };
        var formatList = new VkImageFormatListCreateInfo { viewFormatCount = 2, pViewFormats = formats };
        var computeInfo = new VkImageCreateInfo
        {
            pNext = &formatList,
            flags = VkImageCreateFlags.MutableFormat | VkImageCreateFlags.ExtendedUsage,
            imageType = VkImageType.Image2D,
            format = VkFormat.R8G8B8A8Unorm,
            extent = new VkExtent3D(W, H, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
        };
        var transferInfo = computeInfo;
        transferInfo.pNext = null;
        transferInfo.flags = VkImageCreateFlags.None;
        transferInfo.usage = VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled;
        using var computeImage = new TestImage(device, computeInfo);
        using var transferImage = new TestImage(device, transferInfo);
        var storageUsage = new VkImageViewUsageCreateInfo { usage = VkImageUsageFlags.Storage };
        var viewInfo = new VkImageViewCreateInfo
        {
            pNext = &storageUsage,
            image = computeImage.Image,
            viewType = VkImageViewType.Image2D,
            format = VkFormat.R32Uint,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        VkImageView words;
        VulkanException.Check(api.vkCreateImageView(&viewInfo, &words), "vkCreateImageView");
        using var pass = new PresentPass(device);
        pass.BindCopy(ring, words);
        var queryInfo = new VkQueryPoolCreateInfo { queryType = VkQueryType.Timestamp, queryCount = 2 };
        VkQueryPool created;
        VulkanException.Check(api.vkCreateQueryPool(&queryInfo, &created), "vkCreateQueryPool");
        var queries = created;
        try
        {
            double Time(bool compute, int copies)
            {
                var stamps = stackalloc ulong[2];
                BufferIo.SubmitAndWait(device, cmd =>
                {
                    api.vkCmdResetQueryPool(cmd, queries, 0, 2);
                    var barrier = new VkImageMemoryBarrier2
                    {
                        dstStageMask = compute ? VkPipelineStageFlags2.ComputeShader : VkPipelineStageFlags2.Copy,
                        dstAccessMask = compute ? VkAccessFlags2.ShaderStorageWrite : VkAccessFlags2.TransferWrite,
                        oldLayout = VkImageLayout.Undefined,
                        newLayout = compute ? VkImageLayout.General : VkImageLayout.TransferDstOptimal,
                        srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                        dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                        image = compute ? computeImage.Image : transferImage.Image,
                        subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
                    };
                    var dep = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &barrier };
                    api.vkCmdPipelineBarrier2(cmd, &dep);
                    api.vkCmdWriteTimestamp2(cmd, VkPipelineStageFlags2.TopOfPipe, queries, 0);
                    for (var i = 0; i < copies; i++)
                    {
                        // Each copy writes the whole image again; order them so the timing is the sum of the copies.
                        var waw = new VkImageMemoryBarrier2
                        {
                            srcStageMask = compute ? VkPipelineStageFlags2.ComputeShader : VkPipelineStageFlags2.Copy,
                            srcAccessMask = compute ? VkAccessFlags2.ShaderStorageWrite : VkAccessFlags2.TransferWrite,
                            dstStageMask = compute ? VkPipelineStageFlags2.ComputeShader : VkPipelineStageFlags2.Copy,
                            dstAccessMask = compute ? VkAccessFlags2.ShaderStorageWrite : VkAccessFlags2.TransferWrite,
                            oldLayout = compute ? VkImageLayout.General : VkImageLayout.TransferDstOptimal,
                            newLayout = compute ? VkImageLayout.General : VkImageLayout.TransferDstOptimal,
                            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                            image = compute ? computeImage.Image : transferImage.Image,
                            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
                        };
                        var wawDep = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &waw };
                        if (i > 0)
                        {
                            api.vkCmdPipelineBarrier2(cmd, &wawDep);
                        }

                        var slot = i % ring.SlotCount;
                        if (compute)
                        {
                            api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Compute, pass.CopyPipeline);
                            var set = pass.CopySet(slot);
                            api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Compute, pass.CopyLayout, 0, 1, &set, 0, null);
                            var push = new CopyPushConstants { PitchWords = (uint)(ring.PitchBytes / 4), Width = W, Height = H };
                            api.vkCmdPushConstants(cmd, pass.CopyLayout, VkShaderStageFlags.Compute, 0, (uint)sizeof(CopyPushConstants), &push);
                            api.vkCmdDispatch(cmd, (W + 15) / 16, (H + 15) / 16, 1);
                        }
                        else
                        {
                            var region = new VkBufferImageCopy
                            {
                                bufferRowLength = (uint)(ring.PitchBytes / 4),
                                imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                                imageExtent = new VkExtent3D(W, H, 1),
                            };
                            api.vkCmdCopyBufferToImage(cmd, ring.Buffer(slot), transferImage.Image, VkImageLayout.TransferDstOptimal, 1, &region);
                        }
                    }

                    api.vkCmdWriteTimestamp2(cmd, VkPipelineStageFlags2.AllCommands, queries, 1);
                });
                VulkanException.Check(api.vkGetQueryPoolResults(queries, 0, 2, 2 * sizeof(ulong), stamps, sizeof(ulong), VkQueryResultFlags.Bit64 | VkQueryResultFlags.Wait), "vkGetQueryPoolResults");
                return (stamps[1] - stamps[0]) * device.Capabilities.TimestampPeriodNs / 1000.0;
            }

            double PerCopy(bool compute)
            {
                Time(compute, 1);
                var samples = new List<double>();
                for (var i = 0; i < 5; i++)
                {
                    samples.Add((Time(compute, 41) - Time(compute, 1)) / 40);
                }

                samples.Sort();
                return samples[2];
            }

            var computeUs = PerCopy(compute: true);
            var transferUs = PerCopy(compute: false);
            Gpu.WriteLine($"4K copy, per copy: compute dispatch {computeUs:F1} us, vkCmdCopyBufferToImage {transferUs:F1} us");
            Assert.True(computeUs * 3 < transferUs, $"compute {computeUs:F1} us vs transfer {transferUs:F1} us");
        }
        finally
        {
            api.vkDestroyQueryPool(queries);
            api.vkDestroyImageView(words);
        }
    });

    private sealed class TestImage : IDisposable
    {
        private readonly VulkanDevice _device;
        private readonly VkDeviceMemory _memory;

        public TestImage(VulkanDevice device, VkImageCreateInfo info)
        {
            _device = device;
            var api = device.Api;
            VkImage image;
            VulkanException.Check(api.vkCreateImage(&info, &image), "vkCreateImage");
            Image = image;
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
        }

        public VkImage Image { get; }

        public void Dispose()
        {
            _device.Api.vkDestroyImage(Image);
            _device.Api.vkFreeMemory(_memory);
        }
    }
}
