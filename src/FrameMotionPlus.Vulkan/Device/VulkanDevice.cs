using FrameMotionPlus.Vulkan.Interop;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace FrameMotionPlus.Vulkan;

/// <summary>
/// A logical device on the selected physical device with one graphics/compute queue. External memory and semaphore
/// FD export are required (the presentation ring needs them); the presentation extensions are enabled when present
/// and reported in <see cref="Capabilities"/>.
/// <para>The queue is externally synchronised: one thread (the present thread) submits to it.</para>
/// </summary>
public sealed unsafe class VulkanDevice : IDisposable
{
    private VkDevice _handle;
    private VkPhysicalDeviceMemoryProperties _memory;

    private VulkanDevice(VulkanInstance instance, PhysicalDeviceInfo physical)
    {
        Instance = instance;
        PhysicalDevice = physical;
    }

    public VulkanInstance Instance { get; }

    public PhysicalDeviceInfo PhysicalDevice { get; }

    public VulkanCapabilities Capabilities { get; private set; } = null!;

    public bool IsDisposed => _handle.IsNull;

    internal VkDevice Handle => _handle;

    internal VkDeviceApi Api { get; private set; } = null!;

    internal VkQueue Queue { get; private set; }

    internal uint QueueFamily { get; private set; }

    internal bool SwapchainMaintenance1IsKhr { get; private set; }

    public static VulkanDevice Create(VulkanInstance instance, DeviceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ObjectDisposedException.ThrowIf(instance.IsDisposed, instance);
        var physical = selector.Select(instance.PhysicalDevices);
        var device = new VulkanDevice(instance, physical);
        device.CreateCore();
        return device;
    }

    private void CreateCore()
    {
        var iapi = Instance.Api;
        var phys = PhysicalDevice.Handle;
        var available = EnumerateDeviceExtensions(iapi, phys);
        foreach (var required in (string[])["VK_KHR_external_memory_fd", "VK_KHR_external_semaphore_fd"])
        {
            if (!available.Contains(required))
            {
                throw new VulkanException($"{PhysicalDevice.Name} lacks {required}, which the presentation ring needs.");
            }
        }

        var enabled = new List<string> { "VK_KHR_external_memory_fd", "VK_KHR_external_semaphore_fd" };
        bool Enable(string name, bool condition = true)
        {
            if (condition && available.Contains(name))
            {
                enabled.Add(name);
                return true;
            }

            return false;
        }

        var surface = Instance.HasExtension("VK_KHR_surface");
        var surfaceCaps2 = Instance.HasExtension("VK_KHR_get_surface_capabilities2");
        var swapchain = Enable("VK_KHR_swapchain", surface);
        var hdrMetadata = Enable("VK_EXT_hdr_metadata", swapchain);
        var maintenance1Khr = Enable("VK_KHR_swapchain_maintenance1", swapchain && Instance.HasExtension("VK_KHR_surface_maintenance1"));
        var maintenance1Ext = !maintenance1Khr
            && Enable("VK_EXT_swapchain_maintenance1", swapchain && Instance.HasExtension("VK_EXT_surface_maintenance1"));
        var fifoLatestKhr = Enable("VK_KHR_present_mode_fifo_latest_ready", swapchain);
        var fifoLatestExt = !fifoLatestKhr && Enable("VK_EXT_present_mode_fifo_latest_ready", swapchain);
        var presentId2 = Enable("VK_KHR_present_id2", swapchain && surfaceCaps2);
        var presentWait2 = Enable("VK_KHR_present_wait2", presentId2);

        // The older present-wait pair: the fallback for capping queued presents where present_wait2 is missing.
        var presentId1 = Enable("VK_KHR_present_id", swapchain);
        var presentWait1 = Enable("VK_KHR_present_wait", presentId1);
        var calibrated = Enable("VK_KHR_calibrated_timestamps") || Enable("VK_EXT_calibrated_timestamps");
        var presentTiming = Enable("VK_EXT_present_timing", presentId2 && calibrated && surfaceCaps2);

        // Query the features of everything enabled, then switch on what is supported.
        var f12 = new VkPhysicalDeviceVulkan12Features();
        var f13 = new VkPhysicalDeviceVulkan13Features();
        var fId2 = new VkPhysicalDevicePresentId2FeaturesKHR();
        var fWait2 = new VkPhysicalDevicePresentWait2FeaturesKHR();
        var fMaint1 = new VkPhysicalDeviceSwapchainMaintenance1FeaturesKHR();
        var fFifo = new VkPhysicalDevicePresentModeFifoLatestReadyFeaturesKHR();
        var fTiming = new VkPhysicalDevicePresentTimingFeaturesEXT();
        var fId1 = new VkPhysicalDevicePresentIdFeaturesKHR();
        var fWait1 = new VkPhysicalDevicePresentWaitFeaturesKHR();
        var features = new VkPhysicalDeviceFeatures2();
        f12.pNext = &f13;
        features.pNext = &f12;
        void* tail = &f13;
        void Chain(void* next)
        {
            ((VkBaseOutStructure*)tail)->pNext = (VkBaseOutStructure*)next;
            tail = next;
        }

        if (presentId2)
        {
            Chain(&fId2);
        }

        if (presentWait2)
        {
            Chain(&fWait2);
        }

        if (maintenance1Khr || maintenance1Ext)
        {
            // The EXT feature struct is an alias of the KHR one (same layout and sType value).
            Chain(&fMaint1);
        }

        if (fifoLatestKhr || fifoLatestExt)
        {
            Chain(&fFifo);
        }

        if (presentTiming)
        {
            Chain(&fTiming);
        }

        if (presentId1)
        {
            Chain(&fId1);
        }

        if (presentWait1)
        {
            Chain(&fWait1);
        }

        iapi.vkGetPhysicalDeviceFeatures2(phys, &features);
        if (!f12.timelineSemaphore || !f13.synchronization2 || !f13.dynamicRendering)
        {
            throw new VulkanException($"{PhysicalDevice.Name} lacks timeline semaphores, synchronization2 or dynamic rendering.");
        }

        // Enable exactly: the required core features, plus whatever optional feature bits were reported.
        var e12 = new VkPhysicalDeviceVulkan12Features { timelineSemaphore = true };
        var e13 = new VkPhysicalDeviceVulkan13Features { synchronization2 = true, dynamicRendering = true };
        e12.pNext = &e13;
        var enableFeatures = new VkPhysicalDeviceFeatures2 { pNext = &e12 };
        fId2.pNext = null;
        fWait2.pNext = null;
        fMaint1.pNext = null;
        fFifo.pNext = null;
        fTiming.pNext = null;
        fId1.pNext = null;
        fWait1.pNext = null;
        tail = &e13;
        if (presentId2)
        {
            Chain(&fId2);
        }

        if (presentWait2)
        {
            Chain(&fWait2);
        }

        if (maintenance1Khr || maintenance1Ext)
        {
            Chain(&fMaint1);
        }

        if (fifoLatestKhr || fifoLatestExt)
        {
            Chain(&fFifo);
        }

        if (presentTiming)
        {
            Chain(&fTiming);
        }

        if (presentId1)
        {
            Chain(&fId1);
        }

        if (presentWait1)
        {
            Chain(&fWait1);
        }

        QueueFamily = FindQueueFamily(iapi, phys);
        var priority = 1.0f;
        var queueInfo = new VkDeviceQueueCreateInfo { queueFamilyIndex = QueueFamily, queueCount = 1, pQueuePriorities = &priority };
        using var names = new Utf8StringList(enabled);
        var createInfo = new VkDeviceCreateInfo
        {
            pNext = &enableFeatures,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueInfo,
            enabledExtensionCount = names.Count,
            ppEnabledExtensionNames = names.Pointer,
        };
        VkDevice handle;
        VulkanException.Check(iapi.vkCreateDevice(phys, &createInfo, &handle), "vkCreateDevice");
        _handle = handle;
        Api = GetApi(Instance.Handle, handle);
        VkQueue queue;
        Api.vkGetDeviceQueue(QueueFamily, 0, &queue);
        Queue = queue;
        SwapchainMaintenance1IsKhr = maintenance1Khr;

        VkPhysicalDeviceMemoryProperties memory;
        iapi.vkGetPhysicalDeviceMemoryProperties(phys, &memory);
        _memory = memory;

        VkPhysicalDeviceProperties props;
        iapi.vkGetPhysicalDeviceProperties(phys, &props);
        uint familyCount;
        iapi.vkGetPhysicalDeviceQueueFamilyProperties(phys, &familyCount, null);
        var families = stackalloc VkQueueFamilyProperties[(int)familyCount];
        iapi.vkGetPhysicalDeviceQueueFamilyProperties(phys, &familyCount, families);

        Capabilities = new VulkanCapabilities
        {
            Device = PhysicalDevice,
            Swapchain = swapchain,
            PresentId2 = presentId2 && fId2.presentId2,
            PresentWait2 = presentWait2 && fWait2.presentWait2,
            PresentId = presentId1 && fId1.presentId,
            PresentWait = presentWait1 && fId1.presentId && fWait1.presentWait,
            PresentTiming = presentTiming && fTiming.presentTiming,
            PresentAtAbsoluteTime = presentTiming && fTiming.presentAtAbsoluteTime,
            PresentAtRelativeTime = presentTiming && fTiming.presentAtRelativeTime,
            FifoLatestReady = (fifoLatestKhr || fifoLatestExt) && fFifo.presentModeFifoLatestReady,
            HdrMetadata = hdrMetadata,
            SwapchainMaintenance1 = (maintenance1Khr || maintenance1Ext) && fMaint1.swapchainMaintenance1,
            CalibratedTimestamps = calibrated,
            SwapchainColorspace = Instance.HasExtension("VK_EXT_swapchain_colorspace"),
            Display = Instance.HasExtension("VK_KHR_display"),
            AcquireDrmDisplay = Instance.HasExtension("VK_EXT_acquire_drm_display"),
            TimestampPeriodNs = props.limits.timestampPeriod,
            TimestampValidBits = families[QueueFamily].timestampValidBits,
            DeviceExtensions = [.. enabled.Order(StringComparer.Ordinal)],
        };
    }

    /// <summary>Blocks until the device is idle. Not for the hot path.</summary>
    public void WaitIdle()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        VulkanException.Check(Api.vkDeviceWaitIdle(), "vkDeviceWaitIdle");
    }

    public void Dispose()
    {
        if (_handle.IsNull)
        {
            return;
        }

        _ = Api.vkDeviceWaitIdle();
        Api.vkDestroyDevice();
        _handle = default;
    }

    internal uint FindMemoryType(uint typeBits, VkMemoryPropertyFlags required, VkMemoryPropertyFlags avoid = VkMemoryPropertyFlags.None)
    {
        var fallback = uint.MaxValue;
        for (uint i = 0; i < _memory.memoryTypeCount; i++)
        {
            var flags = _memory.memoryTypes[(int)i].propertyFlags;
            if ((typeBits & (1u << (int)i)) == 0 || (flags & required) != required)
            {
                continue;
            }

            if ((flags & avoid) == 0)
            {
                return i;
            }

            fallback = fallback == uint.MaxValue ? i : fallback;
        }

        return fallback != uint.MaxValue
            ? fallback
            : throw new VulkanException($"No memory type with {required} in type bits 0x{typeBits:X}.");
    }

    internal VkMemoryPropertyFlags MemoryTypeFlags(uint index) => _memory.memoryTypes[(int)index].propertyFlags;

    private static uint FindQueueFamily(VkInstanceApi api, VkPhysicalDevice phys)
    {
        uint count;
        api.vkGetPhysicalDeviceQueueFamilyProperties(phys, &count, null);
        var families = stackalloc VkQueueFamilyProperties[(int)count];
        api.vkGetPhysicalDeviceQueueFamilyProperties(phys, &count, families);
        const VkQueueFlags Needed = VkQueueFlags.Graphics | VkQueueFlags.Compute;
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].queueFlags & Needed) == Needed && families[i].timestampValidBits > 0)
            {
                return i;
            }
        }

        throw new VulkanException("No queue family supports graphics, compute and timestamps.");
    }

    private static HashSet<string> EnumerateDeviceExtensions(VkInstanceApi api, VkPhysicalDevice phys)
    {
        uint count;
        VulkanException.Check(api.vkEnumerateDeviceExtensionProperties(phys, null, &count, null), "vkEnumerateDeviceExtensionProperties");
        var props = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* p = props)
        {
            VulkanException.Check(api.vkEnumerateDeviceExtensionProperties(phys, null, &count, p), "vkEnumerateDeviceExtensionProperties");
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            fixed (VkExtensionProperties* p = &props[i])
            {
                set.Add(Utf8.FromFixed(p->extensionName, (int)VK_MAX_EXTENSION_NAME_SIZE));
            }
        }

        return set;
    }
}
