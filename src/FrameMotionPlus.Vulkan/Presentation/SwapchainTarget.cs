using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Vulkan.Windowing;
using Vortice.Vulkan;

namespace FrameMotionPlus.Vulkan;

/// <summary>One surface format as the surface offers it (names are the Vulkan enum names without prefixes).</summary>
public readonly record struct SurfaceFormatInfo(string Format, string ColorSpace)
{
    public override string ToString() => $"{Format} / {ColorSpace}";
}

/// <summary>What a window surface offers on this device, queried when the target is created.</summary>
public sealed record SurfaceReport
{
    public IReadOnlyList<SurfaceFormatInfo> Formats { get; init; } = [];

    public IReadOnlyList<string> PresentModes { get; init; } = [];

    /// <summary>A 10-bit format with VK_COLOR_SPACE_HDR10_ST2084_EXT is offered natively (no VK_hdr_layer needed).</summary>
    public bool Hdr10St2084 { get; init; }

    public bool PresentId2Supported { get; init; }

    public bool PresentWait2Supported { get; init; }

    public bool PresentTimingSupported { get; init; }

    public bool PresentAtAbsoluteTimeSupported { get; init; }

    public bool PresentAtRelativeTimeSupported { get; init; }

    /// <summary>The present stages whose times the surface can report (VK_EXT_present_timing), as Vulkan flag names.</summary>
    public string PresentStageQueries { get; init; } = "";

    internal uint PresentStageQueryBits { get; init; }

    public uint MinImageCount { get; init; }

    public uint MaxImageCount { get; init; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("formats:");
        foreach (var f in Formats)
        {
            sb.Append("\n  ").Append(f);
        }

        sb.Append("\npresent modes: ").AppendJoin(", ", PresentModes);
        sb.Append(CultureInfo.InvariantCulture, $"\nHDR10 ST2084: {Hdr10St2084}; present_id2: {PresentId2Supported}; present_wait2: {PresentWait2Supported}; ");
        sb.Append(CultureInfo.InvariantCulture, $"present timing: {PresentTimingSupported} (absolute: {PresentAtAbsoluteTimeSupported}, relative: {PresentAtRelativeTimeSupported}); images {MinImageCount}..{MaxImageCount}");
        return sb.ToString();
    }
}

/// <summary>How the presenter learns that a present has been shown, which is what caps the presents queued behind the screen.</summary>
public enum PresentFeedbackMode
{
    /// <summary>The best available: <see cref="PresentWait2"/>, else <see cref="PresentWait"/>, else <see cref="None"/>.</summary>
    Automatic = 0,

    /// <summary>VK_KHR_present_wait2 (needs present_id2 and the surface's support).</summary>
    PresentWait2 = 1,

    /// <summary>
    /// VK_KHR_present_wait, the older extension. It keeps the cap, but on NVIDIA with Wayland it reports a present
    /// shown later than present_wait2 does, so the cap sometimes costs a refresh (a 240 Hz display was fed at 203 to
    /// 207 Hz in about a third of runs, and at 237 to 239 Hz otherwise).
    /// </summary>
    PresentWait = 2,

    /// <summary>
    /// No feedback: only the two frame slots and the swapchain's images limit presenting. That does NOT cap the queue
    /// behind the screen on drivers that hand images back before they are shown: NVIDIA on Wayland queued 5 presents
    /// with 3 images. Presents are reported complete when their GPU work is, the presenter reports
    /// <c>MaxFramesInFlight</c> = 0, and readiness cannot see "no free image" (only acquiring one tells), so most
    /// requests a fast producer makes are refused at acquire.
    /// </summary>
    None = 3,
}

/// <summary>The swapchain's present mode (the surface must offer it).</summary>
public enum SwapchainPresentMode
{
    /// <summary>VK_PRESENT_MODE_FIFO_KHR: one queued present per refresh, in order (always offered; the default).</summary>
    Fifo = 0,

    /// <summary>VK_PRESENT_MODE_FIFO_LATEST_READY: at each refresh, the newest present whose work is done; older ones are discarded.</summary>
    FifoLatestReady = 1,

    /// <summary>VK_PRESENT_MODE_MAILBOX_KHR: a newer present replaces a queued one; no tearing.</summary>
    Mailbox = 2,

    /// <summary>VK_PRESENT_MODE_IMMEDIATE_KHR: shown at once, tearing allowed.</summary>
    Immediate = 3,
}

/// <summary>Which optional mechanisms a <see cref="SwapchainTarget"/> uses (forcing a fallback is for tests and diagnosis).</summary>
public sealed record SwapchainTargetOptions
{
    /// <summary>The present mode. Anything but FIFO must be offered by the surface (and FIFO_LATEST_READY enabled on the device), else the target refuses.</summary>
    public SwapchainPresentMode PresentMode { get; init; } = SwapchainPresentMode.Fifo;

    public PresentFeedbackMode Feedback { get; init; } = PresentFeedbackMode.Automatic;

    /// <summary>
    /// Know exactly when a present has released its semaphores (swapchain_maintenance1 present fences). Without them a
    /// rebuild idles the presenter's queue instead, which the presenter only does once all of its own work has finished.
    /// </summary>
    public bool UsePresentFences { get; init; } = true;

    /// <summary>
    /// Ask for the time each present's first pixel went out (VK_EXT_present_timing past presentation timing, on
    /// <c>CLOCK_MONOTONIC</c>), where the device, surface and driver offer it. The presenter reports it in
    /// <see cref="Core.Abstractions.PresentFeedback.ActualTimeNs"/>; the completion that present-wait reports is the
    /// time the driver learnt of it, which on this desktop lands up to a few milliseconds after the refresh and moves
    /// with the compositor's own scheduling.
    /// </summary>
    public bool PresentTiming { get; init; } = true;
}

/// <summary>
/// A window's swapchain: FIFO (fixed refresh) presentation on the surface of an <see cref="SdlWindow"/>. Recreated on
/// resize, out-of-date and suboptimal; shows nothing while the window has no pixels (minimised) or is occluded. With
/// VK_KHR_present_wait2 the presenter caps presents still waiting to be shown; with swapchain_maintenance1, present
/// fences make swapchain and semaphore teardown exact. <see cref="SwapchainTargetOptions"/> can switch either off.
/// <para>The window must outlive this target, and the target its presenter.</para>
/// </summary>
public sealed unsafe class SwapchainTarget : PresentTarget
{
    private readonly SdlWindow _window;
    private readonly VulkanInstance _instance;
    private readonly PresentFeedbackMode _feedback;
    private readonly PresentFeedbackMode _ids;
    private readonly bool _maintenance1;
    private readonly VkPresentModeKHR _presentMode = VkPresentModeKHR.Fifo;
    private readonly List<(VkFormat Format, VkColorSpaceKHR ColorSpace)> _formats = [];
    private VkSurfaceKHR _surface;
    private VkSwapchainKHR _swapchain;
    private VkSemaphore[] _renderDone = [];
    private VkFence[] _presentFences;
    private bool[] _fenceInUse;
    private VkColorSpaceKHR _colorSpace;
    private OutputRequest _request;
    private bool _outOfDate;
    private bool _disposed;
    private int _builtForWidth = -1;
    private int _builtForHeight = -1;

    // One present fence per presenter frame slot; the presenter has at most two.
    private const int FrameSlots = 2;

    // Past presentation timings read per call, stage times per timing, and reports kept for the presenter.
    private const int TimingBatch = 16;
    private const int TimingStages = 4;
    private const int TimingQueue = 64;

    private readonly bool _timingWanted;
    private readonly VkPresentStageFlagsEXT _timingStage;
    private VkPastPresentationTimingEXT* _past;
    private VkPresentStageTimeEXT* _pastStages;
    private readonly ulong[] _timedIds = new ulong[TimingQueue];
    private readonly long[] _timedNs = new long[TimingQueue];
    private int _timedHead;
    private int _timedCount;
    private bool _timingActive;
    private ulong _timingDomainId;
    private VkTimeDomainKHR _timingDomain;
    private long _timingOffsetNs;
    private long _calibratedAt;
    private const long CalibrateEverySeconds = 1;

    public SwapchainTarget(VulkanDevice device, SdlWindow window, SwapchainTargetOptions? options = null)
        : base(device)
    {
        ArgumentNullException.ThrowIfNull(window);
        options ??= new SwapchainTargetOptions();
        if (!device.Capabilities.Swapchain)
        {
            throw new NotSupportedException("The device was created without VK_KHR_swapchain (instance lacks VK_KHR_surface?).");
        }

        _window = window;
        _instance = device.Instance;
        _surface = new VkSurfaceKHR(window.CreateSurface(_instance));
        _presentFences = new VkFence[FrameSlots];
        _fenceInUse = new bool[FrameSlots];
        try
        {
            VkBool32 supported;
            VulkanException.Check(
                _instance.Api.vkGetPhysicalDeviceSurfaceSupportKHR(device.PhysicalDevice.Handle, device.QueueFamily, _surface, &supported),
                "vkGetPhysicalDeviceSurfaceSupportKHR");
            if (!supported)
            {
                throw new NotSupportedException("The presenter's queue family cannot present to this surface.");
            }

            Surface = QuerySurface();
            _presentMode = options.PresentMode switch
            {
                SwapchainPresentMode.FifoLatestReady => VkPresentModeKHR.FifoLatestReady,
                SwapchainPresentMode.Mailbox => VkPresentModeKHR.Mailbox,
                SwapchainPresentMode.Immediate => VkPresentModeKHR.Immediate,
                _ => VkPresentModeKHR.Fifo,
            };
            if (!Surface.PresentModes.Contains(_presentMode.ToString()) || (_presentMode == VkPresentModeKHR.FifoLatestReady && !device.Capabilities.FifoLatestReady))
            {
                throw new NotSupportedException($"The surface does not offer the {options.PresentMode} present mode (offered: {string.Join(", ", Surface.PresentModes)}).");
            }

            var caps = device.Capabilities;
            var wait2 = caps.PresentWait2 && caps.PresentId2 && Surface.PresentWait2Supported && Surface.PresentId2Supported;
            var wait1 = caps.PresentWait && caps.PresentId;
            if (options.Feedback == PresentFeedbackMode.Automatic && !wait2 && !wait1)
            {
                FeedbackFallbackReason =
                    "Neither VK_KHR_present_wait2 nor VK_KHR_present_wait is available: presents are not capped behind the screen, " +
                    "so latency is not bounded by the presenter.";
            }

            _feedback = options.Feedback switch
            {
                PresentFeedbackMode.Automatic => wait2 ? PresentFeedbackMode.PresentWait2 : wait1 ? PresentFeedbackMode.PresentWait : PresentFeedbackMode.None,
                PresentFeedbackMode.PresentWait2 when wait2 => PresentFeedbackMode.PresentWait2,
                PresentFeedbackMode.PresentWait when wait1 => PresentFeedbackMode.PresentWait,
                PresentFeedbackMode.None => PresentFeedbackMode.None,
                _ => throw new NotSupportedException($"{options.Feedback} is not available on this device and surface."),
            };

            // Present ids are attached even without feedback when available, so diagnostics can tell when a present
            // was shown; the presenter only gates on them when _feedback says so.
            _ids = _feedback != PresentFeedbackMode.None ? _feedback
                : wait2 ? PresentFeedbackMode.PresentWait2 : wait1 ? PresentFeedbackMode.PresentWait : PresentFeedbackMode.None;
            _maintenance1 = caps.SwapchainMaintenance1 && options.UsePresentFences;

            // First-pixel times on CLOCK_MONOTONIC need present ids (present_id2) and a surface that reports a pixel stage.
            var stages = (VkPresentStageFlagsEXT)Surface.PresentStageQueryBits;
            _timingStage = (stages & VkPresentStageFlagsEXT.ImageFirstPixelOut) != 0 ? VkPresentStageFlagsEXT.ImageFirstPixelOut
                : (stages & VkPresentStageFlagsEXT.ImageFirstPixelVisible) != 0 ? VkPresentStageFlagsEXT.ImageFirstPixelVisible
                : VkPresentStageFlagsEXT.None;
            _timingWanted = options.PresentTiming && caps.PresentTiming && Surface.PresentTimingSupported
                && _ids == PresentFeedbackMode.PresentWait2 && _timingStage != VkPresentStageFlagsEXT.None;
            if (_timingWanted)
            {
                _past = (VkPastPresentationTimingEXT*)NativeMemory.AllocZeroed((nuint)(TimingBatch * sizeof(VkPastPresentationTimingEXT)));
                _pastStages = (VkPresentStageTimeEXT*)NativeMemory.AllocZeroed((nuint)(TimingBatch * TimingStages * sizeof(VkPresentStageTimeEXT)));
            }
            if (_maintenance1)
            {
                for (var i = 0; i < FrameSlots; i++)
                {
                    var info = new VkFenceCreateInfo();
                    VkFence fence;
                    VulkanException.Check(device.Api.vkCreateFence(&info, &fence), "vkCreateFence");
                    _presentFences[i] = fence;
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public SurfaceReport Surface { get; } = new();

    /// <summary>
    /// Why first-pixel times are not reported, or null when they are (VK_EXT_present_timing on <c>CLOCK_MONOTONIC</c> for
    /// the current swapchain).
    /// </summary>
    public string? PresentTimingUnavailableReason { get; private set; }

    /// <summary>The present stage whose time is reported (first pixel out, else first pixel visible).</summary>
    public string PresentTimingStage => _timingStage.ToString();

    public override FrameRate RefreshRate => _window.RefreshRate;

    public override bool RefreshRateKnown => _window.RefreshRateKnown;

    /// <summary>Images in the current swapchain (the driver may create more than the 3 asked for).</summary>
    public int ImageCountInUse => ImageCount;

    /// <summary>The feedback the presenter caps queued presents with (never <see cref="PresentFeedbackMode.Automatic"/>).</summary>
    public PresentFeedbackMode ActiveFeedback => _feedback;

    /// <summary>
    /// Why <see cref="PresentFeedbackMode.Automatic"/> ended up with no feedback (null when it found some, or when no
    /// feedback was asked for). When set, presents are not capped behind the screen and the presenter reports
    /// <c>Capabilities.MaxFramesInFlight</c> = 0; show it to the user.
    /// </summary>
    public string? FeedbackFallbackReason { get; }

    /// <summary>Swapchain teardown is tracked with present fences (otherwise by idling the queue).</summary>
    public bool UsesPresentFences => _maintenance1;

    public override bool SupportsHdr10 => Surface.Hdr10St2084;

    /// <summary>The swapchain's present mode, as the Vulkan enum name.</summary>
    public string PresentModeName => _presentMode.ToString();

    /// <summary>The swapchain's colour space, as the Vulkan enum name.</summary>
    public string SwapchainColorSpaceName => _colorSpace.ToString();

    /// <summary>The swapchain's format, as the Vulkan enum name.</summary>
    public string SwapchainFormatName => Format.ToString();

    internal override bool UsesAcquireSemaphore => true;

    private int WantedWidth => _window.IsMinimized ? 0 : _window.PixelWidth;

    private int WantedHeight => _window.IsMinimized ? 0 : _window.PixelHeight;

    internal override bool SupportsPresentWait => _feedback != PresentFeedbackMode.None;

    internal override bool ReportsPresentTimes => _timingActive;

    internal override bool CanCapQueuedPresents => _feedback != PresentFeedbackMode.None;

    internal override bool WantsZeroSize => WantedWidth <= 0 || WantedHeight <= 0;

    internal override bool IsOccluded => _window.IsOccluded;

    internal override bool NeedsIdleQueueForPresents => !_maintenance1;

    /// <summary>Test hook: the next rebuild fails as if vkCreateSwapchainKHR had.</summary>
    internal bool FailNextRecreate { get; set; }

    internal override bool IsFrameSlotReady(int frameSlot) =>
        !_maintenance1 || !_fenceInUse[frameSlot] || Device.Api.vkGetFenceStatus(_presentFences[frameSlot]) == VkResult.Success;

    internal override bool PresentsComplete()
    {
        for (var i = 0; i < _fenceInUse.Length; i++)
        {
            if (!IsFrameSlotReady(i))
            {
                return false;
            }
        }

        return true;
    }

    internal override void OnPresentCompleted() => _window.NotePresentShown();

    /// <summary>Diagnostics: has present <paramref name="presentId"/> been shown? Needs present ids; false without them.</summary>
    internal bool DiagnosticPresentShown(ulong presentId) =>
        _ids != PresentFeedbackMode.None && !_swapchain.IsNull && WaitForPresent(presentId) == VkResult.Success;

    private VkResult WaitForPresent(ulong presentId)
    {
        if (_ids == PresentFeedbackMode.PresentWait)
        {
            return Device.Api.vkWaitForPresentKHR(_swapchain, presentId, 0);
        }

        var info = new VkPresentWait2InfoKHR { presentId = presentId, timeout = 0 };
        return Device.Api.vkWaitForPresent2KHR(_swapchain, &info);
    }

    internal override void Configure(in OutputRequest request)
    {
        _request = request;
        (Format, _colorSpace) = ChooseFormat(request);
        ColorSpace = _colorSpace == VkColorSpaceKHR.Hdr10St2084EXT ? TargetColorSpace.Hdr10St2084 : TargetColorSpace.Sdr;
        Recreate();
    }

    internal override VkFormat FormatFor(in OutputRequest request) => ChooseFormat(request).Format;

    internal override void UpdateHdrMetadata(in HdrMetadata metadata)
    {
        _request = _request with { Metadata = metadata };
        if (!_swapchain.IsNull)
        {
            SendHdrMetadata(_swapchain);
        }
    }

    /// <summary>
    /// VK_EXT_hdr_metadata for an HDR10 swapchain: the request's mastering display and light levels. Called for every new
    /// HDR10 swapchain (the metadata belongs to the swapchain) and when the content's metadata changes.
    /// </summary>
    private void SendHdrMetadata(VkSwapchainKHR swapchain)
    {
        HdrMetadataSent = null;
        HdrMetadataUnavailableReason = null;
        if (_colorSpace != VkColorSpaceKHR.Hdr10St2084EXT)
        {
            return;
        }

        if (!Device.Capabilities.HdrMetadata)
        {
            HdrMetadataUnavailableReason = "the device has no VK_EXT_hdr_metadata: the compositor assumes the PQ range's defaults";
            return;
        }

        var metadata = _request.Metadata.ToVulkan();
        Device.Api.vkSetHdrMetadataEXT(1, &swapchain, &metadata);
        HdrMetadataSent = HdrMetadata.FromVulkan(metadata);
        HdrMetadataCalls++;
    }

    // Compared with the window size the swapchain was last built for (not its extent, which the surface may clamp),
    // so a minimised window (wanted size 0) is rebuilt once, not on every frame.
    internal override bool NeedsRecreate() =>
        _outOfDate || WantedWidth != _builtForWidth || WantedHeight != _builtForHeight;

    internal override void Recreate()
    {
        var old = _swapchain;
        var width = WantedWidth;
        var height = WantedHeight;
        DestroyImageResources();
        var caps = width > 0 && height > 0 ? SurfaceCapabilities() : default;
        uint w = (uint)Math.Max(0, width);
        uint h = (uint)Math.Max(0, height);
        if (w > 0 && h > 0)
        {
            if (caps.currentExtent.width != uint.MaxValue)
            {
                w = caps.currentExtent.width;
                h = caps.currentExtent.height;
            }

            w = Math.Clamp(w, caps.minImageExtent.width, caps.maxImageExtent.width);
            h = Math.Clamp(h, caps.minImageExtent.height, caps.maxImageExtent.height);
        }

        if (w == 0 || h == 0)
        {
            DestroySwapchain(old);
            _swapchain = default;
            Width = 0;
            Height = 0;
            Version++;
            Commit(width, height);
            return;
        }

        try
        {
            CreateSwapchain(old, caps, w, h);
        }
        catch
        {
            // A failed vkCreateSwapchainKHR still retires the old swapchain. Show nothing and keep NeedsRecreate true
            // (the size and out-of-date state are only committed on success), so the next request tries again.
            DestroyImageResources();
            if (_swapchain.Handle != old.Handle)
            {
                DestroySwapchain(_swapchain);
            }

            DestroySwapchain(old);
            _swapchain = default;
            Width = 0;
            Height = 0;
            Version++;
            throw;
        }

        DestroySwapchain(old);
        Commit(width, height);
    }

    private void Commit(int width, int height)
    {
        _builtForWidth = width;
        _builtForHeight = height;
        _outOfDate = false;
    }

    private void CreateSwapchain(VkSwapchainKHR old, VkSurfaceCapabilitiesKHR caps, uint w, uint h)
    {
        if (FailNextRecreate)
        {
            FailNextRecreate = false;
            throw new VulkanException(VkResult.ErrorInitializationFailed, "vkCreateSwapchainKHR (injected)");
        }

        var api = Device.Api;

        // One image on screen, up to two queued behind it: three lets the presenter keep two in flight without
        // waiting for an image, and without present-wait it is what bounds the queue.
        var count = Math.Max(caps.minImageCount, 3u);
        if (caps.maxImageCount != 0)
        {
            count = Math.Min(count, caps.maxImageCount);
        }

        var flags = _ids == PresentFeedbackMode.PresentWait2
            ? VkSwapchainCreateFlagsKHR.PresentId2 | VkSwapchainCreateFlagsKHR.PresentWait2
            : VkSwapchainCreateFlagsKHR.None;
        if (_timingWanted)
        {
            flags |= VkSwapchainCreateFlagsKHR.PresentTimingEXT;
        }
        var info = new VkSwapchainCreateInfoKHR
        {
            flags = flags,
            surface = _surface,
            minImageCount = count,
            imageFormat = Format,
            imageColorSpace = _colorSpace,
            imageExtent = new VkExtent2D(w, h),
            imageArrayLayers = 1,
            imageUsage = VkImageUsageFlags.ColorAttachment,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = caps.currentTransform,
            compositeAlpha = (caps.supportedCompositeAlpha & VkCompositeAlphaFlagsKHR.Opaque) != 0
                ? VkCompositeAlphaFlagsKHR.Opaque
                : FirstBit(caps.supportedCompositeAlpha),
            presentMode = _presentMode,
            clipped = true,
            oldSwapchain = old,
        };
        VkSwapchainKHR swapchain;
        VulkanException.Check(api.vkCreateSwapchainKHR(&info, &swapchain), "vkCreateSwapchainKHR");
        _swapchain = swapchain;

        uint imageCount;
        VulkanException.Check(api.vkGetSwapchainImagesKHR(swapchain, &imageCount, null), "vkGetSwapchainImagesKHR");
        if (Images.Length != imageCount)
        {
            // Only when the count changes: a rebuild during playback (a resize) allocates nothing.
            Images = new VkImage[imageCount];
            Views = new VkImageView[imageCount];
            _renderDone = new VkSemaphore[imageCount];
        }

        fixed (VkImage* p = Images)
        {
            VulkanException.Check(api.vkGetSwapchainImagesKHR(swapchain, &imageCount, p), "vkGetSwapchainImagesKHR");
        }

        for (var i = 0; i < imageCount; i++)
        {
            Views[i] = CreateView(Device, Images[i], Format);
            var semInfo = new VkSemaphoreCreateInfo();
            VkSemaphore semaphore;
            VulkanException.Check(api.vkCreateSemaphore(&semInfo, &semaphore), "vkCreateSemaphore");
            _renderDone[i] = semaphore;
        }

        ImageCount = (int)imageCount;
        Width = (int)w;
        Height = (int)h;
        Version++;
        SetUpPresentTiming(swapchain);
        SendHdrMetadata(swapchain);
    }

    /// <summary>
    /// Past presentation timings for a new swapchain: a queue for the reports and the <c>CLOCK_MONOTONIC</c> time domain.
    /// Anything missing turns them off for this swapchain (feedback falls back to present-wait's completion alone).
    /// </summary>
    private void SetUpPresentTiming(VkSwapchainKHR swapchain)
    {
        _timingActive = false;
        _timedCount = 0;
        _timedHead = 0;
        if (!_timingWanted)
        {
            PresentTimingUnavailableReason = PresentTimingUnavailableReason ?? "VK_EXT_present_timing is not offered for this device and surface (or not asked for)";
            return;
        }

        var api = Device.Api;
        var result = api.vkSetSwapchainPresentTimingQueueSizeEXT(swapchain, TimingQueue);
        if (result != VkResult.Success)
        {
            PresentTimingUnavailableReason = $"vkSetSwapchainPresentTimingQueueSizeEXT: {result}";
            return;
        }

        var props = new VkSwapchainTimeDomainPropertiesEXT();
        ulong counter;
        result = api.vkGetSwapchainTimeDomainPropertiesEXT(swapchain, &props, &counter);
        if (result is not (VkResult.Success or VkResult.Incomplete) || props.timeDomainCount == 0)
        {
            PresentTimingUnavailableReason = $"vkGetSwapchainTimeDomainPropertiesEXT: {result}, {props.timeDomainCount} time domains";
            return;
        }

        var count = Math.Min(props.timeDomainCount, 16u);
        var domains = stackalloc VkTimeDomainKHR[(int)count];
        var ids = stackalloc ulong[(int)count];
        props.timeDomainCount = count;
        props.pTimeDomains = domains;
        props.pTimeDomainIds = ids;
        result = api.vkGetSwapchainTimeDomainPropertiesEXT(swapchain, &props, &counter);
        if (result is not (VkResult.Success or VkResult.Incomplete))
        {
            PresentTimingUnavailableReason = $"vkGetSwapchainTimeDomainPropertiesEXT: {result}";
            return;
        }

        for (var i = 0; i < props.timeDomainCount; i++)
        {
            if (domains[i] == VkTimeDomainKHR.ClockMonotonic)
            {
                _timingDomain = domains[i];
                _timingDomainId = ids[i];
                _timingOffsetNs = 0;
                _timingActive = true;
                PresentTimingUnavailableReason = null;
                return;
            }
        }

        // A domain of the swapchain's own (this driver on Wayland offers only the present stage's): put its times on
        // CLOCK_MONOTONIC through calibrated timestamps, taken now and again every second.
        for (var i = 0; i < props.timeDomainCount; i++)
        {
            if (domains[i] is VkTimeDomainKHR.PresentStageLocalEXT or VkTimeDomainKHR.SwapchainLocalEXT && Device.Capabilities.CalibratedTimestamps)
            {
                _timingDomain = domains[i];
                _timingDomainId = ids[i];
                if (Calibrate(swapchain, out var why))
                {
                    _timingActive = true;
                    PresentTimingUnavailableReason = null;
                    return;
                }

                PresentTimingUnavailableReason = $"present timing's {domains[i]} domain could not be calibrated to CLOCK_MONOTONIC: {why}";
                return;
            }
        }

        var offered = new StringBuilder();
        for (var i = 0; i < props.timeDomainCount; i++)
        {
            offered.Append(offered.Length > 0 ? ", " : "").Append(domains[i].ToString()).Append(" #").Append(ids[i]);
        }

        PresentTimingUnavailableReason = $"the swapchain offers no CLOCK_MONOTONIC time domain for present timing (offered: {offered})";
    }

    /// <summary>The offset from the swapchain's time domain to CLOCK_MONOTONIC (both read at once by the driver).</summary>
    private bool Calibrate(VkSwapchainKHR swapchain, out string? why)
    {
        why = null;
        if (_timingDomain == VkTimeDomainKHR.ClockMonotonic)
        {
            return true;
        }

        var swapchainInfo = new VkSwapchainCalibratedTimestampInfoEXT { swapchain = swapchain, presentStage = _timingStage, timeDomainId = _timingDomainId };
        var infos = stackalloc VkCalibratedTimestampInfoKHR[2];
        infos[0] = new VkCalibratedTimestampInfoKHR { pNext = &swapchainInfo, timeDomain = _timingDomain };
        infos[1] = new VkCalibratedTimestampInfoKHR { timeDomain = VkTimeDomainKHR.ClockMonotonic };
        var stamps = stackalloc ulong[2];
        ulong deviation;
        // The device enabled the KHR or the EXT extension: same signature.
        var api = Device.Api;
        var result = api.vkGetCalibratedTimestampsKHR_ptr.Value != null
            ? api.vkGetCalibratedTimestampsKHR(2, infos, stamps, &deviation)
            : api.vkGetCalibratedTimestampsEXT(2, infos, stamps, &deviation);
        if (result != VkResult.Success)
        {
            why = $"vkGetCalibratedTimestampsKHR: {result}";
            return false;
        }

        // Present-timing values are nanoseconds in every domain.
        _timingOffsetNs = (long)stamps[1] - (long)stamps[0];
        _calibratedAt = Stopwatch.GetTimestamp();
        CalibrationDeviationNs = (long)deviation;
        return true;
    }

    /// <summary>The swapchain time domain's offset to CLOCK_MONOTONIC last calibrated, ns (0 when it is CLOCK_MONOTONIC).</summary>
    public long PresentTimingOffsetNs => _timingOffsetNs;

    /// <summary>The driver's bound on the last calibration's error, ns.</summary>
    public long CalibrationDeviationNs { get; private set; }

    /// <summary>Re-calibrations during playback (one a second, on the present thread) and how long they took, ns.</summary>
    public long Calibrations { get; private set; }

    /// <summary>Complete reports that came without the first-pixel time (the compositor left it out), since creation.</summary>
    public long ReportsWithoutTime { get; private set; }

    public long MaxCalibrationNs { get; private set; }

    public long TotalCalibrationNs { get; private set; }

    internal override bool TryTakePresentTime(ulong presentId, out long timeNs)
    {
        timeNs = 0;
        if (!_timingActive || _swapchain.IsNull)
        {
            return false;
        }

        if (_timingDomain != VkTimeDomainKHR.ClockMonotonic && Stopwatch.GetTimestamp() - _calibratedAt > Stopwatch.Frequency * CalibrateEverySeconds)
        {
            var t0 = Stopwatch.GetTimestamp();
            _ = Calibrate(_swapchain, out _);
            var ns = (Stopwatch.GetTimestamp() - t0) * 1_000_000_000 / Stopwatch.Frequency;
            Calibrations++;
            MaxCalibrationNs = Math.Max(MaxCalibrationNs, ns);
            TotalCalibrationNs += ns;
        }

        // Look in what was read before, then ask the driver for what has come in since.
        if (TakeQueued(presentId, out timeNs))
        {
            return true;
        }

        ReadPastTimings();
        return TakeQueued(presentId, out timeNs);
    }

    internal override bool HasPresentReport(ulong presentId)
    {
        if (!_timingActive || _swapchain.IsNull)
        {
            return false;
        }

        if (!Queued(presentId))
        {
            ReadPastTimings();
        }

        return Queued(presentId);
    }

    private bool Queued(ulong presentId)
    {
        for (var k = 0; k < _timedCount; k++)
        {
            if (_timedIds[(_timedHead + k) % TimingQueue] == presentId)
            {
                return true;
            }
        }

        return false;
    }

    private bool TakeQueued(ulong presentId, out long timeNs)
    {
        while (_timedCount > 0)
        {
            var id = _timedIds[_timedHead];
            if (id > presentId)
            {
                break;
            }

            var ns = _timedNs[_timedHead];
            _timedHead = (_timedHead + 1) % TimingQueue;
            _timedCount--;
            if (id == presentId)
            {
                timeNs = ns;
                return true;
            }
        }

        timeNs = 0;
        return false;
    }

    /// <summary>Moves the driver's completed reports (in present order) into the local queue. Allocation-free.</summary>
    private void ReadPastTimings()
    {
        for (var round = 0; round < 4; round++)
        {
            for (var i = 0; i < TimingBatch; i++)
            {
                _past[i] = new VkPastPresentationTimingEXT
                {
                    presentStageCount = TimingStages,
                    pPresentStages = _pastStages + (i * TimingStages),
                };
            }

            var info = new VkPastPresentationTimingInfoEXT { swapchain = _swapchain };
            var props = new VkPastPresentationTimingPropertiesEXT { presentationTimingCount = TimingBatch, pPresentationTimings = _past };
            var result = Device.Api.vkGetPastPresentationTimingEXT(&info, &props);
            if (result is not (VkResult.Success or VkResult.Incomplete))
            {
                return;
            }

            for (var i = 0; i < props.presentationTimingCount; i++)
            {
                ref var timing = ref _past[i];
                if (timing.reportComplete == VkBool32.False)
                {
                    continue;
                }

                // A complete report without the stage's time (KWin at 240 Hz leaves it out for some presents, in runs of every
                // fourth) is queued with 0: the present's report has come, and no time will.
                var ns = 0L;
                for (var s = 0; s < timing.presentStageCount && s < TimingStages; s++)
                {
                    if (timing.pPresentStages[s].stage == _timingStage && timing.pPresentStages[s].time != 0)
                    {
                        ns = (long)timing.pPresentStages[s].time + _timingOffsetNs;
                        break;
                    }
                }

                ReportsWithoutTime += ns == 0 ? 1 : 0;
                if (_timedCount == TimingQueue)
                {
                    _timedHead = (_timedHead + 1) % TimingQueue;
                    _timedCount--;
                }

                _timedIds[(_timedHead + _timedCount) % TimingQueue] = timing.presentId;
                _timedNs[(_timedHead + _timedCount) % TimingQueue] = ns;
                _timedCount++;
            }

            if (result != VkResult.Incomplete)
            {
                return;
            }
        }
    }

    internal override AcquireOutcome TryAcquire(ulong frame, int frameSlot, VkSemaphore acquireSemaphore, out int imageIndex)
    {
        imageIndex = -1;
        var api = Device.Api;
        if (_maintenance1 && _fenceInUse[frameSlot])
        {
            // The present this slot made last time must have released its semaphores before the slot is reused.
            var status = api.vkGetFenceStatus(_presentFences[frameSlot]);
            if (status == VkResult.NotReady)
            {
                return AcquireOutcome.NotReady;
            }

            VulkanException.Check(status, "vkGetFenceStatus");
            var fence = _presentFences[frameSlot];
            VulkanException.Check(api.vkResetFences(1, &fence), "vkResetFences");
            _fenceInUse[frameSlot] = false;
        }

        uint index;
        var result = api.vkAcquireNextImageKHR(_swapchain, 0, acquireSemaphore, default, &index);
        switch (result)
        {
            case VkResult.Success:
            case VkResult.SuboptimalKHR:
                _outOfDate |= result == VkResult.SuboptimalKHR;
                imageIndex = (int)index;
                return AcquireOutcome.Acquired;
            case VkResult.NotReady:
            case VkResult.Timeout:
                return AcquireOutcome.NotReady;
            case VkResult.ErrorOutOfDateKHR:
                _outOfDate = true;
                return AcquireOutcome.OutOfDate;
            default:
                VulkanException.Check(result, "vkAcquireNextImageKHR");
                return AcquireOutcome.NotReady;
        }
    }

    internal override VkSemaphore RenderDoneSemaphore(int imageIndex) => _renderDone[imageIndex];

    internal override void RecordEpilogue(VkCommandBuffer cmd, int imageIndex, ulong frame)
    {
        var barrier = new VkImageMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.ColorAttachmentOutput,
            srcAccessMask = VkAccessFlags2.ColorAttachmentWrite,
            dstStageMask = VkPipelineStageFlags2.None,
            dstAccessMask = VkAccessFlags2.None,
            oldLayout = VkImageLayout.ColorAttachmentOptimal,
            newLayout = VkImageLayout.PresentSrcKHR,
            srcQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            image = Images[imageIndex],
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        var dep = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &barrier };
        Device.Api.vkCmdPipelineBarrier2(cmd, &dep);
    }

    internal override PresentOutcome Present(int imageIndex, int frameSlot, ulong presentId)
    {
        var swapchain = _swapchain;
        var index = (uint)imageIndex;
        var wait = _renderDone[imageIndex];
        var id = presentId;
        var fence = _maintenance1 ? _presentFences[frameSlot] : default;
        var fenceInfo = new VkSwapchainPresentFenceInfoKHR { swapchainCount = 1, pFences = &fence };
        var idInfo = new VkPresentId2KHR { swapchainCount = 1, pPresentIds = &id };
        var idInfo1 = new VkPresentIdKHR { swapchainCount = 1, pPresentIds = &id };
        void* chain = null;
        if (_ids == PresentFeedbackMode.PresentWait2)
        {
            idInfo.pNext = chain;
            chain = &idInfo;
        }
        else if (_ids == PresentFeedbackMode.PresentWait)
        {
            idInfo1.pNext = chain;
            chain = &idInfo1;
        }

        if (_maintenance1)
        {
            fenceInfo.pNext = chain;
            chain = &fenceInfo;
        }

        // Feedback only (this surface cannot be asked for a present time): report when the first pixel went out.
        var timingInfo = new VkPresentTimingInfoEXT { timeDomainId = _timingDomainId, presentStageQueries = _timingStage };
        var timings = new VkPresentTimingsInfoEXT { swapchainCount = 1, pTimingInfos = &timingInfo };
        if (_timingActive)
        {
            timings.pNext = chain;
            chain = &timings;
        }

        var info = new VkPresentInfoKHR
        {
            pNext = chain,
            waitSemaphoreCount = 1,
            pWaitSemaphores = &wait,
            swapchainCount = 1,
            pSwapchains = &swapchain,
            pImageIndices = &index,
        };
        var result = Device.Api.vkQueuePresentKHR(Device.Queue, &info);
        if (_maintenance1)
        {
            _fenceInUse[frameSlot] = true;
        }

        switch (result)
        {
            case VkResult.Success:
                return PresentOutcome.Presented;
            case VkResult.SuboptimalKHR:
                _outOfDate = true;
                return PresentOutcome.Suboptimal;
            case VkResult.ErrorOutOfDateKHR:
                _outOfDate = true;
                return PresentOutcome.OutOfDate;
            default:
                VulkanException.Check(result, "vkQueuePresentKHR");
                return PresentOutcome.Presented;
        }
    }

    internal override bool PollPresentComplete(ulong presentId)
    {
        if (_swapchain.IsNull)
        {
            return true;
        }

        var result = WaitForPresent(presentId);
        switch (result)
        {
            case VkResult.Success:
                return true;
            case VkResult.Timeout:
                return false;
            case VkResult.ErrorOutOfDateKHR:
            case VkResult.SuboptimalKHR:
                // That present will never be reported; stop waiting for it and rebuild.
                _outOfDate = true;
                return true;
            default:
                VulkanException.Check(result, "vkWaitForPresent");
                return true;
        }
    }

    internal override void WaitForPresents(long timeoutNs)
    {
        var api = Device.Api;
        if (_maintenance1)
        {
            for (var i = 0; i < _presentFences.Length; i++)
            {
                if (!_fenceInUse[i])
                {
                    continue;
                }

                var fence = _presentFences[i];
                var result = api.vkWaitForFences(1, &fence, true, (ulong)Math.Max(0, timeoutNs));
                VulkanException.Check(result, "vkWaitForFences");
                if (result == VkResult.Success)
                {
                    VulkanException.Check(api.vkResetFences(1, &fence), "vkResetFences");
                    _fenceInUse[i] = false;
                }
            }
        }
        else
        {
            // Without present fences nothing reports when a present has consumed its wait semaphore; an idle queue
            // is the closest the core API offers. The presenter calls this only once everything it queued has
            // finished (NeedsIdleQueueForPresents), so the queue holds nothing that waits on the producer and the
            // wait is bounded by the presents themselves.
            VulkanException.Check(api.vkQueueWaitIdle(Device.Queue), "vkQueueWaitIdle");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!Device.IsDisposed)
        {
            WaitForPresents(2_000_000_000);
            DestroyImageResources();
            DestroySwapchain(_swapchain);
            _swapchain = default;
            foreach (var fence in _presentFences)
            {
                if (!fence.IsNull)
                {
                    Device.Api.vkDestroyFence(fence);
                }
            }

            _presentFences = [];
            _fenceInUse = [];
        }

        if (!_surface.IsNull && !_instance.IsDisposed)
        {
            SdlWindow.DestroySurface(_instance, _surface.Handle);
            _surface = default;
        }

        NativeMemory.Free(_past);
        _past = null;
        NativeMemory.Free(_pastStages);
        _pastStages = null;
        _timingActive = false;
    }

    private static VkCompositeAlphaFlagsKHR FirstBit(VkCompositeAlphaFlagsKHR flags) =>
        (VkCompositeAlphaFlagsKHR)((uint)flags & (uint)-(int)flags);

    private static readonly VkFormat[] Hdr10Formats = [VkFormat.A2B10G10R10UnormPack32, VkFormat.A2R10G10B10UnormPack32];

    // SDR: UNORM (not _SRGB) formats, so encoded values are written as they are. A 10-bit format for 10-bit content keeps
    // its precision (10-bit SDR from a P010 capture, or the placeholder's output).
    private static readonly VkFormat[] SdrTenBitFormats = [VkFormat.A2B10G10R10UnormPack32, VkFormat.A2R10G10B10UnormPack32, VkFormat.B8G8R8A8Unorm, VkFormat.R8G8B8A8Unorm];
    private static readonly VkFormat[] SdrEightBitFormats = [VkFormat.B8G8R8A8Unorm, VkFormat.R8G8B8A8Unorm, VkFormat.A2B10G10R10UnormPack32];

    /// <summary>Allocation-free (a switch between SDR and HDR chooses again on the present thread).</summary>
    private (VkFormat Format, VkColorSpaceKHR ColorSpace) ChooseFormat(in OutputRequest request)
    {
        if (request.PreferHdr10)
        {
            foreach (var f in Hdr10Formats)
            {
                if (Has(f, VkColorSpaceKHR.Hdr10St2084EXT))
                {
                    return (f, VkColorSpaceKHR.Hdr10St2084EXT);
                }
            }
        }

        foreach (var f in request.TenBitContent ? SdrTenBitFormats : SdrEightBitFormats)
        {
            if (Has(f, VkColorSpaceKHR.SrgbNonLinear))
            {
                return (f, VkColorSpaceKHR.SrgbNonLinear);
            }
        }

        throw new NotSupportedException("The surface offers no UNORM format in the sRGB colour space.");
    }

    private bool Has(VkFormat format, VkColorSpaceKHR colorSpace)
    {
        foreach (var f in _formats)
        {
            if (f.Format == format && f.ColorSpace == colorSpace)
            {
                return true;
            }
        }

        return false;
    }

    private VkSurfaceCapabilitiesKHR SurfaceCapabilities()
    {
        VkSurfaceCapabilitiesKHR caps;
        VulkanException.Check(
            _instance.Api.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(Device.PhysicalDevice.Handle, _surface, &caps),
            "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        return caps;
    }

    private SurfaceReport QuerySurface()
    {
        var iapi = _instance.Api;
        var phys = Device.PhysicalDevice.Handle;
        uint count;
        VulkanException.Check(iapi.vkGetPhysicalDeviceSurfaceFormatsKHR(phys, _surface, &count, null), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        var formats = new VkSurfaceFormatKHR[count];
        fixed (VkSurfaceFormatKHR* p = formats)
        {
            VulkanException.Check(iapi.vkGetPhysicalDeviceSurfaceFormatsKHR(phys, _surface, &count, p), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        }

        var infos = new List<SurfaceFormatInfo>();
        var hdr = false;
        foreach (var f in formats)
        {
            _formats.Add((f.format, f.colorSpace));
            infos.Add(new SurfaceFormatInfo(f.format.ToString(), f.colorSpace.ToString()));
            hdr |= f.colorSpace == VkColorSpaceKHR.Hdr10St2084EXT
                && f.format is VkFormat.A2B10G10R10UnormPack32 or VkFormat.A2R10G10B10UnormPack32;
        }

        uint modeCount;
        VulkanException.Check(iapi.vkGetPhysicalDeviceSurfacePresentModesKHR(phys, _surface, &modeCount, null), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        var modes = new VkPresentModeKHR[modeCount];
        fixed (VkPresentModeKHR* p = modes)
        {
            VulkanException.Check(iapi.vkGetPhysicalDeviceSurfacePresentModesKHR(phys, _surface, &modeCount, p), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        }

        bool id2 = false, wait2 = false, timing = false, absolute = false, relative = false;
        var stages = VkPresentStageFlagsEXT.None;
        if (_instance.HasExtension("VK_KHR_get_surface_capabilities2"))
        {
            var caps = Device.Capabilities;
            var idCaps = new VkSurfaceCapabilitiesPresentId2KHR();
            var waitCaps = new VkSurfaceCapabilitiesPresentWait2KHR();
            var timingCaps = new VkPresentTimingSurfaceCapabilitiesEXT();
            var caps2 = new VkSurfaceCapabilities2KHR();
            void* chain = null;
            if (caps.PresentId2)
            {
                idCaps.pNext = chain;
                chain = &idCaps;
            }

            if (caps.PresentWait2)
            {
                waitCaps.pNext = chain;
                chain = &waitCaps;
            }

            if (caps.PresentTiming)
            {
                timingCaps.pNext = chain;
                chain = &timingCaps;
            }

            caps2.pNext = chain;
            var surfaceInfo = new VkPhysicalDeviceSurfaceInfo2KHR { surface = _surface };
            VulkanException.Check(iapi.vkGetPhysicalDeviceSurfaceCapabilities2KHR(phys, &surfaceInfo, &caps2), "vkGetPhysicalDeviceSurfaceCapabilities2KHR");
            id2 = caps.PresentId2 && idCaps.presentId2Supported;
            wait2 = caps.PresentWait2 && waitCaps.presentWait2Supported;
            timing = caps.PresentTiming && timingCaps.presentTimingSupported;
            absolute = caps.PresentTiming && timingCaps.presentAtAbsoluteTimeSupported;
            relative = caps.PresentTiming && timingCaps.presentAtRelativeTimeSupported;
            stages = caps.PresentTiming ? timingCaps.presentStageQueries : VkPresentStageFlagsEXT.None;
        }

        var basic = SurfaceCapabilities();
        return new SurfaceReport
        {
            Formats = infos,
            PresentModes = [.. modes.Select(static m => m.ToString())],
            Hdr10St2084 = hdr,
            PresentId2Supported = id2,
            PresentWait2Supported = wait2,
            PresentTimingSupported = timing,
            PresentAtAbsoluteTimeSupported = absolute,
            PresentAtRelativeTimeSupported = relative,
            PresentStageQueries = stages.ToString(),
            PresentStageQueryBits = (uint)stages,
            MinImageCount = basic.minImageCount,
            MaxImageCount = basic.maxImageCount,
        };
    }

    private void DestroyImageResources()
    {
        // The arrays stay for the next swapchain (CreateSwapchain); only the handles go.
        var api = Device.Api;
        for (var i = 0; i < Views.Length; i++)
        {
            if (!Views[i].IsNull)
            {
                api.vkDestroyImageView(Views[i]);
            }

            if (!_renderDone[i].IsNull)
            {
                api.vkDestroySemaphore(_renderDone[i]);
            }

            Views[i] = default;
            _renderDone[i] = default;
            Images[i] = default;
        }

        ImageCount = 0;
    }

    private void DestroySwapchain(VkSwapchainKHR swapchain)
    {
        if (!swapchain.IsNull)
        {
            Device.Api.vkDestroySwapchainKHR(swapchain);
        }
    }
}
