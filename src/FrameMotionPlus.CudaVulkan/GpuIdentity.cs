using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vulkan;

namespace FrameMotionPlus.CudaVulkan;

/// <summary>
/// Matches the CUDA device and the Vulkan physical device by their 16-byte device UUID (design doc, "Streams and
/// devices"): CUDA's <c>cuDeviceGetUuid</c> and Vulkan's <c>VkPhysicalDeviceIDProperties.deviceUUID</c> report the
/// same bytes for the same GPU. Compared as raw bytes, so no <see cref="Guid"/> byte reordering is involved.
/// </summary>
public static class GpuIdentity
{
    /// <summary>The CUDA device's UUID as a Vulkan <see cref="DeviceUuid"/> (byte for byte).</summary>
    public static DeviceUuid UuidOf(CudaDevice device)
    {
        Span<byte> bytes = stackalloc byte[DeviceUuid.Size];
        device.CopyUuid(bytes);
        return DeviceUuid.FromBytes(bytes);
    }

    /// <summary>Selects the Vulkan physical device that is <paramref name="device"/>, and nothing else.</summary>
    public static DeviceSelector VulkanSelectorFor(CudaDevice device) => DeviceSelector.ByUuid(UuidOf(device));

    /// <summary>Throws <see cref="GpuDeviceMismatchException"/> unless both UUIDs name the same GPU.</summary>
    public static void EnsureSameDevice(DeviceUuid cuda, DeviceUuid vulkan, string what)
    {
        if (cuda != vulkan || cuda.IsEmpty)
        {
            throw new GpuDeviceMismatchException(cuda, vulkan, what);
        }
    }
}

/// <summary>
/// The CUDA context and the Vulkan objects are on different physical GPUs (or a UUID is missing). Interop memory and
/// semaphores can only be shared within one GPU, so nothing was imported.
/// </summary>
public sealed class GpuDeviceMismatchException : InvalidOperationException
{
    public GpuDeviceMismatchException()
    {
    }

    public GpuDeviceMismatchException(string message)
        : base(message)
    {
    }

    public GpuDeviceMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal GpuDeviceMismatchException(DeviceUuid cuda, DeviceUuid vulkan, string what)
        : base($"{what} is on Vulkan device {vulkan} but the CUDA context is on device {cuda}: CUDA and Vulkan must use the " +
               "same physical GPU (create the Vulkan device with GpuIdentity.VulkanSelectorFor(cudaDevice)). Nothing was imported.")
    {
        CudaUuid = cuda;
        VulkanUuid = vulkan;
    }

    public DeviceUuid CudaUuid { get; }

    public DeviceUuid VulkanUuid { get; }
}
