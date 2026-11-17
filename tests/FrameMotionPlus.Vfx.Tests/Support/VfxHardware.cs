using System.Diagnostics;
using System.Globalization;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;

// VFG tests share one GPU, the SDK's process-wide state and exact per-thread allocation counters, and several of them
// time things; run them one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace FrameMotionPlus.Vfx.Tests.Support;

/// <summary>Test categories. Hardware tests skip, never fail, when their requirement is missing.</summary>
public static class Category
{
    public const string Name = "Category";

    /// <summary>Needs an NVIDIA GPU with the CUDA driver and the NVIDIA VFX SDK with the VFG feature.</summary>
    public const string Vfx = "Vfx";
}

/// <summary>
/// The hardware gate of this project: CUDA (driver library, cuInit, a device) and the VFX SDK, found the way the app
/// finds it (<see cref="VfxSdkLocator"/>: $FMP_VFX_SDK, then lib/VideoFX of this checkout, then /usr/local/VideoFX).
/// Probed once. Anything missing means "skip", never "fail": hosted CI has neither.
/// </summary>
internal static class VfxHardware
{
    private static readonly Lazy<(VfxEnvironment? Environment, string? Reason)> _probe = new(Probe);

    /// <summary>Skips unless CUDA and the SDK are usable; returns the shared environment.</summary>
    public static VfxEnvironment Require()
    {
        var (environment, reason) = _probe.Value;
        if (environment is null)
        {
            Assert.Skip(reason ?? "VFX SDK unavailable");
        }

        return environment;
    }

    /// <summary>Skips unless CUDA is usable (the SDK may be absent).</summary>
    public static CudaContext RequireCuda()
    {
        if (!CudaDriver.TryInitialize(out var reason) || CudaDriver.DeviceCount == 0)
        {
            Assert.Skip(reason ?? "no CUDA device");
        }

        return SharedContext.Value;
    }

    private static readonly Lazy<CudaContext> SharedContext = new(() => CudaContext.RetainPrimary(0));

    private static (VfxEnvironment?, string?) Probe()
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

            var location = Sdk.Location;
            if (!VfxSdk.TryLoad(location, out var why))
            {
                return (null, why);
            }

            return (new VfxEnvironment(SharedContext.Value, location), null);
        }
        catch (Exception e)
        {
            return (null, $"VFG unavailable: {e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>The process-wide context and kernels; never disposed (process exit releases them).</summary>
internal sealed class VfxEnvironment
{
    public VfxEnvironment(CudaContext context, VfxSdkLocation location)
    {
        Context = context;
        Location = location;
        Kernels = ImageKernels.Load(context);
    }

    public CudaContext Context { get; }

    public ImageKernels Kernels { get; }

    public VfxSdkLocation Location { get; }

    /// <summary>A pitched device image filled with <paramref name="fill"/> (every 32-bit pixel).</summary>
    public CudaDeviceBuffer Image(int width, int height, PixelFormat format, uint fill = 0)
    {
        var buffer = CudaDeviceBuffer.AllocateImage(Context, width, height, format);
        using var stream = CudaStream.Create(Context);
        stream.FillFrameAsync(buffer.AsFrame(), fill);
        stream.Synchronize();
        return buffer;
    }

    /// <summary>A device image holding test-pattern frame <paramref name="index"/> (native/kernels/pattern.cu).</summary>
    public CudaDeviceBuffer Pattern(int width, int height, PixelFormat format, uint index)
    {
        var buffer = CudaDeviceBuffer.AllocateImage(Context, width, height, format);
        using var stream = CudaStream.Create(Context);
        Kernels.EnqueueTestPattern(stream, buffer.AsFrame(), index);
        stream.Synchronize();
        return buffer;
    }

    /// <summary>A device image holding <paramref name="pixels"/> (tightly packed rows).</summary>
    public CudaDeviceBuffer Upload(int width, int height, PixelFormat format, uint[] pixels)
    {
        var buffer = CudaDeviceBuffer.AllocateImage(Context, width, height, format);
        using var stream = CudaStream.Create(Context);
        stream.CopyFrameFromHost(pixels, buffer.AsFrame());
        stream.Synchronize();
        return buffer;
    }

    public uint[] Read(in GpuFrame frame)
    {
        using var stream = CudaStream.Create(Context);
        var pixels = new uint[frame.Width * frame.Height];
        stream.CopyFrameToHost(frame, pixels);
        return pixels;
    }

    public uint[] Read(CudaDeviceBuffer buffer) => Read(buffer.AsFrame());

    /// <summary>A generator with options, configured for the shape.</summary>
    public VfgGenerator Generator(int width, int height, PixelFormat format, FrameGenQuality quality = FrameGenQuality.Medium, VfgOptions? options = null)
    {
        var generator = new VfgGenerator(Context, options);
        try
        {
            generator.Configure(new FrameGenConfig { Width = width, Height = height, Format = format, Quality = quality });
        }
        catch
        {
            generator.Dispose();
            throw;
        }

        return generator;
    }
}

/// <summary>The SDK the tests use: found as the app finds it, with this test assembly's checkout as a starting point.</summary>
internal static class Sdk
{
    public static VfxSdkLocation Location => VfxSdkLocator.Resolve(searchFrom: FrameMotionPlus.Cuda.Tests.Support.Repo.Root);
}

internal static class Log
{
    public static void WriteLine(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    public static string Ms(long ns) => (ns / 1e6).ToString("0.000", CultureInfo.InvariantCulture);

    public static string Ms(double ns) => (ns / 1e6).ToString("0.000", CultureInfo.InvariantCulture);
}

internal static class Machine
{
    /// <summary>This process's GPU memory as nvidia-smi reports it (MiB), or -1 when it cannot tell.</summary>
    public static long ProcessGpuMemoryMiB()
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi", "--query-compute-apps=pid,used_memory --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(info);
            if (process is null)
            {
                return -1;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid) && pid == Environment.ProcessId
                    && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var mib))
                {
                    return mib;
                }
            }

            return -1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

    /// <summary>Runs <paramref name="action"/> on a thread of its own and fails the test if it does not return in time.</summary>
    public static void Bounded(Action action, TimeSpan timeout, string what)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failure = e;
            }
        }) { IsBackground = true, Name = what };
        thread.Start();
        Assert.True(thread.Join(timeout), $"{what} did not finish within {timeout.TotalSeconds:F0} s");
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
