using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Vulkan;

namespace FrameMotionPlus.Gpu.Tests.Support;

public sealed record RigOptions
{
    public VulkanPresenterOptions Presenter { get; init; } = new();

    /// <summary>Offscreen target size; 0 means the ring size (1:1, so presents are bit-exact).</summary>
    public int TargetWidth { get; init; }

    public int TargetHeight { get; init; }

    public bool Readback { get; init; } = true;

    public CudaVulkanBridgeOptions Bridge { get; init; } = new();

    /// <summary>Lets a test create the bridge itself (e.g. with test hooks); the rig then has none until it does.</summary>
    public bool CreateBridge { get; init; } = true;
}

/// <summary>
/// The real pipeline, headless: a validated Vulkan instance and the device with the CUDA device's UUID, an offscreen
/// target with readback, the presenter (owner of the ring), the bridge importing the ring into the process-wide CUDA
/// context, and a CUDA producer stream. Built and torn down in the order the app uses.
/// </summary>
internal sealed class InteropRig : IDisposable
{
    private long _outputSlot;

    private InteropRig(CudaEnvironment cuda, VulkanInstance instance)
    {
        CudaEnv = cuda;
        Instance = instance;
    }

    public CudaEnvironment CudaEnv { get; }

    public CudaContext Cuda => CudaEnv.Context;

    public ImageKernels Kernels => CudaEnv.Kernels;

    public VulkanInstance Instance { get; }

    public VulkanDevice Device { get; private set; } = null!;

    public OffscreenTarget Target { get; private set; } = null!;

    public VulkanPresenter Presenter { get; private set; } = null!;

    public CudaVulkanBridge Bridge { get; set; } = null!;

    /// <summary>The producer stream ring writes are enqueued on (signals must complete in order: one stream).</summary>
    public CudaStream Stream { get; private set; } = null!;

    public PresentationRing Ring => Presenter.Ring!;

    public static InteropRig Create(CudaEnvironment cuda, int width, int height, PixelFormat format, RigOptions? options = null)
    {
        options ??= new RigOptions();
        var instance = VulkanInstance.Create(new VulkanInstanceOptions
        {
            ApplicationName = "FrameMotionPlus.Gpu.Tests",
            Layers = VulkanLayerPolicy.Isolated,
            EnableValidation = true,
            SynchronizationValidation = true,
            OnValidationMessage = Interop.EchoValidation,
        });
        var rig = new InteropRig(cuda, instance);
        try
        {
            rig.Device = VulkanDevice.Create(instance, GpuIdentity.VulkanSelectorFor(cuda.Context.Device));
            rig.Target = new OffscreenTarget(rig.Device, new OffscreenTargetOptions
            {
                Width = options.TargetWidth == 0 ? width : options.TargetWidth,
                Height = options.TargetHeight == 0 ? height : options.TargetHeight,
                Readback = options.Readback,
            });
            rig.Presenter = new VulkanPresenter(rig.Device, rig.Target, options.Presenter);
            rig.Stream = CudaStream.Create(cuda.Context);
            if (options.CreateBridge)
            {
                rig.Bridge = new CudaVulkanBridge(cuda.Context, rig.Presenter, options.Bridge);
                rig.Bridge.Configure(new BridgeConfig { Width = width, Height = height, Format = format });
            }

            return rig;
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    /// <summary>Reconfigures through the bridge and resizes the offscreen target to stay 1:1.</summary>
    public void Reconfigure(int width, int height, PixelFormat format)
    {
        Bridge.Configure(new BridgeConfig { Width = width, Height = height, Format = format });
        Target.Resize(width, height);
    }

    /// <summary>Acquires the next slot and enqueues: wait for its release, draw pattern <paramref name="frame"/>, signal written.</summary>
    public BridgeSlot WritePattern(uint frame, CudaStream? stream = null)
    {
        var s = stream ?? Stream;
        var slot = Bridge.AcquireSlot();
        s.Wait(slot.ProducerWait);
        Kernels.EnqueueTestPattern(s, slot.ProducerView, frame);
        s.Signal(slot.ProducerSignal);
        return slot;
    }

    public PresentRequest Request(in BridgeSlot slot) => Bridge.CreatePresentRequest(slot, _outputSlot++, 0);

    /// <summary>Spins until the presenter has a free frame slot. Allocation-free unless it times out.</summary>
    public void WaitReady(long timeoutMs = 5000)
    {
        var start = Stopwatch.GetTimestamp();
        while (!Presenter.IsReadyForPresent())
        {
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > timeoutMs)
            {
                throw new TimeoutException("The presenter never became ready.");
            }

            Thread.SpinWait(64);
        }
    }

    public void Present(in BridgeSlot slot)
    {
        WaitReady();
        Assert.True(Presenter.TryPresent(Request(slot)), $"write {slot.ProducerSignal.Value} was not presented");
    }

    /// <summary>The latest presented frame as packed pixels (target format), waiting for it to complete.</summary>
    public uint[] ReadPixels()
    {
        var bytes = new byte[Target.ReadbackSizeBytes];
        Assert.NotEqual(0UL, Target.ReadLatest(bytes));
        return MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }

    public void Dispose()
    {
        // The app's order: CUDA imports first (they refer to the presenter's ring), then the presenter, which releases
        // whatever it was handed, then its target, the device and the instance.
        ExceptionDispatchInfo? failure = null;
        void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failure ??= ExceptionDispatchInfo.Capture(e);
            }
        }

        Try(() => Bridge?.Dispose());
        Try(() => Presenter?.Dispose());
        Try(() => Target?.Dispose());
        Try(() => Stream?.Dispose());
        Try(() => Device?.Dispose());
        Try(Instance.Dispose);
        failure?.Throw();
    }
}

/// <summary>
/// Runs a GPU interop test body under the validation layer (core and synchronization checks) and fails the test on any
/// validation warning or error, including objects the layer finds leaked when the device and instance are destroyed.
/// </summary>
internal static class Interop
{
    public static void Run(int width, int height, PixelFormat format, Action<InteropRig> body, RigOptions? options = null)
    {
        var cuda = Hardware.RequireGpu();
        var rig = InteropRig.Create(cuda, width, height, format, options);
        ExceptionDispatchInfo? failure = null;
        try
        {
            body(rig);
        }
        catch (Exception e)
        {
            failure = ExceptionDispatchInfo.Capture(e);
        }

        // Bounded: a teardown that hangs (e.g. freeing CUDA memory that stuck GPU work still uses) fails the test
        // instead of wedging the run.
        ExceptionDispatchInfo? teardownFailure = null;
        var teardown = new Thread(() =>
        {
            try
            {
                rig.Dispose();
            }
            catch (Exception e)
            {
                teardownFailure = ExceptionDispatchInfo.Capture(e);
            }
        }) { IsBackground = true, Name = "fmp-rig-teardown" };
        teardown.Start();
        if (!teardown.Join(TimeSpan.FromSeconds(60)))
        {
            failure?.Throw();
            Assert.Fail("Tearing the rig down did not finish within 60 s (hung).");
        }

        failure ??= teardownFailure;
        var log = rig.Instance.Validation;
        if (log.ProblemCount > 0)
        {
            WriteLine(log.Describe());
        }

        failure?.Throw();
        Assert.True(log.ProblemCount == 0, log.Describe());
    }

    public static void WriteLine(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    /// <summary>
    /// Writes a validation-layer problem to stderr as it arrives (from whichever thread made the Vulkan call), with its
    /// message id, so a message is never counted but lost even if the test's own output is not kept.
    /// </summary>
    public static void EchoValidation(ValidationMessage message)
    {
        if (message.IsProblem)
        {
            Console.Error.WriteLine($"validation: {message}");
        }
    }

    /// <summary>Asserts two images are identical, naming the first difference.</summary>
    public static void AssertPixels(uint[] expected, uint[] actual, int width, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            Assert.Fail($"{what}: {FrameMotionPlus.Cuda.Tests.Support.Reference.FirstDifference(expected, actual, width)}");
        }
    }
}
