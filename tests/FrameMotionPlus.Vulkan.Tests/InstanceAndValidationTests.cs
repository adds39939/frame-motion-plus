using FrameMotionPlus.Vulkan.Tests.Support;
using Vortice.Vulkan;

namespace FrameMotionPlus.Vulkan.Tests;

public class InstanceAndValidationTests
{
    // Libraries of the implicit layers installed on the development machine; none may be mapped into our process.
    private static readonly string[] ImplicitLayerLibraries =
    [
        "liblsfg-vk-layer", "libMangoHud", "libnvidia-present", "libVkLayer_NV_dlssnr", "libVkLayer_FROG_gamescope_wsi",
        "steamoverlayvulkanlayer", "libVkLayer_steam_fossilize",
    ];

    [Fact]
    public void Loader_filter_notices_are_not_problems_but_other_warnings_are()
    {
        var notice = new ValidationMessage(ValidationSeverity.Warning, ValidationMessageKinds.General, 0, "Loader Message",
            "Layer \"VK_LAYER_MANGOHUD_overlay_x86_64\" forced disabled because name matches filter of env var 'VK_LOADER_LAYERS_DISABLE'.");
        var loaderWarning = notice with { Text = "Something else went wrong" };
        var validation = new ValidationMessage(ValidationSeverity.Warning, ValidationMessageKinds.Validation, 42, "VUID-x", "bad");
        var info = validation with { Severity = ValidationSeverity.Info };
        Assert.False(notice.IsProblem);
        Assert.True(loaderWarning.IsProblem);
        Assert.True(validation.IsProblem);
        Assert.False(info.IsProblem);
    }

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Isolated_policy_loads_no_implicit_layer() => Gpu.Run(ctx =>
    {
        Gpu.WriteLine("active layers: " + string.Join(", ", ctx.Instance.ActiveLayers));
        Assert.Equal([VulkanInstance.ValidationLayerName], ctx.Instance.ActiveLayers);

        // And no implicit layer's library gets mapped by creating an instance and device (another test may have
        // loaded NVIDIA's present layer on purpose earlier in this process, so compare before and after).
        var before = File.ReadAllText("/proc/self/maps");
        using var instance = VulkanInstance.Create(new VulkanInstanceOptions { EnableValidation = true });
        using var device = VulkanDevice.Create(instance, DeviceSelector.ByUuid(ctx.Nvidia.Uuid));
        var after = File.ReadAllText("/proc/self/maps");
        foreach (var library in ImplicitLayerLibraries)
        {
            Assert.False(!before.Contains(library, StringComparison.Ordinal) && after.Contains(library, StringComparison.Ordinal), $"{library} was loaded");
        }

        Assert.Equal(0, instance.Validation.ProblemCount);
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void An_allowed_implicit_layer_loads_with_its_enable_variable()
    {
        Requirements.SkipUnlessGpu();
        try
        {
            // Layers the isolated policy filters out are not even enumerated; look under the allow-list policy.
            VulkanLayerPolicy.NvidiaSmoothMotion.ApplyToProcess();
            if (!VulkanInstance.EnumerateLayers().Contains("VK_LAYER_NV_present"))
            {
                Assert.Skip("VK_LAYER_NV_present is not installed");
            }

            using var instance = VulkanInstance.Create(new VulkanInstanceOptions
            {
                Layers = VulkanLayerPolicy.NvidiaSmoothMotion,
                EnableValidation = true,
            });
            Gpu.WriteLine("active layers: " + string.Join(", ", instance.ActiveLayers));
            Assert.Contains("VK_LAYER_NV_present", instance.ActiveLayers);
            Assert.Contains(VulkanInstance.ValidationLayerName, instance.ActiveLayers);
            Assert.Equal(2, instance.ActiveLayers.Count);
            instance.Dispose();
            Assert.True(instance.Validation.ProblemCount == 0, instance.Validation.Describe());
        }
        finally
        {
            // Back to the isolated policy for every later test; switching undoes the Smooth Motion switch.
            VulkanLayerPolicy.Isolated.ApplyToProcess();
            Assert.Equal(VulkanLayerPolicy.OriginalValue("NVPRESENT_ENABLE_SMOOTH_MOTION"), Interop.NativeEnvironment.Get("NVPRESENT_ENABLE_SMOOTH_MOTION"));
        }
    }

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public unsafe void The_harness_sees_a_validation_error() => Gpu.Run(ctx =>
    {
        var info = new VkBufferCreateInfo { size = 0, usage = VkBufferUsageFlags.TransferSrc };
        VkBuffer buffer;
        _ = ctx.Device.Api.vkCreateBuffer(&info, &buffer);
        if (!buffer.IsNull)
        {
            ctx.Device.Api.vkDestroyBuffer(buffer);
        }

        var problems = ctx.Instance.Validation.Problems();
        Assert.Contains(problems, static m => m.Severity == ValidationSeverity.Error && m.MessageIdName.Contains("size", StringComparison.Ordinal));
        ctx.Instance.Validation.Clear();
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public unsafe void The_harness_sees_a_leaked_object_at_device_destruction() => Gpu.Run(ctx =>
    {
        var second = VulkanDevice.Create(ctx.Instance, DeviceSelector.ByUuid(ctx.Nvidia.Uuid));
        var info = new VkBufferCreateInfo { size = 256, usage = VkBufferUsageFlags.TransferSrc };
        VkBuffer leaked;
        VulkanException.Check(second.Api.vkCreateBuffer(&info, &leaked), "vkCreateBuffer");
        second.Dispose();

        var problems = ctx.Instance.Validation.Problems();
        Gpu.WriteLine(ctx.Instance.Validation.Describe());
        Assert.Contains(problems, static m => m.Text.Contains("VkBuffer", StringComparison.Ordinal) && m.Text.Contains("not been destroyed", StringComparison.Ordinal));
        ctx.Instance.Validation.Clear();
    });
}
