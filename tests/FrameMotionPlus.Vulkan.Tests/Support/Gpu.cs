using System.Runtime.ExceptionServices;

namespace FrameMotionPlus.Vulkan.Tests.Support;

/// <summary>An instance with validation (core and synchronization checks) and, usually, the NVIDIA device.</summary>
public sealed class GpuContext : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    private GpuContext(VulkanInstance instance) => Instance = instance;

    public VulkanInstance Instance { get; }

    public VulkanDevice Device => _device ?? throw new InvalidOperationException("This context was created without a device.");

    public PhysicalDeviceInfo Nvidia => Instance.PhysicalDevices.First(static d => d.IsNvidia);

    private VulkanDevice? _device;

    public static GpuContext Create(IReadOnlyList<string>? instanceExtensions, bool createDevice)
    {
        var instance = VulkanInstance.Create(new VulkanInstanceOptions
        {
            ApplicationName = "FrameMotionPlus.Vulkan.Tests",
            Layers = VulkanLayerPolicy.Isolated,
            EnableValidation = true,
            SynchronizationValidation = true,
            RequiredExtensions = instanceExtensions ?? [],
        });
        var context = new GpuContext(instance);
        if (createDevice)
        {
            try
            {
                context._device = VulkanDevice.Create(instance, DeviceSelector.ByUuid(context.Nvidia.Uuid));
            }
            catch
            {
                instance.Dispose();
                throw;
            }
        }

        return context;
    }

    /// <summary>Another logical device on the NVIDIA GPU (a stand-in for a second API).</summary>
    public VulkanDevice CreateSecondDevice() => Own(VulkanDevice.Create(Instance, DeviceSelector.ByUuid(Nvidia.Uuid)));

    /// <summary>Disposed (in reverse order) before the device and instance.</summary>
    public T Own<T>(T disposable)
        where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            _owned[i].Dispose();
        }

        _owned.Clear();
        _device?.Dispose();
        Instance.Dispose();
    }
}

/// <summary>
/// Runs a GPU test body under the validation layer and fails the test on any validation warning or error, including
/// leaked objects that the layer reports when the device and instance are destroyed after the body.
/// </summary>
public static class Gpu
{
    public static void Run(Action<GpuContext> body, IReadOnlyList<string>? instanceExtensions = null, bool createDevice = true)
    {
        Requirements.SkipUnlessGpu();
        var context = GpuContext.Create(instanceExtensions, createDevice);
        ExceptionDispatchInfo? failure = null;
        try
        {
            body(context);
        }
        catch (Exception e)
        {
            failure = ExceptionDispatchInfo.Capture(e);
        }

        try
        {
            context.Dispose();
        }
        catch (Exception e)
        {
            failure ??= ExceptionDispatchInfo.Capture(e);
        }

        var log = context.Instance.Validation;
        if (log.ProblemCount > 0)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(log.Describe());
        }

        failure?.Throw();
        Assert.True(log.ProblemCount == 0, log.Describe());
    }

    public static void WriteLine(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);
}
