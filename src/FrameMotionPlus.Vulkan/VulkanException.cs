using Vortice.Vulkan;

namespace FrameMotionPlus.Vulkan;

/// <summary>A Vulkan call returned an error code.</summary>
public sealed class VulkanException : Exception
{
    public VulkanException()
    {
    }

    public VulkanException(string message)
        : base(message)
    {
    }

    public VulkanException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VulkanException(VkResult result, string call)
        : base($"{call} failed: {result}")
    {
        Result = result;
    }

    public VkResult Result { get; }

    /// <summary>Throws for error codes (negative); success and status codes (SUBOPTIMAL, TIMEOUT, ...) pass.</summary>
    internal static void Check(VkResult result, string call)
    {
        if (result < 0)
        {
            Throw(result, call);
        }
    }

    private static void Throw(VkResult result, string call) => throw new VulkanException(result, call);
}

/// <summary>No physical device matched the requested selection.</summary>
public sealed class VulkanDeviceNotFoundException : Exception
{
    public VulkanDeviceNotFoundException()
    {
    }

    public VulkanDeviceNotFoundException(string message)
        : base(message)
    {
    }

    public VulkanDeviceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
