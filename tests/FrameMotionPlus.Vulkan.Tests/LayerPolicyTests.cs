using FrameMotionPlus.Vulkan.Interop;

namespace FrameMotionPlus.Vulkan.Tests;

/// <summary>
/// The layer policy lives in the native process environment: re-applying it writes nothing, switching undoes the
/// previous policy's variables, and Inherit gives back the environment the process started with. Each test ends with
/// the isolated policy applied again, which is what every GPU test runs under.
/// </summary>
public class LayerPolicyTests
{
    private static readonly string[] LoaderVariables =
        ["VK_LOADER_LAYERS_DISABLE", "VK_LOADER_LAYERS_ALLOW", "VK_LOADER_LAYERS_ENABLE", "VK_INSTANCE_LAYERS"];

    [Fact]
    public void Applying_the_same_policy_again_writes_nothing()
    {
        VulkanLayerPolicy.Isolated.ApplyToProcess();
        var writes = NativeEnvironment.Writes;
        VulkanLayerPolicy.Isolated.ApplyToProcess();
        (VulkanLayerPolicy.Isolated with { }).ApplyToProcess();
        Assert.Equal(writes, NativeEnvironment.Writes);
        Assert.Equal("~implicit~", NativeEnvironment.Get("VK_LOADER_LAYERS_DISABLE"));
        Assert.Null(NativeEnvironment.Get("VK_INSTANCE_LAYERS"));
        Assert.Null(NativeEnvironment.Get("VK_LOADER_LAYERS_ENABLE"));
    }

    [Fact]
    public void Inherit_restores_the_original_loader_variables()
    {
        try
        {
            VulkanLayerPolicy.Isolated.ApplyToProcess();
            VulkanLayerPolicy.Inherit.ApplyToProcess();
            foreach (var name in LoaderVariables)
            {
                Assert.Equal(VulkanLayerPolicy.OriginalValue(name), NativeEnvironment.Get(name));
                Assert.Equal(VulkanLayerPolicy.OriginalValue(name), Environment.GetEnvironmentVariable(name));
            }
        }
        finally
        {
            VulkanLayerPolicy.Isolated.ApplyToProcess();
        }

        Assert.Equal("~implicit~", NativeEnvironment.Get("VK_LOADER_LAYERS_DISABLE"));
    }

    [Fact]
    public void Switching_policies_undoes_the_previous_policys_variables()
    {
        var original = VulkanLayerPolicy.OriginalValue("NVPRESENT_ENABLE_SMOOTH_MOTION");
        try
        {
            VulkanLayerPolicy.NvidiaSmoothMotion.ApplyToProcess();
            Assert.Equal("1", NativeEnvironment.Get("NVPRESENT_ENABLE_SMOOTH_MOTION"));
            Assert.Equal("VK_LAYER_NV_present", NativeEnvironment.Get("VK_LOADER_LAYERS_ALLOW"));

            var custom = new VulkanLayerPolicy { Environment = [new("FMP_POLICY_TEST", "a")] };
            custom.ApplyToProcess();
            Assert.Equal(original, NativeEnvironment.Get("NVPRESENT_ENABLE_SMOOTH_MOTION"));
            Assert.Null(NativeEnvironment.Get("VK_LOADER_LAYERS_ALLOW"));
            Assert.Equal("a", NativeEnvironment.Get("FMP_POLICY_TEST"));
        }
        finally
        {
            VulkanLayerPolicy.Isolated.ApplyToProcess();
        }

        Assert.Null(NativeEnvironment.Get("FMP_POLICY_TEST"));
        Assert.Equal(original, NativeEnvironment.Get("NVPRESENT_ENABLE_SMOOTH_MOTION"));
    }
}
