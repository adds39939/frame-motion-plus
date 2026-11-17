using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Vulkan;
using FrameMotionPlus.Vulkan.Windowing;

// GPU tests share one GPU, a process-wide loader policy, the user's desktop (Display) and exact per-thread allocation
// counters; run them one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace FrameMotionPlus.Gpu.Tests.Support;

/// <summary>Test categories. Hardware tests skip, never fail, when their requirement is missing.</summary>
public static class Category
{
    public const string Name = "Category";

    /// <summary>Needs Linux, the CUDA driver with an NVIDIA device, and a Vulkan NVIDIA device with the validation layer.</summary>
    public const string Gpu = "Gpu";

    /// <summary>Needs a Wayland session and SDL3 as well.</summary>
    public const string Display = "Display";
}

/// <summary>
/// The hardware gate. Probed once: CUDA (driver library, cuInit, a device), Vulkan (loader, validation layer, the
/// physical device with the CUDA device's UUID). Anything missing or throwing means "skip", never "fail".
/// </summary>
internal static class Hardware
{
    private static readonly Lazy<(CudaEnvironment? Cuda, string? Reason)> _probe = new(Probe);

    /// <summary>Skips unless the GPU interop requirements are met; returns the shared CUDA environment.</summary>
    public static CudaEnvironment RequireGpu()
    {
        var (cuda, reason) = _probe.Value;
        if (cuda is null)
        {
            Assert.Skip(reason ?? "GPU unavailable");
        }

        return cuda;
    }

    public static CudaEnvironment RequireDisplay()
    {
        var cuda = RequireGpu();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Assert.Skip("no Wayland session (WAYLAND_DISPLAY is not set)");
        }

        if (!SdlWindow.IsLibraryAvailable())
        {
            Assert.Skip("SDL3 (libSDL3.so.0) is not installed");
        }

        return cuda;
    }

    private static (CudaEnvironment?, string?) Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return (null, "GPU interop tests need Linux (opaque FD interop)");
        }

        try
        {
            if (!CudaDriver.TryInitialize(out var reason))
            {
                return (null, reason);
            }

            if (CudaDriver.DeviceCount == 0)
            {
                return (null, "no CUDA device");
            }

            // Before anything else touches the loader, so no implicit layer of the user's session is ever involved.
            VulkanLayerPolicy.Isolated.ApplyToProcess();
            if (!VulkanLoader.TryInitialize(out var error))
            {
                return (null, error);
            }

            var uuid = GpuIdentity.UuidOf(CudaDriver.GetDevice(0));
            VulkanInstance instance;
            try
            {
                // Throws when VK_LAYER_KHRONOS_validation is not installed; every test here runs under it.
                instance = VulkanInstance.Create(new VulkanInstanceOptions { EnableValidation = true, OnValidationMessage = Interop.EchoValidation });
            }
            catch (VulkanException e)
            {
                return (null, e.Message);
            }

            using (instance)
            {
                if (!instance.PhysicalDevices.Any(d => d.Uuid == uuid))
                {
                    return (null, $"no Vulkan device has the CUDA device's UUID {uuid}");
                }
            }

            return (new CudaEnvironment(), null);
        }
        catch (Exception e)
        {
            return (null, $"GPU unavailable: {e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>The process-wide CUDA context and kernels; never disposed (process exit releases them).</summary>
internal sealed class CudaEnvironment
{
    public CudaEnvironment()
    {
        Context = CudaContext.RetainPrimary(0);
        Kernels = ImageKernels.Load(Context);
        Uuid = GpuIdentity.UuidOf(Context.Device);
    }

    public CudaContext Context { get; }

    public ImageKernels Kernels { get; }

    public DeviceUuid Uuid { get; }
}
