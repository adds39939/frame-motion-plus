namespace FrameMotionPlus.Vulkan;

public sealed record VulkanInstanceOptions
{
    public string ApplicationName { get; init; } = "Frame Motion Plus";

    public VulkanLayerPolicy Layers { get; init; } = VulkanLayerPolicy.Isolated;

    /// <summary>Loads <c>VK_LAYER_KHRONOS_validation</c> and installs a debug messenger feeding <see cref="VulkanInstance.Validation"/>.</summary>
    public bool EnableValidation { get; init; }

    /// <summary>
    /// Also runs the validation layer's synchronization checks (hazards between commands), including shader accesses
    /// (syncval_shader_accesses_heuristic). Needs <see cref="EnableValidation"/>.
    /// </summary>
    public bool SynchronizationValidation { get; init; }

    /// <summary>Instance extensions the caller cannot do without, e.g. <see cref="Windowing.SdlWindow.RequiredInstanceExtensions"/>.</summary>
    public IReadOnlyList<string> RequiredExtensions { get; init; } = [];

    /// <summary>Optional echo of every validation message (e.g. to a log), called on the thread that made the Vulkan call.</summary>
    public Action<ValidationMessage>? OnValidationMessage { get; init; }
}
