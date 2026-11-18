using System.Globalization;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.App.Play;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Sources.V4l2;

namespace FrameMotionPlus.App.Capture;

/// <summary>A named capture mode: what to ask the device for and the HDR decision that goes with it.</summary>
internal sealed record CapturePreset(string Name, int Width, int Height, double Fps, CapturePixelFormat Format, HdrMode Hdr, string Note)
{
    /// <summary>The presets, the owner's target first (4K HDR, 30 to 60).</summary>
    public static readonly IReadOnlyList<CapturePreset> All =
    [
        new("4k30-hdr", 3840, 2160, 30, CapturePixelFormat.P010, HdrMode.On, "3840x2160 P010 at 30 fps, HDR10 (the Elgato 4K X's 10-bit 4K mode; generate to 60)"),
        new("1440p60-hdr", 2560, 1440, 60, CapturePixelFormat.P010, HdrMode.On, "2560x1440 P010 at 60 fps, HDR10 (for 60 fps games; generate to 120)"),
        new("4k60-sdr", 3840, 2160, 60, CapturePixelFormat.Nv12, HdrMode.Off, "3840x2160 NV12 at 60 fps, SDR (8-bit)"),
        new("1440p60-sdr", 2560, 1440, 60, CapturePixelFormat.Nv12, HdrMode.Off, "2560x1440 NV12 at 60 fps, SDR"),
        new("1440p120-sdr", 2560, 1440, 120, CapturePixelFormat.Nv12, HdrMode.Off, "2560x1440 NV12 at 120 fps, SDR"),
        new("1080p60-sdr", 1920, 1080, 60, CapturePixelFormat.Nv12, HdrMode.Off, "1920x1080 NV12 at 60 fps, SDR"),
        new("1080p120-sdr", 1920, 1080, 120, CapturePixelFormat.Nv12, HdrMode.Off, "1920x1080 NV12 at 120 fps, SDR"),
    ];

    public static CapturePreset Parse(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new UsageException($"Option '--mode' expects one of {string.Join(", ", All.Select(p => p.Name))}, got '{name}'.");
}

/// <summary>Configuration of <c>fmp capture</c>.</summary>
internal sealed record CaptureOptions
{
    /// <summary>The device node; null: the first capture device with a 10-bit mode, else the first capture device.</summary>
    public string? Device { get; init; }

    public bool List { get; init; }

    public CapturePreset? Preset { get; init; }

    public CaptureRequest Request { get; init; } = new();

    public CaptureColorOverrides Color { get; init; } = new();

    public CaptureBufferMode BufferMode { get; init; } = CaptureBufferMode.Auto;

    public int Buffers { get; init; } = 4;

    public bool Cadence { get; init; } = true;

    public GeneratorChoice Generator { get; init; } = GeneratorChoice.Auto;

    public FrameGenQuality Quality { get; init; } = FrameGenQuality.Medium;

    public int Multiplier { get; init; } = 2;

    public double? OutputHz { get; init; }

    public double DurationS { get; init; }

    public int WindowWidth { get; init; } = 1280;

    public int WindowHeight { get; init; } = 720;

    /// <summary>Borderless fullscreen (the default unless --windowed or --window is given, or --offscreen).</summary>
    public bool Fullscreen { get; init; }

    /// <summary>--clock: fixed refresh, variable refresh, or auto (variable when fullscreen and the display follows the presents).</summary>
    public Pipeline.RefreshMode Refresh { get; init; } = Pipeline.RefreshMode.Auto;

    public bool Offscreen { get; init; }

    public FrameRate OffscreenRate { get; init; } = FrameRate.FromHz(120);

    public bool Validate { get; init; }

    public string? CsvPath { get; init; }

    /// <summary>Write every dequeued buffer (sequence, timestamp, dequeue and judge times, cadence metric, decision) here.</summary>
    public string? FramesCsvPath { get; init; }

    public string? SettingsPath { get; init; }

    public string? VfxSdk { get; init; }

    public double WarmupS { get; init; } = 2;

    public double PresentLeadMs { get; init; } = -1;

    public double LatchMs { get; init; } = 1;

    public CudaSchedulingMode CudaSync { get; init; } = CudaSchedulingMode.BlockingSync;

    public static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal)
    {
        "list", "fullscreen", "windowed", "offscreen", "validate", "no-cadence",
    };

    public static CaptureOptions Parse(IReadOnlyList<string> args)
    {
        string? device = null;
        var rest = args;
        if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            device = args[0];
            rest = [.. args.Skip(1)];
        }

        var r = new ArgumentReader(rest, Flags);
        var list = r.Flag("list");
        var preset = r.String("mode") is { } mode ? CapturePreset.Parse(mode) : null;
        var (width, height) = Size(r, "size", 0, 0, 16, 16384);
        var fps = r.Double("fps", 0, 1, 1000);
        var format = ParseFormat(r.String("format"));
        if (preset is not null && (width > 0 || fps > 0 || format != CapturePixelFormat.Unknown))
        {
            throw new UsageException("Give --mode, or --size/--fps/--format, not both.");
        }

        var request = preset is not null
            ? new CaptureRequest { Width = preset.Width, Height = preset.Height, Fps = preset.Fps, Format = preset.Format }
            : new CaptureRequest { Width = width, Height = height, Fps = fps, Format = format };
        var hdrText = r.String("hdr");
        var hdr = hdrText is null ? preset?.Hdr ?? HdrMode.TenBit : ParseHdr(hdrText);
        var offscreen = r.Flag("offscreen");
        var fullscreen = r.Flag("fullscreen");
        var windowed = r.Flag("windowed") || r.Has("window");
        if (fullscreen && r.Flag("windowed"))
        {
            throw new UsageException("'--fullscreen' and '--windowed' contradict each other.");
        }

        var (windowWidth, windowHeight) = Size(r, "window", 1280, 720, 64, 8192);
        var o = new CaptureOptions
        {
            Device = device,
            List = list,
            Preset = preset,
            Request = request,
            Color = new CaptureColorOverrides
            {
                Hdr = hdr,
                Range = ParseRange(r.String("range")),
                Matrix = ParseMatrix(r.String("matrix")),
            },
            BufferMode = ParseBufferMode(r.String("buffer-mode")),
            Buffers = (int)r.Int64("buffers", 4, 2, 16),
            Cadence = !r.Flag("no-cadence"),
            Generator = ParseGenerator(r.String("generator")),
            Quality = r.String("quality") is { } quality ? Vfg.VfgBenchOptions.ParseMode(quality) : FrameGenQuality.Medium,
            Multiplier = (int)r.Int64("multiplier", 2, 1, 8),
            OutputHz = r.Has("output-hz") ? r.Double("output-hz", 0, 1, 1000) : null,
            DurationS = r.Double("duration-s", 0, 0.05, 86_400),
            WindowWidth = windowWidth,
            WindowHeight = windowHeight,
            // Fullscreen by default: there KWin gives the window variable refresh, which --clock auto then uses (the
            // lowest latency measured, Phase 6); --windowed or a --window size keeps a window (fixed refresh).
            Fullscreen = !offscreen && (fullscreen || !windowed),
            Refresh = r.String("clock")?.ToLowerInvariant() switch
            {
                null or "auto" => Pipeline.RefreshMode.Auto,
                "fixed" => Pipeline.RefreshMode.Fixed,
                "vrr" => Pipeline.RefreshMode.Vrr,
                var v => throw new UsageException($"Option '--clock' expects auto, fixed or vrr, got '{v}'."),
            },
            Offscreen = offscreen,
            OffscreenRate = r.Rate("display-hz", FrameRate.FromHz(120)),
            Validate = r.Flag("validate"),
            CsvPath = r.String("csv"),
            FramesCsvPath = r.String("frames-csv"),
            SettingsPath = r.String("settings"),
            VfxSdk = r.String("vfx-sdk"),
            WarmupS = r.Double("warmup-s", 2, 0, 600),
            PresentLeadMs = r.Double("present-lead-ms", -1, -1, 100),
            LatchMs = r.Double("latch-ms", 1, 0, 50),
            CudaSync = ParseSync(r.String("cuda-sync")),
        };
        r.EnsureAllConsumed();
        if (o.OutputHz is not null && rest.Any(static a => a.StartsWith("--multiplier", StringComparison.Ordinal)))
        {
            throw new UsageException("Give --multiplier or --output-hz, not both.");
        }

        if (!offscreen && rest.Any(static a => a.StartsWith("--display-hz", StringComparison.Ordinal)))
        {
            throw new UsageException("'--display-hz' only applies with '--offscreen'.");
        }

        if (offscreen && (fullscreen || rest.Any(static a => a.StartsWith("--window", StringComparison.Ordinal))))
        {
            throw new UsageException("'--window' and '--fullscreen' do not apply with '--offscreen'.");
        }

        if (list && rest.Count > 1)
        {
            throw new UsageException("'--list' takes no other option (an optional device first: fmp capture /dev/video0 --list).");
        }

        return o;
    }

    /// <summary>The generator choice as <c>fmp play</c> takes it (shared resolution rules).</summary>
    public PlayOptions AsPlayOptions(string device) => new()
    {
        Path = device,
        Generator = Generator,
        Quality = Quality,
        Multiplier = Multiplier,
        OutputHz = OutputHz,
        VfxSdk = VfxSdk,
        CudaSync = CudaSync,
        Validate = Validate,
    };

    private static CapturePixelFormat ParseFormat(string? text) => text?.ToLowerInvariant() switch
    {
        null => CapturePixelFormat.Unknown,
        "nv12" => CapturePixelFormat.Nv12,
        "p010" => CapturePixelFormat.P010,
        "yuyv" => CapturePixelFormat.Yuyv,
        "mjpg" or "mjpeg" or "jpeg" => throw new UsageException("MJPEG is never used: it needs a decode step and adds latency; give nv12, p010 or yuyv."),
        _ => throw new UsageException($"Option '--format' expects nv12, p010 or yuyv, got '{text}'."),
    };

    private static HdrMode ParseHdr(string text) => text.ToLowerInvariant() switch
    {
        "on" => HdrMode.On,
        "off" => HdrMode.Off,
        "auto" => HdrMode.Auto,
        "10bit" => HdrMode.TenBit,
        _ => throw new UsageException($"Option '--hdr' expects on, off, auto (as the device reports) or 10bit (on for 10-bit captures), got '{text}'."),
    };

    private static ColorRange? ParseRange(string? text) => text?.ToLowerInvariant() switch
    {
        null or "auto" => null,
        "limited" or "tv" => ColorRange.Limited,
        "full" or "pc" => ColorRange.Full,
        _ => throw new UsageException($"Option '--range' expects limited, full or auto, got '{text}'."),
    };

    private static MatrixCoefficients? ParseMatrix(string? text) => text?.ToLowerInvariant() switch
    {
        null or "auto" => null,
        "bt709" or "709" => MatrixCoefficients.Bt709,
        "bt2020" or "2020" => MatrixCoefficients.Bt2020NonConstant,
        "bt601" or "601" => MatrixCoefficients.Bt601,
        _ => throw new UsageException($"Option '--matrix' expects bt709, bt2020, bt601 or auto, got '{text}'."),
    };

    private static CaptureBufferMode ParseBufferMode(string? text) => text?.ToLowerInvariant() switch
    {
        null or "auto" => CaptureBufferMode.Auto,
        "userptr" => CaptureBufferMode.UserPtrPinned,
        "userptr-registered" => CaptureBufferMode.UserPtrRegistered,
        "mmap" => CaptureBufferMode.MmapRegistered,
        _ => throw new UsageException($"Option '--buffer-mode' expects auto, userptr, userptr-registered or mmap, got '{text}'."),
    };

    private static GeneratorChoice ParseGenerator(string? text) => text?.ToLowerInvariant() switch
    {
        null or "auto" => GeneratorChoice.Auto,
        "vfg" => GeneratorChoice.Vfg,
        "blend" => GeneratorChoice.Blend,
        "repeat" => GeneratorChoice.Repeat,
        "none" or "off" => GeneratorChoice.None,
        _ => throw new UsageException($"Option '--generator' expects auto, vfg, blend, repeat or none, got '{text}'."),
    };

    private static CudaSchedulingMode ParseSync(string? text) => text?.ToLowerInvariant() switch
    {
        null or "blocking" => CudaSchedulingMode.BlockingSync,
        "spin" => CudaSchedulingMode.Spin,
        "auto" => CudaSchedulingMode.Auto,
        "yield" => CudaSchedulingMode.Yield,
        _ => throw new UsageException($"Option '--cuda-sync' expects blocking, spin, yield or auto, got '{text}'."),
    };

    private static (int Width, int Height) Size(ArgumentReader r, string name, int width, int height, int min, int max)
    {
        var text = r.String(name);
        if (text is null)
        {
            return (width, height);
        }

        var x = text.IndexOfAny(['x', 'X']);
        if (x <= 0
            || !int.TryParse(text.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(text.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || w < min || h < min || w > max || h > max)
        {
            throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"Option '--{name}' expects WIDTHxHEIGHT ({min} to {max} each), got '{text}'."));
        }

        return (w, h);
    }
}
