using System.Globalization;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.App.Vfg;

/// <summary>What the frames fed to VFG show.</summary>
internal enum VfgContentKind
{
    /// <summary>The project's test pattern (native/kernels/pattern.cu): its box moves 4 × W/240 px per source frame.</summary>
    Pattern = 0,

    /// <summary>A pan by whole pixels across a detailed synthetic texture: motion everywhere in the frame.</summary>
    Pan = 1,

    /// <summary>Frames decoded by the ffmpeg CLI from a file or an ffmpeg input (<c>--video</c>), scaled to the cell's size.</summary>
    Video = 2,
}

/// <summary>Configuration of <c>fmp bench vfg</c>.</summary>
internal sealed record VfgBenchOptions
{
    public IReadOnlyList<(int Width, int Height)> Sizes { get; init; } = [(1920, 1080), (2560, 1440), (3840, 2160)];

    public IReadOnlyList<FrameGenQuality> Modes { get; init; } = [FrameGenQuality.Low, FrameGenQuality.Medium, FrameGenQuality.High];

    public IReadOnlyList<PixelFormat> Formats { get; init; } = [PixelFormat.Rgba8, PixelFormat.Rgb10A2];

    /// <summary>2: one frame at t = 0.5 per source pair; 4: t = 0.25, 0.5 and 0.75.</summary>
    public IReadOnlyList<int> Multipliers { get; init; } = [2, 4];

    /// <summary>Generated frames measured per cell, after <see cref="WarmupFrames"/>.</summary>
    public int Frames { get; init; } = 600;

    public int WarmupFrames { get; init; } = 30;

    public VfgContentKind Content { get; init; } = VfgContentKind.Pattern;

    /// <summary>With <see cref="VfgContentKind.Video"/>: a file path, or "lavfi:SPEC" for an ffmpeg lavfi input.</summary>
    public string? Video { get; init; }

    /// <summary>
    /// Output refresh rate the runs are paced at: a generated frame goes out every slot except the real frame's, so
    /// 2x runs at rate / 2 per second and 4x at 3 × rate / 4 (the pipeline's cadence, with the GPU otherwise idle).
    /// </summary>
    public FrameRate Rate { get; init; } = FrameRate.FromHz(120);

    /// <summary>Back to back instead of paced (the GPU never idles between runs).</summary>
    public bool Unpaced { get; init; }

    /// <summary>VFG's automatic shot-change detection (on in the app). Off, a run only enqueues and Enqueue does not block.</summary>
    public bool ShotChangeDetection { get; init; } = true;

    /// <summary>Every n-th generated frame is read back and checked to be a genuine interpolation (0: never).</summary>
    public int VerifyEvery { get; init; } = 20;

    /// <summary>The gate: p99 of d at 1440p Medium, every format and multiplier (design doc Phase 0).</summary>
    public double GateMs { get; init; } = 7;

    public string? CsvPath { get; init; }

    public string? SdkDirectory { get; init; }

    public string? SettingsPath { get; init; }

    public static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal) { "unpaced" };

    public static VfgBenchOptions Parse(IReadOnlyList<string> args)
    {
        var r = new ArgumentReader(args, Flags);
        var o = new VfgBenchOptions
        {
            Sizes = r.String("size") is { } sizes ? List(sizes, "size", ParseSize) : [(1920, 1080), (2560, 1440), (3840, 2160)],
            Modes = r.String("mode") is { } modes ? List(modes, "mode", ParseMode) : [FrameGenQuality.Low, FrameGenQuality.Medium, FrameGenQuality.High],
            Formats = r.String("format") is { } formats ? List(formats, "format", ParseFormat) : [PixelFormat.Rgba8, PixelFormat.Rgb10A2],
            Multipliers = r.String("multiplier") is { } multipliers ? List(multipliers, "multiplier", ParseMultiplier) : [2, 4],
            Frames = (int)r.Int64("frames", 600, 10, 1_000_000),
            WarmupFrames = (int)r.Int64("warmup-frames", 30, 0, 100_000),
            Content = ParseContent(r.String("content")),
            Video = r.String("video"),
            Rate = r.Rate("rate", FrameRate.FromHz(120)),
            Unpaced = r.Flag("unpaced"),
            ShotChangeDetection = ParseOnOff(r.String("scd") ?? "on", "scd"),
            VerifyEvery = (int)r.Int64("verify-every", 20, 0, 1_000_000),
            GateMs = r.Double("gate-ms", 7, 0.01, 1000),
            CsvPath = r.String("csv"),
            SdkDirectory = r.String("vfx-sdk"),
            SettingsPath = r.String("settings"),
        };
        r.EnsureAllConsumed();
        if (o.Content == VfgContentKind.Video && string.IsNullOrEmpty(o.Video))
        {
            throw new UsageException("'--content video' needs '--video <file or lavfi:SPEC>'.");
        }

        if (o.Video is not null && o.Content != VfgContentKind.Video)
        {
            o = o with { Content = VfgContentKind.Video };
        }

        if (o.Unpaced && args.Any(static a => a.StartsWith("--rate", StringComparison.Ordinal)))
        {
            throw new UsageException("'--rate' does not apply with '--unpaced'.");
        }

        return o;
    }

    public int Cells => Sizes.Count * Modes.Count * Formats.Count * Multipliers.Count;

    public static string SizeName((int Width, int Height) size) => size switch
    {
        (1920, 1080) => "1080p",
        (2560, 1440) => "1440p",
        (3840, 2160) => "2160p",
        _ => string.Create(CultureInfo.InvariantCulture, $"{size.Width}x{size.Height}"),
    };

    public static string FormatName(PixelFormat format) => format == PixelFormat.Rgb10A2 ? "RGB10A2" : format == PixelFormat.Bgra8 ? "BGRA8" : "RGBA8";

    private static List<T> List<T>(string text, string name, Func<string, T> parse)
    {
        var items = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0)
        {
            throw new UsageException($"Option '--{name}' needs at least one value.");
        }

        var list = new List<T>();
        foreach (var item in items)
        {
            var value = parse(item);
            if (!list.Contains(value))
            {
                list.Add(value);
            }
        }

        return list;
    }

    internal static (int Width, int Height) ParseSize(string text)
    {
        switch (text.ToLowerInvariant())
        {
            case "1080p":
                return (1920, 1080);
            case "1440p":
                return (2560, 1440);
            case "2160p" or "4k":
                return (3840, 2160);
            case "720p":
                return (1280, 720);
        }

        var x = text.IndexOfAny(['x', 'X']);
        if (x <= 0
            || !int.TryParse(text.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(text.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || w < 64 || h < 64 || w > 8192 || h > 8192)
        {
            throw new UsageException($"Option '--size' expects 1080p, 1440p, 2160p or WIDTHxHEIGHT (64 to 8192 each), got '{text}'.");
        }

        return (w, h);
    }

    internal static FrameGenQuality ParseMode(string text) => text.ToLowerInvariant() switch
    {
        "low" => FrameGenQuality.Low,
        "medium" => FrameGenQuality.Medium,
        "high" => FrameGenQuality.High,
        _ => throw new UsageException($"Option '--mode' expects low, medium or high, got '{text}'."),
    };

    internal static PixelFormat ParseFormat(string text) => text.ToLowerInvariant() switch
    {
        "8" or "rgba8" or "sdr" => PixelFormat.Rgba8,
        "10" or "rgb10a2" or "hdr" or "pq" => PixelFormat.Rgb10A2,
        _ => throw new UsageException($"Option '--format' expects 8 or 10 (rgba8, rgb10a2), got '{text}'."),
    };

    private static int ParseMultiplier(string text) => text switch
    {
        "2" or "2x" => 2,
        "4" or "4x" => 4,
        _ => throw new UsageException($"Option '--multiplier' expects 2 or 4, got '{text}'."),
    };

    private static VfgContentKind ParseContent(string? text) => text?.ToLowerInvariant() switch
    {
        null or "pattern" => VfgContentKind.Pattern,
        "pan" => VfgContentKind.Pan,
        "video" => VfgContentKind.Video,
        _ => throw new UsageException($"Option '--content' expects pattern, pan or video, got '{text}'."),
    };

    internal static bool ParseOnOff(string text, string name) => text.ToLowerInvariant() switch
    {
        "on" or "true" or "1" => true,
        "off" or "false" or "0" => false,
        _ => throw new UsageException($"Option '--{name}' expects on or off, got '{text}'."),
    };
}
