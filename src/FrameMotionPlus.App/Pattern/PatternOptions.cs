using System.Globalization;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.App.Pattern;

/// <summary>Configuration of <c>fmp pattern</c>.</summary>
internal sealed record PatternOptions
{
    public int Width { get; init; } = 2560;

    public int Height { get; init; } = 1440;

    /// <summary><see cref="PixelFormat.Rgba8"/> (8-bit SDR) or <see cref="PixelFormat.Rgb10A2"/> (10-bit PQ).</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba8;

    /// <summary>Run length when <see cref="Frames"/> is not given.</summary>
    public double DurationSeconds { get; init; } = 5;

    /// <summary>Output frames (refresh slots) to run; overrides <see cref="DurationSeconds"/>.</summary>
    public long? Frames { get; init; }

    /// <summary>Headless: an offscreen target the ring's size, every frame read back and verified.</summary>
    public bool Offscreen { get; init; }

    /// <summary>A 2x generator in the loop: real frames alternate with BlendGenerator output at t = 0.5.</summary>
    public bool Blend { get; init; }

    /// <summary>The Vulkan validation layer (core and synchronization checks); any message fails the run.</summary>
    public bool Validate { get; init; }

    /// <summary>Offscreen: read back and check every frame (off only to measure costs without the readback).</summary>
    public bool Verify { get; init; } = true;

    /// <summary>
    /// Offscreen, a stress mode: no pacing and no wait for a free image, every slot acquired, produced and handed to the
    /// presenter as fast as the bridge's back-pressure allows. Frames may be dropped or held back (not failures here);
    /// shown frames are still verified, and the bridge and presenter must keep going on their own rules alone.
    /// </summary>
    public bool Unpaced { get; init; }

    public int WindowWidth { get; init; } = 960;

    public int WindowHeight { get; init; } = 540;

    /// <summary>Refresh rate to run at. Offscreen default 120 Hz; windowed default the display's.</summary>
    public FrameRate? Rate { get; init; }

    public int Slots { get; init; } = 4;

    /// <summary>Frames at the start excluded from timing and allocation statistics (first use of every path).</summary>
    public int WarmupFrames { get; init; } = 30;

    public static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal)
    {
        "offscreen", "blend", "validate", "no-verify", "unpaced",
    };

    public static PatternOptions Parse(IReadOnlyList<string> args)
    {
        var r = new ArgumentReader(args, Flags);
        var (width, height) = Size(r, "size", 2560, 1440, 16384);
        var (windowWidth, windowHeight) = Size(r, "window", 960, 540, 8192);
        var offscreen = r.Flag("offscreen");
        var o = new PatternOptions
        {
            Width = width,
            Height = height,
            Format = ParseFormat(r.String("format")),
            DurationSeconds = r.Double("duration-s", 5, 0.05, 3600),
            Frames = r.Has("frames") ? r.Int64("frames", 0, 1, 2_000_000) : null,
            Offscreen = offscreen,
            Blend = r.Flag("blend"),
            Validate = r.Flag("validate"),
            Verify = !r.Flag("no-verify"),
            Unpaced = r.Flag("unpaced"),
            WindowWidth = windowWidth,
            WindowHeight = windowHeight,
            Rate = r.Has("rate") ? r.Rate("rate", default) : null,
            Slots = (int)r.Int64("slots", 4, 4, 6),
            WarmupFrames = (int)r.Int64("warmup-frames", 30, 0, 100_000),
        };
        r.EnsureAllConsumed();
        if (!o.Verify && !offscreen)
        {
            throw new UsageException("'--no-verify' only applies with '--offscreen' (windowed frames are never read back).");
        }

        if (o.Unpaced && !offscreen)
        {
            throw new UsageException("'--unpaced' only applies with '--offscreen' (a window is paced by its display).");
        }

        if (args.Any(static a => a.StartsWith("--window", StringComparison.Ordinal)) && offscreen)
        {
            throw new UsageException("'--window' does not apply with '--offscreen': the offscreen target is the pattern's size (1:1, so frames verify bit-exact).");
        }

        // The frame counter needs 32 cells of at least one pixel; the bands need a few rows each.
        if (o.Width < 32 || o.Height < 12)
        {
            throw new UsageException("'--size' must be at least 32x12 (the frame counter is 32 cells wide).");
        }

        return o;
    }

    public string FormatName => Format == PixelFormat.Rgb10A2 ? "10-bit (RGB10A2, PQ)" : "8-bit (RGBA8, SDR)";

    private static PixelFormat ParseFormat(string? text) => text?.ToLowerInvariant() switch
    {
        null or "8" or "rgba8" or "sdr" => PixelFormat.Rgba8,
        "10" or "rgb10a2" or "hdr" or "pq" => PixelFormat.Rgb10A2,
        _ => throw new UsageException($"Option '--format' expects 8 or 10 (rgba8, rgb10a2), got '{text}'."),
    };

    private static (int Width, int Height) Size(ArgumentReader r, string name, int width, int height, int max)
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
            || w < 1 || h < 1 || w > max || h > max)
        {
            throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"Option '--{name}' expects WIDTHxHEIGHT (1 to {max} each), got '{text}'."));
        }

        return (w, h);
    }
}
