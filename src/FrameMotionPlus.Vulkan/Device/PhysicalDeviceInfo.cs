using System.Globalization;
using FrameMotionPlus.Vulkan.Interop;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace FrameMotionPlus.Vulkan;

public enum PhysicalDeviceKind
{
    Other = 0,
    IntegratedGpu = 1,
    DiscreteGpu = 2,
    VirtualGpu = 3,
    Cpu = 4,
}

/// <summary>What selection needs to know about a physical device.</summary>
public sealed unsafe class PhysicalDeviceInfo
{
    public const uint NvidiaVendorId = 0x10DE;

    private PhysicalDeviceInfo(VkPhysicalDevice handle) => Handle = handle;

    public string Name { get; private init; } = string.Empty;

    public DeviceUuid Uuid { get; private init; }

    public DeviceUuid DriverUuid { get; private init; }

    public uint VendorId { get; private init; }

    public uint DeviceId { get; private init; }

    public PhysicalDeviceKind Kind { get; private init; }

    public uint ApiVersion { get; private init; }

    public uint DriverVersion { get; private init; }

    public string DriverName { get; private init; } = string.Empty;

    public string DriverInfo { get; private init; } = string.Empty;

    public bool IsNvidia => VendorId == NvidiaVendorId;

    internal VkPhysicalDevice Handle { get; }

    /// <summary>NVIDIA packs the driver version as 10.8.8.6 bits (e.g. 620.70.06); others use the Vulkan packing.</summary>
    public string DriverVersionText => IsNvidia
        ? string.Create(CultureInfo.InvariantCulture, $"{DriverVersion >> 22}.{(DriverVersion >> 14) & 0xFF}.{(DriverVersion >> 6) & 0xFF:00}")
        : new VkVersion(DriverVersion).ToString();

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name} [{Kind}, vendor 0x{VendorId:X4}, driver {DriverVersionText}, UUID {Uuid}]");

    internal static IReadOnlyList<PhysicalDeviceInfo> Enumerate(VulkanInstance instance)
    {
        var api = instance.Api;
        uint count;
        VulkanException.Check(api.vkEnumeratePhysicalDevices(&count, null), "vkEnumeratePhysicalDevices");
        var handles = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* p = handles)
        {
            VulkanException.Check(api.vkEnumeratePhysicalDevices(&count, p), "vkEnumeratePhysicalDevices");
        }

        var list = new List<PhysicalDeviceInfo>((int)count);
        for (var i = 0; i < count; i++)
        {
            list.Add(Describe(api, handles[i]));
        }

        return list;
    }

    private static PhysicalDeviceInfo Describe(VkInstanceApi api, VkPhysicalDevice handle)
    {
        var driver = new VkPhysicalDeviceDriverProperties();
        var id = new VkPhysicalDeviceIDProperties { pNext = &driver };
        var props = new VkPhysicalDeviceProperties2 { pNext = &id };
        api.vkGetPhysicalDeviceProperties2(handle, &props);
        var p = props.properties;
        return new PhysicalDeviceInfo(handle)
        {
            Name = Utf8.FromFixed(props.properties.deviceName, (int)VK_MAX_PHYSICAL_DEVICE_NAME_SIZE),
            Uuid = DeviceUuid.FromBytes(new ReadOnlySpan<byte>(id.deviceUUID, DeviceUuid.Size)),
            DriverUuid = DeviceUuid.FromBytes(new ReadOnlySpan<byte>(id.driverUUID, DeviceUuid.Size)),
            VendorId = p.vendorID,
            DeviceId = p.deviceID,
            Kind = p.deviceType switch
            {
                VkPhysicalDeviceType.IntegratedGpu => PhysicalDeviceKind.IntegratedGpu,
                VkPhysicalDeviceType.DiscreteGpu => PhysicalDeviceKind.DiscreteGpu,
                VkPhysicalDeviceType.VirtualGpu => PhysicalDeviceKind.VirtualGpu,
                VkPhysicalDeviceType.Cpu => PhysicalDeviceKind.Cpu,
                _ => PhysicalDeviceKind.Other,
            },
            ApiVersion = p.apiVersion,
            DriverVersion = p.driverVersion,
            DriverName = Utf8.FromFixed(driver.driverName, (int)VK_MAX_DRIVER_NAME_SIZE),
            DriverInfo = Utf8.FromFixed(driver.driverInfo, (int)VK_MAX_DRIVER_INFO_SIZE),
        };
    }
}

/// <summary>How <see cref="VulkanDevice.Create"/> picks a physical device.</summary>
public readonly record struct DeviceSelector
{
    private DeviceSelector(DeviceUuid uuid, bool byUuid)
    {
        Uuid = uuid;
        IsByUuid = byUuid;
    }

    /// <summary>The device the selection must match (e.g. CUDA's <c>cuDeviceGetUuid</c>), when <see cref="IsByUuid"/>.</summary>
    public DeviceUuid Uuid { get; }

    public bool IsByUuid { get; }

    /// <summary>The physical device whose <c>deviceUUID</c> equals <paramref name="uuid"/>; anything else fails.</summary>
    public static DeviceSelector ByUuid(DeviceUuid uuid) => new(uuid, true);

    /// <summary>The first discrete GPU in enumeration order (standalone use, no CUDA device to match).</summary>
    public static DeviceSelector FirstDiscreteGpu => new(default, false);

    internal PhysicalDeviceInfo Select(IReadOnlyList<PhysicalDeviceInfo> devices)
    {
        foreach (var d in devices)
        {
            if (IsByUuid ? d.Uuid == Uuid : d.Kind == PhysicalDeviceKind.DiscreteGpu)
            {
                return d;
            }
        }

        var listing = devices.Count == 0 ? " (none)" : string.Concat(devices.Select(static d => "\n  " + d));
        throw new VulkanDeviceNotFoundException(IsByUuid
            ? $"No Vulkan physical device has UUID {Uuid}. Devices present:{listing}"
            : $"No discrete GPU found. Devices present:{listing}");
    }

    public override string ToString() => IsByUuid ? $"device UUID {Uuid}" : "first discrete GPU";
}
