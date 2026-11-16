using System.Reflection;

namespace FrameMotionPlus.App.Cli;

internal static class CommandDispatcher
{
    public const int ExitOk = 0;
    public const int ExitCheckFailed = 1;
    public const int ExitUsage = 2;

    /// <summary>A bug: an exception nothing else handled. EX_SOFTWARE in sysexits.h.</summary>
    public const int ExitInternalError = 70;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.Write(Usage);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        if (args[0] is "--version" or "version")
        {
            output.WriteLine($"fmp {Version}");
            return ExitOk;
        }

        var rest = args.AsSpan(1).ToArray();
        try
        {
            return args[0] switch
            {
                "simulate" => SimulateCommand.Run(rest, output),
                "settings" => SettingsCommand.Run(rest, output),
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException e)
        {
            error.WriteLine($"fmp: {e.Message}");
            error.WriteLine("Run 'fmp --help' for usage.");
            return ExitUsage;
        }
        catch (Exception e)
        {
            // Last-chance handler: one line and an exit code instead of an unhandled-exception crash (exit 134).
            error.WriteLine($"fmp: internal error: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ").Trim()}");
            return ExitInternalError;
        }
    }

    public static string Version =>
        typeof(CommandDispatcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    private const string Usage =
        """
        fmp - Frame Motion Plus: live frame generation (PS5 capture, file, Remote Play)

        Usage:
          fmp simulate [options]    Run the real scheduler against a simulated source and display
          fmp settings [options]    Print default settings JSON, or validate a settings file
          fmp --version
          fmp --help

        simulate options:
          --source-fps <rate>       Unique source frame rate: 60, 59.94, 30, 60000/1001 ...   (60)
          --display-hz <rate>       Panel refresh, or maximum refresh under VRR               (120)
          --clock <fixed|vrr>       Slot clock: panel vblanks, or follow the source           (fixed)
          --d-ms <ms>               Simulated generation time per generated frame             (2)
          --d-jitter-ms <ms>        Uniform +/- jitter on the generation time                 (0)
          --duration-s <s>          Simulated duration                                        (60)
          --warmup-s <s>            Excluded from statistics                                  (1)
          --margin-ms <ms>          Scheduler safety margin                                   (0.5)
          --max-multiplier <n>      Cap on output frames per source frame                     (8)
          --drop-rate <p>           Probability of dropping a source frame                    (0)
          --frame-jitter-ms <ms>    Uniform +/- jitter on source frame times                  (0)
          --skew-ppm <ppm>          Source clock error versus the display clock               (0)
          --phase-ms <ms>           Source content-time offset against the vblank grid        (0)
          --hold-at-s <s>           Start of a source hold (no new frames)                    (none)
          --hold-for-s <s>          Length of that hold                                       (2)
          --ingest-ms <ms>          Capture-to-GPU availability latency                       (0)
          --ingest-jitter-ms <ms>   Extra uniform [0, J] availability delay per frame         (0)
          --wake-late-us <us>       Scheduling thread wakes this late for every decision      (0)
          --wake-jitter-us <us>     Extra uniform [0, J] wake lateness                        (0)
          --stall-at-s <s>          Scheduling thread blocked once from this time             (none)
          --stall-ms <ms>           Length of that stall                                      (400)
          --rate-change-at-s <s>    Source switches rate at this content time                 (none)
          --rate-after <rate>       The rate it switches to
          --deliver-ahead-ms <ms>   Hand frames to the scheduler before they are available    (0)
          --seed <n>                Random seed                                               (1)
          --settings <file>         Base scheduler/display settings from a JSON file
          --csv <path>              Write per-slot telemetry CSV
          --check                   Exit 1 unless the run meets the design doc: no missed slot, regression,
                                    causality or timestep error; on ideal inputs also the {k/M} pattern,
                                    the real:generated ratio and the latency band from the latency model

        settings options:
          --validate <file>         Validate a settings file instead of printing defaults

        Exit codes: 0 success, 1 check or validation failed, 2 usage error, 70 internal error.

        """;
}
