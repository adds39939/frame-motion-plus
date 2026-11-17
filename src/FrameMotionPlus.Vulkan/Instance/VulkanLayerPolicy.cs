using FrameMotionPlus.Vulkan.Interop;

namespace FrameMotionPlus.Vulkan;

public enum ImplicitLayerMode
{
    /// <summary>
    /// No implicit layer loads unless it is in <see cref="VulkanLayerPolicy.AllowedImplicitLayers"/> (and its own enable
    /// variable is set). Layers forced on through <c>VK_INSTANCE_LAYERS</c> or <c>VK_LOADER_LAYERS_ENABLE</c> in the
    /// inherited environment are dropped too. Overlays and frame generators that hook every Vulkan app (MangoHud,
    /// Lossless Scaling, gamescope WSI, DLSS injection, NVIDIA's present layer) stay out of our presents.
    /// </summary>
    Isolated = 0,

    /// <summary>The loader's normal behaviour: whatever the user's environment and manifests enable.</summary>
    Inherit = 1,
}

/// <summary>
/// Which Vulkan layers the presenter's instance gets. The policy works through the loader's environment variables
/// (<c>VK_LOADER_LAYERS_DISABLE</c>, <c>VK_LOADER_LAYERS_ALLOW</c>, and it clears <c>VK_LOADER_LAYERS_ENABLE</c> and
/// <c>VK_INSTANCE_LAYERS</c> while isolated), so it lives in the native process environment.
/// <para>
/// Apply it once, at startup, before the first Vulkan call (SDL creating a Vulkan window counts) and before other
/// threads exist: changing the environment races with <c>getenv</c> on other threads (unsafe on older glibc), and child
/// processes inherit what is set. Switching policy later is supported (a Smooth Motion instance in Phase 4 needs a
/// different one) but should happen while nothing else reads the environment. Each application writes only the
/// variables whose value changes, undoes the previous policy's <see cref="Environment"/> entries, and
/// <see cref="ImplicitLayerMode.Inherit"/> restores the values the process had when a policy was first applied.
/// </para>
/// <para>
/// Later phases deliberately enable two implicit layers: NVIDIA Smooth Motion (<c>VK_LAYER_NV_present</c> with
/// <c>NVPRESENT_ENABLE_SMOOTH_MOTION=1</c>, see <see cref="NvidiaSmoothMotion"/>) and possibly <c>VK_hdr_layer</c>
/// (<c>VK_LAYER_hdr_wsi</c> with <c>ENABLE_HDR_WSI=1</c>). Both are an allow-list entry plus the layer's own variable.
/// Switching either on or off means a new instance, because layers are chosen when it is created.
/// </para>
/// </summary>
public sealed record VulkanLayerPolicy
{
    private static readonly Lock ApplyLock = new();
    private static readonly string[] LoaderVariables =
        ["VK_LOADER_LAYERS_DISABLE", "VK_LOADER_LAYERS_ALLOW", "VK_LOADER_LAYERS_ENABLE", "VK_INSTANCE_LAYERS"];

    // Values the process had before any policy touched them, and what each managed variable holds now.
    private static readonly Dictionary<string, string?> Original = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string?> Current = new(StringComparer.Ordinal);
    private static VulkanLayerPolicy? _applied;

    public ImplicitLayerMode ImplicitLayers { get; init; } = ImplicitLayerMode.Isolated;

    /// <summary>Implicit layers exempt from <see cref="ImplicitLayerMode.Isolated"/>. Each still needs its enable variable.</summary>
    public IReadOnlyList<string> AllowedImplicitLayers { get; init; } = [];

    /// <summary>
    /// Variables set while this policy is applied, e.g. a layer's enable variable (a null value unsets it). Applying
    /// another policy puts them back to what they were before any policy set them.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string?>> Environment { get; init; } = [];

    /// <summary>All implicit layers off; only the layers the presenter requests itself (validation) load.</summary>
    public static VulkanLayerPolicy Isolated { get; } = new();

    /// <summary>Whatever the system and user environment enable (the process's original loader variables).</summary>
    public static VulkanLayerPolicy Inherit { get; } = new() { ImplicitLayers = ImplicitLayerMode.Inherit };

    /// <summary>Isolated except for NVIDIA's present layer with Smooth Motion switched on (Phase 4 presenter mode).</summary>
    public static VulkanLayerPolicy NvidiaSmoothMotion { get; } = new()
    {
        AllowedImplicitLayers = ["VK_LAYER_NV_present"],
        Environment = [new("NVPRESENT_ENABLE_SMOOTH_MOTION", "1")],
    };

    /// <summary>The loader messages this policy itself causes (one per layer it filters out). Not problems.</summary>
    internal static bool IsFilterNotice(string text) =>
        text.Contains("forced disabled because name matches filter of env var", StringComparison.Ordinal);

    /// <summary>The value <paramref name="name"/> had before any policy changed it (tests).</summary>
    internal static string? OriginalValue(string name)
    {
        lock (ApplyLock)
        {
            return Remember(name);
        }
    }

    /// <summary>Writes the policy into the native process environment. <see cref="VulkanInstance.Create"/> calls this.</summary>
    public void ApplyToProcess()
    {
        lock (ApplyLock)
        {
            if (_applied is { } applied && applied.SameAs(this))
            {
                return;
            }

            var wanted = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var name in LoaderVariables)
            {
                wanted[name] = Remember(name);
            }

            if (_applied is not null)
            {
                foreach (var (name, _) in _applied.Environment)
                {
                    wanted[name] = Remember(name);
                }
            }

            if (ImplicitLayers == ImplicitLayerMode.Isolated)
            {
                wanted["VK_LOADER_LAYERS_DISABLE"] = "~implicit~";
                wanted["VK_LOADER_LAYERS_ALLOW"] = AllowedImplicitLayers.Count == 0 ? null : string.Join(',', AllowedImplicitLayers);
                wanted["VK_LOADER_LAYERS_ENABLE"] = null;
                wanted["VK_INSTANCE_LAYERS"] = null;
            }

            foreach (var (name, value) in Environment)
            {
                Remember(name);
                wanted[name] = value;
            }

            foreach (var (name, value) in wanted)
            {
                if (Current[name] != value)
                {
                    NativeEnvironment.Set(name, value);
                    Current[name] = value;
                }
            }

            _applied = this;
        }
    }

    private static string? Remember(string name)
    {
        if (!Original.TryGetValue(name, out var value))
        {
            value = NativeEnvironment.Get(name);
            Original[name] = value;
            Current[name] = value;
        }

        return value;
    }

    private bool SameAs(VulkanLayerPolicy other) =>
        ImplicitLayers == other.ImplicitLayers
        && AllowedImplicitLayers.SequenceEqual(other.AllowedImplicitLayers, StringComparer.Ordinal)
        && Environment.SequenceEqual(other.Environment);
}
