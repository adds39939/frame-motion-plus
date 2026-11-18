using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using Vortice.Vulkan;

namespace FrameMotionPlus.Vulkan;

public sealed record OffscreenTargetOptions
{
    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    /// <summary>Output format when the presenter asks for SDR: <see cref="PixelFormat.Rgba8"/> or <see cref="PixelFormat.Rgb10A2"/>.</summary>
    public PixelFormat SdrFormat { get; init; } = PixelFormat.Rgba8;

    /// <summary>Behave like a display that offers HDR10 ST 2084 (A2B10G10R10); false behaves like an SDR-only display.</summary>
    public bool Hdr10Capable { get; init; } = true;

    /// <summary>Copy every presented frame to a host-visible buffer so <see cref="OffscreenTarget.ReadLatest"/> can return it.</summary>
    public bool Readback { get; init; } = true;

    /// <summary>What <see cref="VulkanPresenter.Timing"/> reports; nothing is paced offscreen.</summary>
    public FrameRate RefreshRate { get; init; } = FrameRate.FromHz(60);
}

/// <summary>
/// A headless target: the presenter renders into offscreen images (two, one per frame in flight) and, with
/// <see cref="OffscreenTargetOptions.Readback"/>, each frame is copied to a host-visible buffer. Needs no display
/// server. "Present" completes when the frame's GPU work completes.
/// <para>
/// Timing measured offscreen includes a driver cost that is not the presenter's: on NVIDIA 620.70.06,
/// <c>vkQueueSubmit2</c> takes 1.56 to 1.68 ms on every 1024th submission on a queue (submits 1017, 2041, 3065, 4089,
/// ... in a bare loop), whatever the command buffers hold, their spacing or the frame size; on-CPU, no context switch,
/// no GC. It shows offscreen and with a swapchain that is not presented to; across about 11,000 windowed presents at 120
/// and 240 Hz nothing reached 1 ms after warm-up (p99.9 0.29 to 0.44 ms). So occasional ~1.5 ms TryPresent calls in the
/// offscreen benchmark are that, not presenter logic.
/// </para>
/// </summary>
public sealed unsafe class OffscreenTarget : PresentTarget
{
    private const int Images2 = 2;

    private readonly OffscreenTargetOptions _options;
    private readonly ulong[] _readbackFrame = new ulong[Images2];
    private VkDeviceMemory[] _imageMemory = [];
    private VkBuffer[] _readback = [];
    private VkDeviceMemory[] _readbackMemory = [];
    private byte*[] _readbackMapped = [];
    private bool _readbackCoherent;
    private int _desiredWidth;
    private int _desiredHeight;
    private OutputRequest _request;
    private bool _disposed;

    public OffscreenTarget(VulkanDevice device, OffscreenTargetOptions options)
        : base(device)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Width <= 0 || options.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Offscreen size must be positive.");
        }

        if (options.SdrFormat is not (PixelFormat.Rgba8 or PixelFormat.Rgb10A2))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.SdrFormat, "SDR format must be Rgba8 or Rgb10A2.");
        }

        _options = options;
        _desiredWidth = options.Width;
        _desiredHeight = options.Height;
    }

    public override FrameRate RefreshRate => _options.RefreshRate;

    public override bool SupportsHdr10 => _options.Hdr10Capable;

    /// <summary>Layout of <see cref="ReadLatest"/> data: tightly packed rows of 4-byte pixels in this format.</summary>
    public PixelFormat PixelFormat => Format == VkFormat.A2B10G10R10UnormPack32 ? PixelFormat.Rgb10A2 : PixelFormat.Rgba8;

    public int ReadbackSizeBytes => Width * Height * 4;

    /// <summary>Changes the image size; the presenter recreates the images before its next frame.</summary>
    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _desiredWidth = width;
        _desiredHeight = height;
    }

    /// <summary>
    /// Waits for the most recently presented frame's GPU work, then copies its pixels into
    /// <paramref name="destination"/> (<see cref="ReadbackSizeBytes"/> bytes). Returns that frame's number, or 0 if
    /// nothing has been presented since the images were last created. Blocks; for tests and tools.
    /// </summary>
    public ulong ReadLatest(Span<byte> destination, long timeoutNs = 5_000_000_000)
    {
        if (!_options.Readback)
        {
            throw new InvalidOperationException("Readback is off for this target.");
        }

        var index = _readbackFrame[0] >= _readbackFrame[1] ? 0 : 1;
        var frame = _readbackFrame[index];
        if (frame == 0)
        {
            return 0;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, ReadbackSizeBytes, nameof(destination));
        var semaphore = FrameDone;
        var info = new VkSemaphoreWaitInfo { semaphoreCount = 1, pSemaphores = &semaphore, pValues = &frame };
        var result = Device.Api.vkWaitSemaphores(&info, (ulong)timeoutNs);
        VulkanException.Check(result, "vkWaitSemaphores");
        if (result != VkResult.Success)
        {
            throw new TimeoutException($"Frame {frame} did not complete within {timeoutNs / 1_000_000} ms.");
        }

        if (!_readbackCoherent)
        {
            var range = new VkMappedMemoryRange { memory = _readbackMemory[index], offset = 0, size = Vortice.Vulkan.Vulkan.VK_WHOLE_SIZE };
            VulkanException.Check(Device.Api.vkInvalidateMappedMemoryRanges(1, &range), "vkInvalidateMappedMemoryRanges");
        }

        new ReadOnlySpan<byte>(_readbackMapped[index], ReadbackSizeBytes).CopyTo(destination);
        return frame;
    }

    internal override void Configure(in OutputRequest request)
    {
        _request = request;
        Recreate();
    }

    internal override bool NeedsRecreate() => _desiredWidth != Width || _desiredHeight != Height;

    internal override void Recreate()
    {
        DestroyImages();
        var hdr = _request.PreferHdr10 && _options.Hdr10Capable;
        ColorSpace = hdr ? TargetColorSpace.Hdr10St2084 : TargetColorSpace.Sdr;
        Format = hdr || _options.SdrFormat == PixelFormat.Rgb10A2 ? VkFormat.A2B10G10R10UnormPack32 : VkFormat.R8G8B8A8Unorm;
        Width = _desiredWidth;
        Height = _desiredHeight;
        CreateImages();
        Version++;
    }

    internal override AcquireOutcome TryAcquire(ulong frame, int frameSlot, VkSemaphore acquireSemaphore, out int imageIndex)
    {
        // One image per frame slot: the presenter only reuses a slot once its previous frame has completed.
        imageIndex = frameSlot % ImageCount;
        return AcquireOutcome.Acquired;
    }

    internal override void RecordEpilogue(VkCommandBuffer cmd, int imageIndex, ulong frame)
    {
        if (!_options.Readback)
        {
            return;
        }

        var api = Device.Api;
        var toTransfer = new VkImageMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.ColorAttachmentOutput,
            srcAccessMask = VkAccessFlags2.ColorAttachmentWrite,
            dstStageMask = VkPipelineStageFlags2.Copy,
            dstAccessMask = VkAccessFlags2.TransferRead,
            oldLayout = VkImageLayout.ColorAttachmentOptimal,
            newLayout = VkImageLayout.TransferSrcOptimal,
            srcQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            image = Images[imageIndex],
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        var dep = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &toTransfer };
        api.vkCmdPipelineBarrier2(cmd, &dep);

        var region = new VkBufferImageCopy
        {
            bufferOffset = 0,
            bufferRowLength = 0,
            bufferImageHeight = 0,
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageExtent = new VkExtent3D((uint)Width, (uint)Height, 1),
        };
        api.vkCmdCopyImageToBuffer(cmd, Images[imageIndex], VkImageLayout.TransferSrcOptimal, _readback[imageIndex], 1, &region);

        var toHost = new VkBufferMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.Copy,
            srcAccessMask = VkAccessFlags2.TransferWrite,
            dstStageMask = VkPipelineStageFlags2.Host,
            dstAccessMask = VkAccessFlags2.HostRead,
            srcQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            buffer = _readback[imageIndex],
            offset = 0,
            size = Vortice.Vulkan.Vulkan.VK_WHOLE_SIZE,
        };
        var dep2 = new VkDependencyInfo { bufferMemoryBarrierCount = 1, pBufferMemoryBarriers = &toHost };
        api.vkCmdPipelineBarrier2(cmd, &dep2);
        _readbackFrame[imageIndex] = frame;
    }

    internal override PresentOutcome Present(int imageIndex, int frameSlot, ulong presentId)
    {
        PresentSubmitted?.Invoke(presentId);
        return PresentOutcome.Presented;
    }

    /// <summary>Simulations: told each present's id as it is submitted (on the presenter's thread).</summary>
    public Action<ulong>? PresentSubmitted { get; set; }

    /// <summary>
    /// Simulations (with <see cref="PresentTimeSource"/>): a present counts as complete only once its report has come, as
    /// present-wait reports a swapchain's present complete once shown, so the presenter caps presents in flight the way it
    /// does on a window.
    /// </summary>
    public bool CompletesOnReport { get; set; }

    internal override bool SupportsPresentWait => CompletesOnReport && PresentTimeSource is not null;

    internal override bool PollPresentComplete(ulong presentId) => (PresentTimeSource?.Invoke(presentId) ?? 1) != 0;

    /// <summary>
    /// Tests and simulations: makes the target report first-pixel times through this source (present id → time; 0 while its
    /// report has not come; negative for a report that came without a time), as a swapchain with present timing does, so the
    /// presenter's handling of late, missing and time-less reports, and a pipeline's pacing against a model of a compositor,
    /// can be checked offscreen. Called on the presenter's thread, only for presents whose GPU work has completed.
    /// </summary>
    public Func<ulong, long>? PresentTimeSource { get; set; }

    internal override bool ReportsPresentTimes => PresentTimeSource is not null;

    internal override bool TryTakePresentTime(ulong presentId, out long timeNs)
    {
        var t = PresentTimeSource?.Invoke(presentId) ?? 0;
        timeNs = Math.Max(0, t);
        return t != 0;
    }

    internal override bool HasPresentReport(ulong presentId) => (PresentTimeSource?.Invoke(presentId) ?? 0) != 0;

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyImages();
    }

    private void CreateImages()
    {
        var api = Device.Api;
        ImageCount = Images2;
        Images = new VkImage[Images2];
        Views = new VkImageView[Images2];
        _imageMemory = new VkDeviceMemory[Images2];
        _readback = new VkBuffer[_options.Readback ? Images2 : 0];
        _readbackMemory = new VkDeviceMemory[_readback.Length];
        _readbackMapped = new byte*[_readback.Length];
        Array.Clear(_readbackFrame);
        for (var i = 0; i < Images2; i++)
        {
            var info = new VkImageCreateInfo
            {
                imageType = VkImageType.Image2D,
                format = Format,
                extent = new VkExtent3D((uint)Width, (uint)Height, 1),
                mipLevels = 1,
                arrayLayers = 1,
                samples = VkSampleCountFlags.Count1,
                tiling = VkImageTiling.Optimal,
                usage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc,
                sharingMode = VkSharingMode.Exclusive,
                initialLayout = VkImageLayout.Undefined,
            };
            VkImage image;
            VulkanException.Check(api.vkCreateImage(&info, &image), "vkCreateImage");
            Images[i] = image;
            VkMemoryRequirements reqs;
            api.vkGetImageMemoryRequirements(image, &reqs);
            _imageMemory[i] = Allocate(reqs, VkMemoryPropertyFlags.DeviceLocal, VkMemoryPropertyFlags.HostVisible);
            VulkanException.Check(api.vkBindImageMemory(image, _imageMemory[i], 0), "vkBindImageMemory");
            Views[i] = CreateView(Device, image, Format);
        }

        for (var i = 0; i < _readback.Length; i++)
        {
            var info = new VkBufferCreateInfo
            {
                size = (ulong)ReadbackSizeBytes,
                usage = VkBufferUsageFlags.TransferDst,
                sharingMode = VkSharingMode.Exclusive,
            };
            VkBuffer buffer;
            VulkanException.Check(api.vkCreateBuffer(&info, &buffer), "vkCreateBuffer");
            _readback[i] = buffer;
            VkMemoryRequirements reqs;
            api.vkGetBufferMemoryRequirements(buffer, &reqs);
            var type = Device.FindMemoryType(reqs.memoryTypeBits, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCached);
            _readbackCoherent = (Device.MemoryTypeFlags(type) & VkMemoryPropertyFlags.HostCoherent) != 0;
            var alloc = new VkMemoryAllocateInfo { allocationSize = reqs.size, memoryTypeIndex = type };
            VkDeviceMemory memory;
            VulkanException.Check(api.vkAllocateMemory(&alloc, &memory), "vkAllocateMemory");
            _readbackMemory[i] = memory;
            VulkanException.Check(api.vkBindBufferMemory(buffer, memory, 0), "vkBindBufferMemory");
            void* mapped;
            VulkanException.Check(api.vkMapMemory(memory, 0, Vortice.Vulkan.Vulkan.VK_WHOLE_SIZE, 0, &mapped), "vkMapMemory");
            _readbackMapped[i] = (byte*)mapped;
        }
    }

    private VkDeviceMemory Allocate(VkMemoryRequirements reqs, VkMemoryPropertyFlags required, VkMemoryPropertyFlags avoid)
    {
        var alloc = new VkMemoryAllocateInfo
        {
            allocationSize = reqs.size,
            memoryTypeIndex = Device.FindMemoryType(reqs.memoryTypeBits, required, avoid),
        };
        VkDeviceMemory memory;
        VulkanException.Check(Device.Api.vkAllocateMemory(&alloc, &memory), "vkAllocateMemory");
        return memory;
    }

    private void DestroyImages()
    {
        var api = Device.Api;
        for (var i = 0; i < Views.Length; i++)
        {
            api.vkDestroyImageView(Views[i]);
            api.vkDestroyImage(Images[i]);
            api.vkFreeMemory(_imageMemory[i]);
        }

        for (var i = 0; i < _readback.Length; i++)
        {
            api.vkDestroyBuffer(_readback[i]);
            api.vkFreeMemory(_readbackMemory[i]);
        }

        Views = [];
        Images = [];
        _imageMemory = [];
        _readback = [];
        _readbackMemory = [];
        _readbackMapped = [];
        ImageCount = 0;
    }
}
