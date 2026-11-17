using Vortice.Vulkan;

namespace FrameMotionPlus.Vulkan;

/// <summary>Loads the system Vulkan loader (libvulkan.so.1 / vulkan-1.dll) once per process.</summary>
public static class VulkanLoader
{
    private static readonly Lock InitLock = new();
    private static VkResult _result = VkResult.ErrorInitializationFailed;
    private static bool _attempted;

    /// <summary>True when the loader library is present and its entry points resolved.</summary>
    public static bool TryInitialize(out string? error)
    {
        lock (InitLock)
        {
            if (!_attempted)
            {
                _attempted = true;
                try
                {
                    _result = Vortice.Vulkan.Vulkan.vkInitialize();
                }
                catch (DllNotFoundException e)
                {
                    _result = VkResult.ErrorInitializationFailed;
                    error = e.Message;
                    return false;
                }
            }

            error = _result == VkResult.Success ? null : $"Vulkan loader not available ({_result})";
            return _result == VkResult.Success;
        }
    }

    internal static void EnsureInitialized()
    {
        if (!TryInitialize(out var error))
        {
            throw new VulkanException(error ?? "Vulkan loader not available");
        }
    }
}
