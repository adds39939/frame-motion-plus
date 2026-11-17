using FrameMotionPlus.Vulkan.Windowing;

// GPU tests share one device, a process-wide loader policy and (for Display) the user's desktop; run them one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace FrameMotionPlus.Vulkan.Tests.Support;

/// <summary>Test categories. Hardware tests skip, never fail, when their requirement is missing.</summary>
public static class Category
{
    public const string Name = "Category";

    /// <summary>Needs a Vulkan NVIDIA device and the validation layer.</summary>
    public const string Gpu = "Gpu";

    /// <summary>Needs a Wayland session and SDL3 as well.</summary>
    public const string Display = "Display";
}

public static class Requirements
{
    private static readonly Lazy<string?> GpuMissing = new(ProbeGpu);

    public static void SkipUnlessGpu()
    {
        if (GpuMissing.Value is { } reason)
        {
            Assert.Skip(reason);
        }
    }

    public static void SkipUnlessDisplay()
    {
        SkipUnlessGpu();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Assert.Skip("no Wayland session (WAYLAND_DISPLAY is not set)");
        }

        if (!SdlWindow.IsLibraryAvailable())
        {
            Assert.Skip("SDL3 (libSDL3.so.0) is not installed");
        }
    }

    private static string? ProbeGpu()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "GPU tests need Linux (opaque FD interop)";
        }

        try
        {
            // Before anything else touches the loader, so no implicit layer of the user's session is ever involved.
            VulkanLayerPolicy.Isolated.ApplyToProcess();
            if (!VulkanLoader.TryInitialize(out var error))
            {
                return error;
            }

            if (!VulkanInstance.EnumerateLayers().Contains(VulkanInstance.ValidationLayerName))
            {
                return "the Vulkan validation layer is not installed";
            }

            using var instance = VulkanInstance.Create(new VulkanInstanceOptions());
            return instance.PhysicalDevices.Any(static d => d.IsNvidia) ? null : "no NVIDIA Vulkan device";
        }
        catch (Exception e)
        {
            // Whatever a machine without a usable loader or driver throws, it means "skip", never "fail".
            return $"Vulkan unavailable: {e.GetType().Name}: {e.Message}";
        }
    }
}
