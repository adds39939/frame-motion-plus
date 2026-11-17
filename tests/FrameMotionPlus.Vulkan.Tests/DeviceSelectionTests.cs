using FrameMotionPlus.Vulkan.Tests.Support;

namespace FrameMotionPlus.Vulkan.Tests;

public class DeviceSelectionTests
{
    [Fact]
    public void Device_uuid_parses_and_prints_in_byte_order()
    {
        var uuid = DeviceUuid.Parse("624c61e8-563c-9d37-8e16-60031217e270");
        var bytes = uuid.ToArray();
        Assert.Equal(0x62, bytes[0]);
        Assert.Equal(0x4c, bytes[1]);
        Assert.Equal(0x70, bytes[15]);
        Assert.Equal("624c61e8-563c-9d37-8e16-60031217e270", uuid.ToString());
        Assert.Equal(uuid, DeviceUuid.Parse("GPU-624C61E8563C9D378E1660031217E270"));
        Assert.Equal(uuid, DeviceUuid.FromBytes(bytes));
        Assert.False(DeviceUuid.TryParse("624c61e8-563c-9d37-8e16", out _));
        Assert.False(DeviceUuid.TryParse("624c61e8-563c-9d37-8e16-60031217e27000", out _));
        Assert.False(DeviceUuid.TryParse("zz4c61e8-563c-9d37-8e16-60031217e270", out _));
        Assert.True(default(DeviceUuid).IsEmpty);
    }

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Selects_the_device_by_uuid_and_reports_capabilities() => Gpu.Run(ctx =>
    {
        foreach (var d in ctx.Instance.PhysicalDevices)
        {
            Gpu.WriteLine(d.ToString());
        }

        var nvidia = ctx.Nvidia;
        using var device = VulkanDevice.Create(ctx.Instance, DeviceSelector.ByUuid(nvidia.Uuid));
        Assert.Equal(nvidia.Uuid, device.PhysicalDevice.Uuid);
        Assert.False(device.PhysicalDevice.Uuid.IsEmpty);

        var caps = device.Capabilities;
        Gpu.WriteLine(caps.ToString());
        Assert.Contains("VK_KHR_external_memory_fd", caps.DeviceExtensions);
        Assert.Contains("VK_KHR_external_semaphore_fd", caps.DeviceExtensions);
        Assert.True(caps.Swapchain, "the instance has VK_KHR_surface, so the swapchain extension should be on");
        Assert.True(caps.TimestampPeriodNs > 0);

        // Each optional capability is the conjunction of the extension and its feature; a flag never claims more.
        Assert.Equal(caps.PresentTiming, caps.DeviceExtensions.Contains("VK_EXT_present_timing") && caps.PresentTiming);
        Assert.True(!caps.PresentWait2 || caps.PresentId2);
        Assert.True(!caps.PresentTiming || caps.PresentId2);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_wrong_uuid_fails_naming_the_uuid_and_the_devices_present() => Gpu.Run(ctx =>
    {
        var wrong = DeviceUuid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var e = Assert.Throws<VulkanDeviceNotFoundException>(() => VulkanDevice.Create(ctx.Instance, DeviceSelector.ByUuid(wrong)));
        Gpu.WriteLine(e.Message);
        Assert.Contains("00112233-4455-6677-8899-aabbccddeeff", e.Message, StringComparison.Ordinal);
        Assert.Contains(ctx.Nvidia.Uuid.ToString(), e.Message, StringComparison.Ordinal);
    }, createDevice: false);

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void First_discrete_gpu_is_the_nvidia_card_here() => Gpu.Run(ctx =>
    {
        using var device = VulkanDevice.Create(ctx.Instance, DeviceSelector.FirstDiscreteGpu);
        Assert.Equal(PhysicalDeviceKind.DiscreteGpu, device.PhysicalDevice.Kind);
        Assert.True(device.PhysicalDevice.IsNvidia);
    }, createDevice: false);
}
