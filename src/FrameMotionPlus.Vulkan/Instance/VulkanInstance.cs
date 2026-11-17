using System.Runtime.InteropServices;
using FrameMotionPlus.Vulkan.Interop;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace FrameMotionPlus.Vulkan;

/// <summary>
/// A Vulkan instance created under an explicit <see cref="VulkanLayerPolicy"/>, optionally with the validation layer
/// and a debug messenger that records into <see cref="Validation"/>. The messenger is also chained into instance
/// creation, so messages from <c>vkCreateInstance</c> and <c>vkDestroyInstance</c> (leaked objects) are recorded.
/// </summary>
public sealed unsafe class VulkanInstance : IDisposable
{
    public const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";

    // Instance creation applies the layer policy to the process environment first; one at a time.
    private static readonly Lock CreateLock = new();

    private static readonly string[] OptionalExtensions =
    [
        "VK_KHR_surface",
        "VK_KHR_get_surface_capabilities2",
        "VK_KHR_surface_maintenance1",
        "VK_EXT_surface_maintenance1",
        "VK_EXT_swapchain_colorspace",
        "VK_KHR_display",
        "VK_EXT_direct_mode_display",
        "VK_EXT_acquire_drm_display",
    ];

    private readonly HashSet<string> _extensions;
    private GCHandle _logHandle;
    private VkInstance _handle;
    private VkDebugUtilsMessengerEXT _messenger;

    private VulkanInstance(
        VkInstance handle,
        VkInstanceApi api,
        HashSet<string> extensions,
        IReadOnlyList<string> layers,
        ValidationLog log,
        GCHandle logHandle,
        bool validation)
    {
        _handle = handle;
        Api = api;
        _extensions = extensions;
        EnabledLayers = layers;
        Validation = log;
        _logHandle = logHandle;
        ValidationEnabled = validation;
        EnabledExtensions = [.. extensions.Order(StringComparer.Ordinal)];
    }

    public ValidationLog Validation { get; }

    public bool ValidationEnabled { get; }

    public IReadOnlyList<string> EnabledExtensions { get; }

    /// <summary>Layers this instance asked for by name (validation). Implicit layers are in <see cref="ActiveLayers"/>.</summary>
    public IReadOnlyList<string> EnabledLayers { get; }

    /// <summary>Every layer the loader activated for this instance, implicit ones included, as the loader reports them.</summary>
    public IReadOnlyList<string> ActiveLayers { get; private set; } = [];

    public IReadOnlyList<PhysicalDeviceInfo> PhysicalDevices { get; private set; } = [];

    public bool IsDisposed => _handle.IsNull;

    internal VkInstance Handle => _handle;

    internal VkInstanceApi Api { get; }

    public bool HasExtension(string name) => _extensions.Contains(name);

    public static VulkanInstance Create(VulkanInstanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (CreateLock)
        {
            options.Layers.ApplyToProcess();
            VulkanLoader.EnsureInitialized();
            return CreateCore(options);
        }
    }

    private static VulkanInstance CreateCore(VulkanInstanceOptions options)
    {
        var available = EnumerateExtensions(null);
        var layers = new List<string>();
        var layerExtensions = new HashSet<string>(StringComparer.Ordinal);
        if (options.EnableValidation)
        {
            if (!EnumerateLayers().Contains(ValidationLayerName))
            {
                throw new VulkanException($"Validation was requested but {ValidationLayerName} is not installed.");
            }

            layers.Add(ValidationLayerName);
            layerExtensions = EnumerateExtensions(ValidationLayerName);
        }

        var enabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in options.RequiredExtensions)
        {
            if (!available.Contains(name))
            {
                throw new VulkanException($"Required instance extension {name} is not available.");
            }

            enabled.Add(name);
        }

        foreach (var name in OptionalExtensions)
        {
            if (available.Contains(name))
            {
                enabled.Add(name);
            }
        }

        // Only one of the two surface_maintenance1 spellings is needed; the KHR one is what swapchain_maintenance1 (KHR) wants.
        if (enabled.Contains("VK_KHR_surface_maintenance1"))
        {
            enabled.Remove("VK_EXT_surface_maintenance1");
        }

        var layerSettings = options.EnableValidation && options.SynchronizationValidation;
        if (options.EnableValidation)
        {
            if (!available.Contains("VK_EXT_debug_utils") && !layerExtensions.Contains("VK_EXT_debug_utils"))
            {
                throw new VulkanException("VK_EXT_debug_utils is not available; cannot collect validation output.");
            }

            enabled.Add("VK_EXT_debug_utils");
            if (layerSettings)
            {
                if (!layerExtensions.Contains("VK_EXT_layer_settings"))
                {
                    throw new VulkanException("Synchronization validation needs VK_EXT_layer_settings from the validation layer.");
                }

                enabled.Add("VK_EXT_layer_settings");
            }
        }

        var log = new ValidationLog();
        if (options.OnValidationMessage is { } echo)
        {
            log.OnMessage = echo;
        }

        var logHandle = GCHandle.Alloc(log);
        try
        {
            using var appName = new Utf8StringList([options.ApplicationName, "FrameMotionPlus"]);
            using var extensionNames = new Utf8StringList([.. enabled]);
            using var layerNames = new Utf8StringList(layers);
            // validate_sync alone does not model shader reads and writes (storage buffers, storage and sampled
            // images); the heuristic setting makes it check those too.
            using var settingStrings = new Utf8StringList([ValidationLayerName, "validate_sync", "syncval_shader_accesses_heuristic"]);

            var appInfo = new VkApplicationInfo
            {
                pApplicationName = appName.Pointer[0],
                applicationVersion = new VkVersion(0, 1, 0),
                pEngineName = appName.Pointer[1],
                engineVersion = new VkVersion(0, 1, 0),
                apiVersion = VkVersion.Version_1_3,
            };

            var messengerInfo = MessengerCreateInfo(logHandle);
            VkBool32 on = true;
            var settings = stackalloc VkLayerSettingEXT[2];
            for (var i = 0; i < 2; i++)
            {
                settings[i] = new VkLayerSettingEXT
                {
                    pLayerName = settingStrings.Pointer[0],
                    pSettingName = settingStrings.Pointer[1 + i],
                    type = VkLayerSettingTypeEXT.Bool32,
                    valueCount = 1,
                    pValues = &on,
                };
            }

            var settingsInfo = new VkLayerSettingsCreateInfoEXT { settingCount = 2, pSettings = settings };
            if (layerSettings)
            {
                messengerInfo.pNext = &settingsInfo;
            }

            var createInfo = new VkInstanceCreateInfo
            {
                pNext = options.EnableValidation ? &messengerInfo : null,
                pApplicationInfo = &appInfo,
                enabledLayerCount = layerNames.Count,
                ppEnabledLayerNames = layerNames.Pointer,
                enabledExtensionCount = extensionNames.Count,
                ppEnabledExtensionNames = extensionNames.Pointer,
            };

            VkInstance handle;
            VulkanException.Check(vkCreateInstance(&createInfo, &handle), "vkCreateInstance");
            var api = GetApi(handle);
            var instance = new VulkanInstance(handle, api, enabled, layers, log, logHandle, options.EnableValidation);
            logHandle = default;
            try
            {
                if (options.EnableValidation)
                {
                    var persistent = MessengerCreateInfo(instance._logHandle);
                    VkDebugUtilsMessengerEXT messenger;
                    VulkanException.Check(api.vkCreateDebugUtilsMessengerEXT(&persistent, &messenger), "vkCreateDebugUtilsMessengerEXT");
                    instance._messenger = messenger;
                }

                instance.PhysicalDevices = PhysicalDeviceInfo.Enumerate(instance);
                instance.ActiveLayers = instance.QueryActiveLayers();
                return instance;
            }
            catch
            {
                instance.Dispose();
                throw;
            }
        }
        finally
        {
            if (logHandle.IsAllocated)
            {
                logHandle.Free();
            }
        }
    }

    public void Dispose()
    {
        if (_handle.IsNull)
        {
            return;
        }

        if (!_messenger.IsNull)
        {
            Api.vkDestroyDebugUtilsMessengerEXT(_messenger);
            _messenger = default;
        }

        Api.vkDestroyInstance();
        _handle = default;
        if (_logHandle.IsAllocated)
        {
            _logHandle.Free();
        }
    }

    internal static HashSet<string> EnumerateExtensions(string? layer)
    {
        using var layerName = new Utf8StringList(layer is null ? [] : [layer]);
        var name = layer is null ? null : layerName.Pointer[0];
        uint count;
        VulkanException.Check(vkEnumerateInstanceExtensionProperties(name, &count, null), "vkEnumerateInstanceExtensionProperties");
        var props = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* p = props)
        {
            VulkanException.Check(vkEnumerateInstanceExtensionProperties(name, &count, p), "vkEnumerateInstanceExtensionProperties");
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            fixed (VkExtensionProperties* p = &props[i])
            {
                set.Add(Utf8.FromFixed(p->extensionName, (int)VK_MAX_EXTENSION_NAME_SIZE));
            }
        }

        return set;
    }

    internal static HashSet<string> EnumerateLayers()
    {
        uint count;
        VulkanException.Check(vkEnumerateInstanceLayerProperties(&count, null), "vkEnumerateInstanceLayerProperties");
        var props = new VkLayerProperties[count];
        fixed (VkLayerProperties* p = props)
        {
            VulkanException.Check(vkEnumerateInstanceLayerProperties(&count, p), "vkEnumerateInstanceLayerProperties");
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            fixed (VkLayerProperties* p = &props[i])
            {
                set.Add(Utf8.FromFixed(p->layerName, (int)VK_MAX_EXTENSION_NAME_SIZE));
            }
        }

        return set;
    }

    private List<string> QueryActiveLayers()
    {
        var result = new List<string>();
        if (PhysicalDevices.Count == 0)
        {
            return result;
        }

        // The loader answers this (deprecated) query with the instance's activated layer list.
        var physical = PhysicalDevices[0].Handle;
        uint count;
        if (Api.vkEnumerateDeviceLayerProperties(physical, &count, null) != VkResult.Success || count == 0)
        {
            return result;
        }

        var props = new VkLayerProperties[count];
        fixed (VkLayerProperties* p = props)
        {
            if (Api.vkEnumerateDeviceLayerProperties(physical, &count, p) < 0)
            {
                return result;
            }
        }

        for (var i = 0; i < count; i++)
        {
            fixed (VkLayerProperties* p = &props[i])
            {
                result.Add(Utf8.FromFixed(p->layerName, (int)VK_MAX_EXTENSION_NAME_SIZE));
            }
        }

        return result;
    }

    private static VkDebugUtilsMessengerCreateInfoEXT MessengerCreateInfo(GCHandle logHandle) => new()
    {
        messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Warning | VkDebugUtilsMessageSeverityFlagsEXT.Error,
        messageType = VkDebugUtilsMessageTypeFlagsEXT.General | VkDebugUtilsMessageTypeFlagsEXT.Validation
            | VkDebugUtilsMessageTypeFlagsEXT.Performance,
        pfnUserCallback = &DebugCallback,
        pUserData = (void*)GCHandle.ToIntPtr(logHandle),
    };

    [UnmanagedCallersOnly]
    private static uint DebugCallback(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT types,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        // An exception must not unwind into the driver; a message that cannot be recorded is dropped.
        try
        {
            if (GCHandle.FromIntPtr((nint)userData).Target is ValidationLog log)
            {
                var level = (severity & VkDebugUtilsMessageSeverityFlagsEXT.Error) != 0 ? ValidationSeverity.Error
                    : (severity & VkDebugUtilsMessageSeverityFlagsEXT.Warning) != 0 ? ValidationSeverity.Warning
                    : (severity & VkDebugUtilsMessageSeverityFlagsEXT.Info) != 0 ? ValidationSeverity.Info
                    : ValidationSeverity.Verbose;
                var kinds = ValidationMessageKinds.None;
                kinds |= (types & VkDebugUtilsMessageTypeFlagsEXT.General) != 0 ? ValidationMessageKinds.General : 0;
                kinds |= (types & VkDebugUtilsMessageTypeFlagsEXT.Validation) != 0 ? ValidationMessageKinds.Validation : 0;
                kinds |= (types & VkDebugUtilsMessageTypeFlagsEXT.Performance) != 0 ? ValidationMessageKinds.Performance : 0;
                log.Add(new ValidationMessage(
                    level,
                    kinds,
                    data->messageIdNumber,
                    Utf8.FromPointer(data->pMessageIdName) ?? string.Empty,
                    Utf8.FromPointer(data->pMessage) ?? string.Empty));
            }
        }
#pragma warning disable CA1031 // Native callback boundary: nothing may propagate.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return 0;
    }
}
