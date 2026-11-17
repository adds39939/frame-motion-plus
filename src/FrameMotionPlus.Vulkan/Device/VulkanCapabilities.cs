using System.Globalization;
using System.Text;

namespace FrameMotionPlus.Vulkan;

/// <summary>
/// What the selected device and instance can do, detected at run time. Each flag means the extension is enabled on
/// this device and, where it has a feature bit, the feature is supported and switched on. Later phases branch on
/// these (present timing for pacing, HDR metadata and colour spaces for HDR, FIFO_LATEST_READY for the fallback).
/// Surface-specific support (HDR10 colour space, present-wait on this surface) is in <see cref="SurfaceReport"/>.
/// </summary>
public sealed record VulkanCapabilities
{
    public required PhysicalDeviceInfo Device { get; init; }

    public bool Swapchain { get; init; }

    /// <summary>VK_KHR_present_id2 with <c>presentId2</c>.</summary>
    public bool PresentId2 { get; init; }

    /// <summary>VK_KHR_present_wait2 with <c>presentWait2</c>.</summary>
    public bool PresentWait2 { get; init; }

    /// <summary>VK_KHR_present_id with <c>presentId</c> (the older present-id extension).</summary>
    public bool PresentId { get; init; }

    /// <summary>VK_KHR_present_wait with <c>presentWait</c>: the fallback present feedback where present_wait2 is missing.</summary>
    public bool PresentWait { get; init; }

    /// <summary>VK_EXT_present_timing with <c>presentTiming</c>.</summary>
    public bool PresentTiming { get; init; }

    public bool PresentAtAbsoluteTime { get; init; }

    public bool PresentAtRelativeTime { get; init; }

    /// <summary>VK_KHR_ (or EXT_) present_mode_fifo_latest_ready with its feature.</summary>
    public bool FifoLatestReady { get; init; }

    public bool HdrMetadata { get; init; }

    /// <summary>VK_KHR_ (or EXT_) swapchain_maintenance1 with its feature: present fences, safe swapchain teardown.</summary>
    public bool SwapchainMaintenance1 { get; init; }

    public bool CalibratedTimestamps { get; init; }

    /// <summary>Instance: VK_EXT_swapchain_colorspace (HDR10 ST 2084 and other colour spaces can be offered).</summary>
    public bool SwapchainColorspace { get; init; }

    /// <summary>Instance: VK_KHR_display (direct display, Phase 9).</summary>
    public bool Display { get; init; }

    /// <summary>Instance: VK_EXT_acquire_drm_display (direct display from a DRM lease, Phase 9).</summary>
    public bool AcquireDrmDisplay { get; init; }

    public double TimestampPeriodNs { get; init; }

    public uint TimestampValidBits { get; init; }

    public IReadOnlyList<string> DeviceExtensions { get; init; } = [];

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Device).AppendLine();
        void Flag(string name, bool on) => sb.Append("  ").Append(name).Append(": ").Append(on ? "yes" : "no").AppendLine();
        Flag("swapchain", Swapchain);
        Flag("present_id2", PresentId2);
        Flag("present_wait2", PresentWait2);
        Flag("present_wait (v1)", PresentWait);
        Flag("present_timing", PresentTiming);
        Flag("present at absolute time", PresentAtAbsoluteTime);
        Flag("present at relative time", PresentAtRelativeTime);
        Flag("fifo_latest_ready", FifoLatestReady);
        Flag("hdr_metadata", HdrMetadata);
        Flag("swapchain_maintenance1", SwapchainMaintenance1);
        Flag("calibrated_timestamps", CalibratedTimestamps);
        Flag("swapchain_colorspace", SwapchainColorspace);
        Flag("display", Display);
        Flag("acquire_drm_display", AcquireDrmDisplay);
        sb.Append(CultureInfo.InvariantCulture, $"  timestamp period: {TimestampPeriodNs} ns, valid bits {TimestampValidBits}");
        return sb.ToString();
    }
}
