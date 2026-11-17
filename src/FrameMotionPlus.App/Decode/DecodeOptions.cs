using FrameMotionPlus.App.Cli;

namespace FrameMotionPlus.App.Decode;

/// <summary>Configuration of <c>fmp decode</c>.</summary>
internal sealed record DecodeOptions
{
    public required string Path { get; init; }

    /// <summary>Frames to read; 0: the whole file (with <see cref="Loop"/>: three laps, or 600 frames when the length is unknown).</summary>
    public long Frames { get; init; }

    /// <summary>Read for this long instead (0: not limited by time).</summary>
    public double DurationS { get; init; }

    public bool Loop { get; init; }

    /// <summary>Check every frame: decoded planes bit-exact against the ffmpeg CLI's software decode, RGB against the CPU reference.</summary>
    public bool Verify { get; init; }

    /// <summary>The ffmpeg CLI for <see cref="Verify"/> (default: <c>ffmpeg</c> on PATH; ffprobe is looked for beside it).</summary>
    public string Ffmpeg { get; init; } = "ffmpeg";

    public int DecodeAhead { get; init; } = 4;

    /// <summary>Run a thread that launches small kernels on its own stream throughout and report its worst call.</summary>
    public bool StallProbe { get; init; } = true;

    public string? CsvPath { get; init; }

    public static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal) { "loop", "verify", "no-stall-probe" };

    public static DecodeOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new UsageException("decode needs a file: fmp decode <file> [options].");
        }

        var r = new ArgumentReader([.. args.Skip(1)], Flags);
        var o = new DecodeOptions
        {
            Path = args[0],
            Frames = r.Int64("frames", 0, 1, 100_000_000),
            DurationS = r.Double("duration-s", 0, 0.001, 86_400),
            Loop = r.Flag("loop"),
            Verify = r.Flag("verify"),
            Ffmpeg = r.String("ffmpeg") ?? "ffmpeg",
            DecodeAhead = (int)r.Int64("decode-ahead", 4, 1, 32),
            StallProbe = !r.Flag("no-stall-probe"),
            CsvPath = r.String("csv"),
        };
        r.EnsureAllConsumed();
        if (o.Frames > 0 && o.DurationS > 0)
        {
            throw new UsageException("Give --frames or --duration-s, not both.");
        }

        return o;
    }
}
