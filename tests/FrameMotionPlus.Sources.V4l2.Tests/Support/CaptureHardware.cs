using System.Diagnostics;
using System.Runtime.Versioning;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;

// The capture tests share the vivid instance, the capture card, the process-wide CUDA context and exact per-thread
// allocation counters: run them one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace FrameMotionPlus.Sources.V4l2.Tests.Support;

/// <summary>Test categories. Hardware tests skip, never fail, when their requirement is missing.</summary>
public static class Category
{
    public const string Name = "Category";

    /// <summary>An NVIDIA GPU with the CUDA driver.</summary>
    public const string Gpu = "Gpu";

    /// <summary>A V4L2 capture device: the kernel's vivid test driver (and a GPU).</summary>
    public const string Capture = "Capture";

    /// <summary>The Elgato 4K X with a signal on it (and a GPU).</summary>
    public const string Elgato = "Elgato";
}

/// <summary>The hardware gates: anything missing means "skip", never "fail" (hosted CI has no GPU and no V4L2 devices).</summary>
internal static class CaptureHardware
{
    private static readonly Lazy<(CudaContext? Context, string? Reason)> _cuda = new(ProbeCuda);
    private static readonly Lazy<ImageKernels?> _kernels = new(() => _cuda.Value.Context is { } c ? ImageKernels.Load(c) : null);

    public static CudaContext RequireCuda()
    {
        var (context, reason) = _cuda.Value;
        if (context is null)
        {
            Assert.Skip(reason ?? "no CUDA device");
        }

        return context;
    }

    public static ImageKernels Kernels => _kernels.Value ?? throw new InvalidOperationException("no CUDA context");

    /// <summary>The vivid capture node (vivid is loaded by hand: <c>sudo modprobe vivid</c>; it goes away on reboot).</summary>
    [SupportedOSPlatform("linux")]
    public static string RequireVivid()
    {
        RequireLinux();
        var vivid = CaptureDevices.List().FirstOrDefault(d => d.Info.Driver == "vivid");
        if (vivid is null)
        {
            Assert.Skip("the vivid test driver is not loaded (sudo modprobe vivid)");
        }

        return vivid.Info.Path;
    }

    /// <summary>The Elgato 4K X's capture node.</summary>
    [SupportedOSPlatform("linux")]
    public static string RequireElgato()
    {
        RequireLinux();
        var card = CaptureDevices.List().FirstOrDefault(d => d.Info.Driver == "uvcvideo" && d.Info.Card.Contains("Elgato 4K X", StringComparison.Ordinal));
        if (card is null)
        {
            Assert.Skip("no Elgato 4K X capture card is plugged in");
        }

        return card.Info.Path;
    }

    /// <summary>
    /// The Elgato with a picture on its input: a 1.5 s capture at 1080p NV12 must not be mostly black (a console asleep or
    /// off sends nothing, and the card then delivers black frames): skip otherwise, saying so.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static string RequireElgatoSignal()
    {
        var path = RequireElgato();
        var context = RequireCuda();
        using var source = new V4l2FrameSource(context, Capture.Options(path, 1920, 1080, 60, CapturePixelFormat.Nv12));
        source.Start();
        var until = Stopwatch.GetTimestamp() + (long)(1.5 * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            if (source.TryRead(out var f))
            {
                source.Release(f.FrameId, Core.Frames.GpuSignal.None);
            }

            Thread.Sleep(2);
        }

        source.Stop();
        var s = source.Statistics;
        if (s.Captured == 0 || s.BlackFrames * 2 > s.Captured)
        {
            Assert.Skip($"no picture on the capture card's input ({s.BlackFrames} of {s.Captured} frames black): the console asleep or off?");
        }

        return path;
    }

    [SupportedOSPlatformGuard("linux")]
    public static void RequireLinux()
    {
        if (!CaptureDevices.IsAvailable(out var why))
        {
            Assert.Skip(why);
        }
    }

    private static (CudaContext?, string?) ProbeCuda()
    {
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

            // As the app does: BlockingSync before the primary context is first made active in the process.
            return (CudaContext.RetainPrimary(CudaDriver.GetDevice(0), CudaSchedulingMode.BlockingSync), null);
        }
        catch (Exception e)
        {
            return (null, $"CUDA unavailable: {e.Message}");
        }
    }
}

/// <summary>vivid's own controls (drivers/media/test-drivers/vivid/vivid-ctrls.c: VIVID_CID_CUSTOM_BASE + n), set for a test and restored after.</summary>
[SupportedOSPlatform("linux")]
internal sealed class VividControls : IDisposable
{
    public const uint TestPattern = 0x00f0f000;
    public const uint OsdTextMode = 0x00f0f001;
    public const uint HorizontalMovement = 0x00f0f002;
    public const uint TimestampSource = 0x00f0f018;
    public const uint PercentageDroppedBuffers = 0x00f0f040;
    public const uint Disconnect = 0x00f0f041;
    public const uint InjectBufFlagError = 0x00f0f042;
    public const uint InjectFatalStreamingError = 0x00f0f046;

    /// <summary>osd_text_mode: 2 = none (a frame is then the pattern alone, the same every time).</summary>
    public const int OsdNone = 2;

    /// <summary>horizontal_movement: 3 = none; 4 = slow right, 5 = medium right, 6 = fast right.</summary>
    public const int NoMovement = 3;

    public const int FastRight = 6;

    private readonly string _path;
    private readonly List<(uint Id, int Value)> _restore = [];

    public VividControls(string path)
    {
        _path = path;
    }

    /// <summary>Sets <paramref name="id"/> (remembering its value) through a descriptor of its own, closed at once.</summary>
    public VividControls Set(uint id, int value)
    {
        using var device = V4l2Device.Open(_path);
        if (device.TryGetControl(id, out var old) && !_restore.Any(r => r.Id == id))
        {
            _restore.Add((id, old));
        }

        Assert.True(device.SetControl(id, value) == 0, $"vivid control 0x{id:x8} could not be set to {value}");
        return this;
    }

    /// <summary>Presses a button control on <paramref name="device"/> (the capture's own descriptor, or another).</summary>
    public static int Press(V4l2Device device, uint id) => device.SetControl(id, 1);

    public void Dispose()
    {
        try
        {
            using var device = V4l2Device.Open(_path);
            foreach (var (id, value) in _restore)
            {
                _ = device.SetControl(id, value);
            }
        }
        catch (CaptureUnavailableException)
        {
            // vivid disconnected (a disconnect test): it reconnects once the last descriptor closes; controls keep their values.
        }
    }
}

/// <summary>Reading frames from a capture source and back from the GPU.</summary>
[SupportedOSPlatform("linux")]
internal static class Capture
{
    public static CaptureSourceOptions Options(string device, int width, int height, double fps, CapturePixelFormat format) => new()
    {
        Device = device,
        Request = new CaptureRequest { Width = width, Height = height, Fps = fps, Format = format },
        FrameLogCapacity = 4096,
    };

    /// <summary>The next frame, waiting (bounded) for one; fails when the capture ended.</summary>
    public static SourceFrame Next(V4l2FrameSource source, double timeoutS = 10)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutS * Stopwatch.Frequency);
        while (true)
        {
            if (source.TryRead(out var frame))
            {
                return frame;
            }

            if (source.Failure is { } failure)
            {
                throw new InvalidOperationException("the capture ended", failure);
            }

            Assert.True(Stopwatch.GetTimestamp() < deadline, $"no frame within {timeoutS} s");
            Thread.Sleep(1);
        }
    }

    /// <summary>Waits (bounded) until <paramref name="condition"/> holds.</summary>
    public static void WaitUntil(Func<bool> condition, double timeoutS, string what)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutS * Stopwatch.Frequency);
        while (!condition())
        {
            Assert.True(Stopwatch.GetTimestamp() < deadline, $"{what} did not happen within {timeoutS} s");
            Thread.Sleep(5);
        }
    }

    /// <summary>A frame's RGB pixels, read back after its Ready point.</summary>
    public static uint[] Read(CudaContext context, in SourceFrame frame)
    {
        using var stream = CudaStream.Create(context);
        stream.Wait(frame.Frame.Ready);
        var pixels = new uint[frame.Frame.Width * frame.Frame.Height];
        stream.CopyFrameToHost(frame.Frame, pixels);
        return pixels;
    }
}

internal static class Log
{
    public static void WriteLine(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);
}
