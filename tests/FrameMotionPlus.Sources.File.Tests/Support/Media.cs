using System.Collections.Concurrent;
using System.Globalization;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Cuda.Tests.Support;
using FrameMotionPlus.Sources.File.Interop;

[assembly: Xunit.AssemblyFixture(typeof(FrameMotionPlus.Sources.File.Tests.Support.ClipDirectory))]

namespace FrameMotionPlus.Sources.File.Tests.Support;

public static class Category
{
    public const string Name = "Category";

    /// <summary>Needs an NVIDIA GPU, the FFmpeg 9 shared libraries and the ffmpeg CLI; skips otherwise.</summary>
    public const string Media = "Media";
}

/// <summary>
/// The hardware and tool gate of this project: a test calls <see cref="Require"/> (GPU, FFmpeg 9 libraries, ffmpeg CLI)
/// or <see cref="RequireGpu"/> first and is skipped, never failed, when something is missing (hosted CI has neither a GPU
/// nor these libraries).
/// </summary>
internal static class Media
{
    private static readonly Lazy<(MediaEnvironment? Environment, string? Reason)> _shared = new(Create);
    private static readonly Lazy<(CudaEnvironment? Environment, string? Reason)> _cuda = new(CreateCuda);

    /// <summary>A GPU only (the convert kernel's tests need no FFmpeg).</summary>
    public static CudaEnvironment RequireCuda()
    {
        var (environment, reason) = _cuda.Value;
        if (environment is null)
        {
            Assert.Skip(reason ?? "unavailable");
        }

        return environment;
    }

    private static (CudaEnvironment?, string?) CreateCuda()
    {
        if (!CudaDriver.TryInitialize(out var reason))
        {
            return (null, $"no CUDA driver: {reason}");
        }

        return CudaDriver.DeviceCount == 0 ? (null, "no CUDA device") : (new CudaEnvironment(), null);
    }

    public static MediaEnvironment Require()
    {
        var (environment, reason) = _shared.Value;
        if (environment is null)
        {
            Assert.Skip(reason ?? "unavailable");
        }

        if (environment.Ffmpeg is null)
        {
            Assert.Skip("the ffmpeg CLI is not installed (it makes the test clips)");
        }

        return environment;
    }

    /// <summary>GPU and FFmpeg libraries, no CLI needed.</summary>
    public static MediaEnvironment RequireGpu()
    {
        var (environment, reason) = _shared.Value;
        if (environment is null)
        {
            Assert.Skip(reason ?? "unavailable");
        }

        return environment;
    }

    private static (MediaEnvironment?, string?) Create()
    {
        if (!CudaDriver.TryInitialize(out var reason))
        {
            return (null, $"no CUDA driver: {reason}");
        }

        if (CudaDriver.DeviceCount == 0)
        {
            return (null, "no CUDA device");
        }

        if (!FFmpegLibraries.TryLoad(out var ffmpeg))
        {
            return (null, $"no FFmpeg 9 libraries: {ffmpeg}");
        }

        return (new MediaEnvironment(), null);
    }
}

/// <summary>The process's primary context and kernels; shared for the whole run, never disposed.</summary>
internal class CudaEnvironment
{
    private static readonly Lazy<(CudaContext Context, ImageKernels Kernels)> _shared = new(() =>
    {
        var context = CudaContext.RetainPrimary(0);
        return (context, ImageKernels.Load(context));
    });

    public CudaEnvironment() => (Context, Kernels) = _shared.Value;

    public CudaContext Context { get; }

    public ImageKernels Kernels { get; }
}

/// <summary>GPU, FFmpeg 9 libraries and (for clips) the ffmpeg CLI.</summary>
internal sealed class MediaEnvironment : CudaEnvironment
{
    public MediaEnvironment()
    {
        Ffmpeg = Tools.Which("ffmpeg");
        Ffprobe = Tools.Which("ffprobe");
    }

    public string? Ffmpeg { get; }

    public string? Ffprobe { get; }

    public string Clip(string name) => Clips.Get(name, Ffmpeg ?? throw new InvalidOperationException("no ffmpeg CLI"));

    /// <summary>
    /// Each frame's best-effort timestamp in seconds as ffprobe decodes it, in presentation order (NaN for a frame
    /// without one: an elementary stream has none).
    /// </summary>
    public double[] FrameTimes(string path)
    {
        var (exit, output) = Tools.Run(Ffprobe ?? "ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", path]);
        Assert.True(exit == 0, output);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimEnd(','))
            .Where(l => l.Length > 0)
            .Select(l => l == "N/A" ? double.NaN : double.Parse(l, CultureInfo.InvariantCulture))];
    }
}

/// <summary>
/// Test clips, made on first use by the ffmpeg CLI (nothing binary is checked in) into a temporary directory that is
/// removed when the run ends. Small sizes and short lengths except where size is the point.
/// </summary>
internal static class Clips
{
    private static readonly ConcurrentDictionary<string, Lazy<string>> _made = new();

    public static string Directory => ClipDirectory.Path;

    /// <summary>name → (file name, ffmpeg arguments after <c>-y</c>, without the output).</summary>
    public static readonly IReadOnlyDictionary<string, (string File, string[] Args)> Specs = new Dictionary<string, (string, string[])>
    {
        // H.264 8-bit, BT.709 tagged, B-frames (3), with an audio track the source must ignore.
        ["h264"] = ("h264.mp4", [.. Source(640, 360, 30, 45), "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1.5",
            "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "veryfast", "-bf", "3", "-g", "15", "-pix_fmt", "yuv420p", .. Bt709, "-c:a", "aac", "-shortest"]),
        ["hevc"] = ("hevc.mkv", [.. Source(640, 360, 30, 30), "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-pix_fmt", "yuv420p", .. Bt709]),
        ["hevc10-pq"] = ("hevc10-pq.mkv", [.. Source(640, 360, 30, 30), "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le",
            "-x265-params", "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:chromaloc=2:hdr10=1:" +
            "master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc"]),
        ["vp9"] = ("vp9.webm", [.. Source(640, 360, 30, 20), "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-pix_fmt", "yuv420p", .. Bt709]),
        ["av1"] = ("av1.mkv", [.. Source(640, 360, 30, 20), "-c:v", "libsvtav1", "-preset", "12", "-pix_fmt", "yuv420p10le", .. Bt709]),
        // Variable frame rate: frames 43 ms and 23 ms apart, alternately (Matroska keeps the timestamps).
        ["vfr"] = ("vfr.mkv", [.. Source(640, 360, 30, 30), "-vf", "settb=1/1000,setpts='N*33+mod(N,2)*10'", "-fps_mode", "passthrough",
            "-enc_time_base", "filter", "-c:v", "libx264", "-preset", "veryfast", "-bf", "2", "-pix_fmt", "yuv420p", .. Bt709]),
        // A broken file: timestamps rounded to the 1/30 s grid collide in pairs (0, 33, 33, 100, 100 ms, ...).
        ["dup-pts"] = ("dup-pts.mkv", [.. Source(640, 360, 30, 30), "-vf", "setpts='(N*33+mod(N,2)*10)/1000/TB'", "-fps_mode", "passthrough",
            "-c:v", "libx264", "-preset", "veryfast", "-bf", "2", "-pix_fmt", "yuv420p", .. Bt709]),
        // MPEG-TS starts at 1.4 s.
        ["start-offset"] = ("start-offset.ts", [.. Source(640, 360, 30, 30), "-c:v", "libx264", "-preset", "veryfast", "-bf", "2", "-pix_fmt", "yuv420p", .. Bt709]),
        // An elementary stream: no container timestamps at all.
        ["raw-h264"] = ("raw.h264", [.. Source(640, 360, 25, 25), "-c:v", "libx264", "-preset", "veryfast", "-bf", "0", "-pix_fmt", "yuv420p", .. Bt709, "-f", "h264"]),
        ["h264-1080p"] = ("h264-1080p.mp4", [.. Source(1920, 1080, 60, 8), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", .. Bt709]),
        ["h264-1440p"] = ("h264-1440p.mp4", [.. Source(2560, 1440, 60, 8), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", .. Bt709]),
        ["h264-2160p"] = ("h264-2160p.mp4", [.. Source(3840, 2160, 60, 6), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", .. Bt709]),
        ["hevc10-2160p"] = ("hevc10-2160p.mkv", [.. Source(3840, 2160, 60, 6), "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le",
            "-x265-params", "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:chromaloc=2",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc"]),
        // Not a multiple of 16 (H.264 crops 10 columns and 2 rows of its 1376x768 coded picture).
        ["h264-odd"] = ("h264-odd.mp4", [.. Source(1366, 766, 30, 10), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", .. Bt709]),
        ["hevc10-odd"] = ("hevc10-odd.mkv", [.. Source(1366, 766, 30, 10), "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-pix_fmt", "yuv420p10le", .. Bt709]),
        // Full range, BT.709.
        ["h264-full"] = ("h264-full.mp4", [.. Source(640, 360, 30, 15), "-vf", "scale=out_range=full", "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-color_range", "pc", .. Bt709]),
        // BT.601 (SMPTE 170M) standard definition, tagged.
        ["h264-bt601"] = ("h264-bt601.mp4", [.. Source(720, 480, 30, 15), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-x264-params", "colorprim=smpte170m:transfer=smpte170m:colormatrix=smpte170m",
            "-colorspace", "smpte170m", "-color_primaries", "smpte170m", "-color_trc", "smpte170m"]),
        // Untagged: standard definition defaults to BT.601, HD to BT.709.
        ["h264-sd-untagged"] = ("h264-sd-untagged.mp4", [.. Source(720, 576, 25, 10), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p"]),
        ["h264-hd-untagged"] = ("h264-hd-untagged.mp4", [.. Source(1280, 720, 30, 10), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p"]),
        // Longer, for loops, seeks and back-pressure: 3 s at 30 fps.
        ["h264-3s"] = ("h264-3s.mkv", [.. Source(640, 360, 30, 90), "-c:v", "libx264", "-preset", "veryfast", "-bf", "3", "-g", "30", "-pix_fmt", "yuv420p", .. Bt709]),
        // Unsupported.
        ["h264-444"] = ("h264-444.mp4", [.. Source(320, 240, 30, 5), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv444p"]),
        ["h264-10bit"] = ("h264-10bit.mp4", [.. Source(320, 240, 30, 5), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p10le", .. Bt709]),
        ["mjpeg"] = ("mjpeg.mkv", [.. Source(320, 240, 30, 5), "-c:v", "mjpeg", "-pix_fmt", "yuvj420p"]),
        ["audio-only"] = ("audio-only.m4a", ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1", "-c:a", "aac"]),
        // Streamable (MPEG-TS) so a truncated copy still opens.
        ["h264-ts"] = ("h264.ts", [.. Source(640, 360, 30, 45), "-c:v", "libx264", "-preset", "veryfast", "-bf", "3", "-g", "15", "-pix_fmt", "yuv420p", .. Bt709]),
        // Two seconds per codec with B-frames (where the codec has them) and a keyframe every second, for seeks near the end.
        ["hevc-2s"] = ("hevc-2s.mkv", [.. Source(640, 360, 30, 60), "-c:v", "libx265", "-preset", "ultrafast", "-x265-params",
            "log-level=error:bframes=3:keyint=30:min-keyint=30", "-pix_fmt", "yuv420p", .. Bt709]),
        ["hevc10-2s"] = ("hevc10-2s.mkv", [.. Source(640, 360, 30, 60), "-c:v", "libx265", "-preset", "ultrafast", "-x265-params",
            "log-level=error:bframes=4:keyint=30:min-keyint=30", "-pix_fmt", "yuv420p10le", .. Bt709]),
        ["av1-2s"] = ("av1-2s.mkv", [.. Source(640, 360, 30, 60), "-c:v", "libsvtav1", "-preset", "12", "-g", "30", "-pix_fmt", "yuv420p10le", .. Bt709]),
        ["vp9-2s"] = ("vp9-2s.webm", [.. Source(640, 360, 30, 60), "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-g", "30", "-pix_fmt", "yuv420p", .. Bt709]),
        // MPEG-TS parts (each starts at 1.4 s) to concatenate: a second shot, and one 100 s later.
        ["ts-part-a"] = ("ts-part-a.ts", [.. Source(640, 360, 30, 30), "-c:v", "libx264", "-preset", "veryfast", "-bf", "2", "-g", "15", "-pix_fmt", "yuv420p", .. Bt709]),
        ["ts-part-b"] = ("ts-part-b.ts", ["-f", "lavfi", "-i", "testsrc=size=640x360:rate=30,trim=end_frame=30", "-c:v", "libx264", "-preset", "veryfast", "-bf", "2", "-g", "15",
            "-pix_fmt", "yuv420p", .. Bt709]),
        ["ts-part-b-late"] = ("ts-part-b-late.ts", ["-f", "lavfi", "-i", "testsrc=size=640x360:rate=30,trim=end_frame=30", "-c:v", "libx264", "-preset", "veryfast", "-bf", "2",
            "-g", "15", "-pix_fmt", "yuv420p", .. Bt709, "-output_ts_offset", "100"]),
        // MPEG-TS with a keyframe every second (B-frames), and with one keyframe only: FFmpeg's TS seek lands on whatever
        // packet is near the target's DTS.
        ["ts-2s"] = ("ts-2s.ts", [.. Source(640, 360, 30, 60), "-c:v", "libx264", "-preset", "veryfast", "-bf", "3", "-g", "30", "-pix_fmt", "yuv420p", .. Bt709]),
        ["ts-onekey"] = ("ts-onekey.ts", [.. Source(640, 360, 30, 60), "-c:v", "libx264", "-preset", "veryfast", "-bf", "3", "-g", "600", "-keyint_min", "600",
            "-sc_threshold", "0", "-pix_fmt", "yuv420p", .. Bt709]),
        // A 12 s still after frame 29 (frames 33 ms apart otherwise), in containers with a continuous timeline.
        ["gap-mkv"] = ("gap.mkv", [.. Source(640, 360, 30, 60), "-vf", "settb=1/1000,setpts='N*33+gt(N\\,29)*12000'", "-fps_mode", "passthrough",
            "-enc_time_base", "filter", "-c:v", "libx264", "-preset", "veryfast", "-bf", "0", "-pix_fmt", "yuv420p", .. Bt709]),
        ["gap-mp4"] = ("gap.mp4", [.. Source(640, 360, 30, 60), "-vf", "settb=1/1000,setpts='N*33+gt(N\\,29)*12000'", "-fps_mode", "passthrough",
            "-enc_time_base", "filter", "-c:v", "libx264", "-preset", "veryfast", "-bf", "0", "-pix_fmt", "yuv420p", .. Bt709]),
        // The 33-bit MPEG-TS clock wraps 0.52 s in (libavformat unwraps it).
        ["ts-wrap"] = ("ts-wrap.ts", [.. Source(640, 360, 30, 60), "-c:v", "libx264", "-preset", "veryfast", "-bf", "2", "-g", "15", "-pix_fmt", "yuv420p", .. Bt709,
            "-output_ts_offset", "95441.8"]),
    };

    /// <summary>name → the clips whose bytes, one after the other, make it (as `cat a.ts b.ts &gt; c.ts` does).</summary>
    public static readonly IReadOnlyDictionary<string, (string File, string[] Parts)> Concatenations = new Dictionary<string, (string, string[])>
    {
        // Timestamps step back from 2.43 s to 1.47 s at frame 30.
        ["ts-concat"] = ("ts-concat.ts", ["ts-part-a", "ts-part-b"]),
        // Timestamps jump forward from 2.43 s to 101.4 s at frame 30.
        ["ts-concat-late"] = ("ts-concat-late.ts", ["ts-part-a", "ts-part-b-late"]),
    };

    private static string[] Bt709 => ["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"];

    public static string Get(string name, string ffmpeg) => _made.GetOrAdd(name, n => new Lazy<string>(() => Make(n, ffmpeg))).Value;

    /// <summary>A moving test picture of exactly <paramref name="frames"/> frames (a lavfi graph input).</summary>
    private static string[] Source(int width, int height, int rate, int frames) =>
        ["-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"testsrc2=size={width}x{height}:rate={rate},trim=end_frame={frames}")];

    private static string Make(string name, string ffmpeg)
    {
        if (Concatenations.TryGetValue(name, out var concatenation))
        {
            var target = System.IO.Path.Combine(Directory, concatenation.File);
            using (var joined = System.IO.File.Create(target))
            {
                foreach (var part in concatenation.Parts)
                {
                    using var input = System.IO.File.OpenRead(Get(part, ffmpeg));
                    input.CopyTo(joined);
                }
            }

            return target;
        }

        var (file, args) = Specs[name];
        var path = System.IO.Path.Combine(Directory, file);
        var (exit, output) = Tools.Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", .. args, path]);
        Assert.True(exit == 0 && System.IO.File.Exists(path), $"ffmpeg could not make clip '{name}': {output}");
        return path;
    }
}

/// <summary>The run's clip directory, removed when the test assembly finishes.</summary>
public sealed class ClipDirectory : IDisposable
{
    private static readonly Lazy<string> _path = new(() =>
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fmp-media-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(path);
        return path;
    });

    public static string Path => _path.Value;

    public void Dispose()
    {
        if (_path.IsValueCreated)
        {
            try
            {
                System.IO.Directory.Delete(_path.Value, recursive: true);
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

/// <summary>
/// Tests that run alone, after every parallel test: the ones that measure this process's GPU memory, the context's
/// reference count or open descriptors, and the stall probe (other tests' CUDA work and frees would show in all of them).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialMediaDefinition
{
    public const string Name = "GPU memory, leaks and stalls (serial)";
}
