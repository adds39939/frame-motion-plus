using System.Collections.Concurrent;
using System.Globalization;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Tests.Support;
using FrameMotionPlus.CudaVulkan;
using FrameMotionPlus.Sources.File;
using FrameMotionPlus.Vfx;
using FrameMotionPlus.Vulkan;
using FrameMotionPlus.Vulkan.Windowing;

// The pipeline tests share one GPU, the process-wide CUDA context and loader policy, the user's desktop (Display) and
// exact per-thread allocation counters; run them one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
[assembly: Xunit.AssemblyFixture(typeof(FrameMotionPlus.Pipeline.Tests.Support.ClipDirectory))]

namespace FrameMotionPlus.Pipeline.Tests.Support;

/// <summary>Test categories. Hardware tests skip, never fail, when their requirement is missing.</summary>
public static class Category
{
    public const string Name = "Category";

    /// <summary>An NVIDIA GPU (CUDA and a Vulkan device on it), the FFmpeg 9 libraries and the ffmpeg CLI (to make clips).</summary>
    public const string Gpu = "Gpu";

    /// <summary>A Wayland session and SDL3 as well.</summary>
    public const string Display = "Display";

    /// <summary>The NVIDIA VFX SDK as well.</summary>
    public const string Vfx = "Vfx";
}

/// <summary>
/// The hardware gate: probed once. Anything missing or throwing means "skip", never "fail" (hosted CI has no GPU, no
/// display, no FFmpeg 9 and no SDK, and also builds on Windows).
/// </summary>
internal static class Hardware
{
    private static readonly Lazy<(CudaContext? Context, string? Reason)> _probe = new(Probe);
    private static readonly Lazy<string?> _ffmpeg = new(() => Tools.Which("ffmpeg"));

    /// <summary>GPU, Vulkan device, FFmpeg libraries and the ffmpeg CLI; returns the process's CUDA context.</summary>
    public static CudaContext RequireGpu()
    {
        var (context, reason) = _probe.Value;
        if (context is null)
        {
            Assert.Skip(reason ?? "GPU unavailable");
        }

        if (_ffmpeg.Value is null)
        {
            Assert.Skip("the ffmpeg CLI is not installed (it makes the test clips)");
        }

        return context;
    }

    public static string Ffmpeg => _ffmpeg.Value ?? throw new InvalidOperationException("no ffmpeg CLI");

    /// <summary>A CUDA device only (the missing-FFmpeg message needs one: without a GPU the GPU message comes first).</summary>
    public static void RequireCuda()
    {
        if (!Cuda.CudaDriver.TryInitialize(out var reason) || Cuda.CudaDriver.DeviceCount == 0)
        {
            Assert.Skip(reason ?? "no CUDA device");
        }
    }

    public static CudaContext RequireDisplay()
    {
        var context = RequireGpu();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Assert.Skip("no Wayland session (WAYLAND_DISPLAY is not set)");
        }

        if (!SdlWindow.IsLibraryAvailable())
        {
            Assert.Skip("SDL3 (libSDL3.so.0) is not installed");
        }

        return context;
    }

    public static CudaContext RequireVfx()
    {
        var context = RequireGpu();
        if (!VfxSdk.TryLoad(out var reason))
        {
            Assert.Skip(reason ?? "the VFX SDK is not available");
        }

        return context;
    }

    private static (CudaContext?, string?) Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return (null, "the pipeline needs Linux (opaque FD interop)");
        }

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

            if (!FileFrameSource.IsAvailable(out var ffmpeg))
            {
                return (null, $"no FFmpeg 9 libraries: {ffmpeg}");
            }

            VulkanLayerPolicy.Isolated.ApplyToProcess();
            if (!VulkanLoader.TryInitialize(out var error))
            {
                return (null, error);
            }

            var device = CudaDriver.GetDevice(0);
            var uuid = GpuIdentity.UuidOf(device);
            using (var instance = VulkanInstance.Create(new VulkanInstanceOptions()))
            {
                if (!instance.PhysicalDevices.Any(d => d.Uuid == uuid))
                {
                    return (null, $"no Vulkan device has the CUDA device's UUID {uuid}");
                }
            }

            // As the app does: BlockingSync before the primary context is first made active in the process.
            return (CudaContext.RetainPrimary(device, CudaSchedulingMode.BlockingSync), null);
        }
        catch (Exception e)
        {
            return (null, $"GPU unavailable: {e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>
/// Clips made on first use by the ffmpeg CLI from lavfi's test sources (nothing binary is checked in), into a temporary
/// directory removed when the run ends. testsrc2 changes every frame (a moving pattern and a frame counter), so every
/// frame is identifiable; the lossless ones decode to exactly the pictures they were made from.
/// </summary>
internal static class Clips
{
    private static readonly ConcurrentDictionary<string, Lazy<string>> _made = new();

    private static string[] Bt709 => ["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"];

    /// <summary>name → (file, ffmpeg arguments after -y without the output), or a concatenation of other clips.</summary>
    private static readonly Dictionary<string, (string File, string[] Args)> Specs = new()
    {
        ["c30"] = ("c30.mkv", [.. Src(320, 180, 30, 75), .. H264(), .. Bt709]),
        ["c60"] = ("c60.mkv", [.. Src(320, 180, 60, 120), .. H264(), .. Bt709]),
        ["c24"] = ("c24.mkv", [.. Src(320, 180, 24, 60), .. H264(), .. Bt709]),
        ["c60-long"] = ("c60-long.mkv", [.. Src(320, 180, 60, 600), .. H264(), .. Bt709]),
        ["c2997-mp4"] = ("c2997.mp4", ["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30000/1001,trim=end_frame=300", .. H264(), .. Bt709]),
        ["c60-mp4"] = ("c60.mp4", [.. Src(320, 180, 60, 600), .. H264(), .. Bt709]),
        ["c60-short"] = ("c60-short.mkv", [.. Src(320, 180, 60, 30), .. H264(), .. Bt709]),
        ["vfr"] = ("vfr.mkv", [.. Src(320, 180, 30, 60), "-vf", "settb=1/1000,setpts='N*33+mod(N\\,2)*10'", "-fps_mode", "passthrough",
            "-enc_time_base", "filter", .. H264(), .. Bt709]),
        ["hevc10"] = ("hevc10.mkv", [.. Src(320, 180, 60, 90), "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le",
            "-x265-params", "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:chromaloc=2",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc"]),
        ["c60-1080p"] = ("c60-1080p.mp4", [.. Src(1920, 1080, 60, 40), .. H264(), .. Bt709]),
        ["c60-1440p"] = ("c60-1440p.mp4", [.. Src(2560, 1440, 60, 40), .. H264(), .. Bt709]),
        ["c60-2160p"] = ("c60-2160p.mp4", [.. Src(3840, 2160, 60, 40), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", .. Bt709]),
        ["hevc10-2160p"] = ("hevc10-2160p.mkv", [.. Src(3840, 2160, 60, 40), "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le",
            "-x265-params", "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:chromaloc=2",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc"]),
        // MPEG-TS parts (each starts at 1.4 s); concatenated, the timestamps step back at frame 30.
        ["ts-a"] = ("ts-a.ts", [.. Src(320, 180, 30, 30), "-c:v", "libx264", "-preset", "veryfast", "-bf", "2", "-g", "15", "-pix_fmt", "yuv420p", .. Bt709]),
        ["ts-b"] = ("ts-b.ts", ["-f", "lavfi", "-i", "testsrc=size=320x180:rate=30,trim=end_frame=30", "-c:v", "libx264", "-preset", "veryfast", "-bf", "2",
            "-g", "15", "-pix_fmt", "yuv420p", .. Bt709]),
        // Lossless: testsrc2 at 120 fps, and every second frame of it at 60 fps: the 120 fps clip holds the true middle
        // frame of every pair of the 60 fps one.
        ["truth-120"] = ("truth-120.mp4", [.. Src(640, 360, 120, 240), "-c:v", "libx264", "-qp", "0", "-preset", "veryfast", "-pix_fmt", "yuv420p", .. Bt709]),
        ["even-60"] = ("even-60.mp4", [.. Src(640, 360, 120, 240), "-vf", "select='not(mod(n\\,2))',setpts=N/60/TB", "-r", "60", "-c:v", "libx264", "-qp", "0",
            "-preset", "veryfast", "-pix_fmt", "yuv420p", .. Bt709]),
    };

    private static readonly Dictionary<string, (string File, string[] Parts)> Concatenations = new()
    {
        ["ts-concat"] = ("ts-concat.ts", ["ts-a", "ts-b"]),
    };

    public static string Get(string name) => _made.GetOrAdd(name, n => new Lazy<string>(() => Make(n))).Value;

    private static string[] Src(int width, int height, int rate, int frames) =>
        ["-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"testsrc2=size={width}x{height}:rate={rate},trim=end_frame={frames}")];

    private static string[] H264() => ["-c:v", "libx264", "-preset", "veryfast", "-bf", "2", "-g", "30", "-pix_fmt", "yuv420p"];

    private static string Make(string name)
    {
        if (Concatenations.TryGetValue(name, out var concatenation))
        {
            var target = Path.Combine(ClipDirectory.Path, concatenation.File);
            using (var joined = File.Create(target))
            {
                foreach (var part in concatenation.Parts)
                {
                    using var input = File.OpenRead(Get(part));
                    input.CopyTo(joined);
                }
            }

            return target;
        }

        var (file, args) = Specs[name];
        var path = Path.Combine(ClipDirectory.Path, file);
        var (exit, output) = Tools.Run(Hardware.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", .. args, path]);
        Assert.True(exit == 0 && File.Exists(path), $"ffmpeg could not make clip '{name}': {output}");
        return path;
    }
}

/// <summary>The run's clip directory, removed when the test assembly finishes.</summary>
public sealed class ClipDirectory : IDisposable
{
    private static readonly Lazy<string> _path = new(() =>
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fmp-pipeline-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    });

    public static string Path => _path.Value;

    public void Dispose()
    {
        if (_path.IsValueCreated)
        {
            try
            {
                Directory.Delete(_path.Value, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

internal static class Log
{
    public static void WriteLine(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);
}
