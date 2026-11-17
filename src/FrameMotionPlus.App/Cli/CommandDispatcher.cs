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
                "bench" => BenchCommand.Run(rest, output),
                "pattern" => PatternCommand.Run(rest, output, error),
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
          fmp bench jitter [options]
                                    Measure pacing-thread wake-up jitter on the real clock (design doc Phase 0)
          fmp pattern [options]     CUDA test pattern through the presentation ring to a window or offscreen (Phase 2)
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

        bench jitter options:
          --duration-s <s>          Measured time per loop, after the warm-up                 (10)
          --warmup-s <s>            Excluded from statistics at the start of each loop        (1)
          --block-s <s>             The full and timer-only loops alternate in blocks this long (5)
          --rate <hz>               Output slot rate (panel refresh): 120, 240 ...            (120)
          --source-fps <rate>       Synthetic source frame rate                               (60)
          --wait <sleep|hybrid>     clock_nanosleep to the deadline, or sleep then spin       (sleep)
          --spin-us <us>            Spin window before the deadline, with --wait hybrid       (200)
          --rt                      Ask for SCHED_FIFO (falls back to a nice value; reports what it got)
          --rt-priority <n>         SCHED_FIFO priority 1-99                                  (10)
          --nice <n>                Nice value to ask for, or the fallback when --rt is refused (-10)
          --pin-core <n>            Pin the pacing thread to this CPU
          --source-core <n>         Pin the source thread to this CPU
          --timer-slack-ns <ns>     Per-thread timer slack, set explicitly; 50000 is the usual kernel default (1)
          --d-ms <ms>               Generation time reported for each generated slot          (1.2)
          --gate-ms <ms>            Exit 1 if p99 wake lateness of the full loop exceeds this (0.5)
          --max-missed <n>          Exit 1 if the full loop misses more slots than this       (0)
          --no-baseline             Skip the timer-only loop (no OS-versus-ours attribution)
          --csv <path>              Write every sample of both loops
                                    Skipped slots count in the percentiles. Also exits 1 on any GC collection or
                                    hot-thread allocation in the measured time.

        pattern options:
          --size <WxH>              Pattern and ring size                                     (2560x1440)
          --format <8|10>           8-bit RGBA8 (SDR) or 10-bit RGB10A2 (PQ)                  (8)
          --duration-s <s>          Run length                                                (5)
          --frames <n>              Run this many refresh slots instead
          --offscreen               Headless: offscreen target, every frame read back and verified bit-exact
          --no-verify               With --offscreen: no readback (to measure costs alone)
          --unpaced                 With --offscreen: no pacing, no wait for a free image; as fast as back-pressure
                                    allows (a stress test: drops and held-back slots allowed, shown frames verified)
          --blend                   2x generator in the loop: real frames alternate with BlendGenerator at t = 0.5
          --validate                Vulkan validation layer with synchronization checks; any message fails the run
          --window <WxH>            Window size                                               (960x540)
          --rate <hz>               Refresh rate to pace at (offscreen 120; windowed: the display's)
          --slots <n>               Presentation ring depth, 4 to 6                           (4)
          --warmup-frames <n>       Excluded from timing and allocation statistics            (30)
                                    Exits 1 on any failure, a dropped frame offscreen, a validation message or an
                                    allocation on the present thread; 69 when no usable GPU, driver or display is found.
                                    Ctrl+C (SIGINT) or SIGTERM stops the run cleanly with its verdict; a second one
                                    ends the process at once. With --validate each validation message is also written
                                    to stderr as it arrives.
                                    Fault injection, for testing teardown: FMP_PATTERN_FAIL_AT=<slot> in the environment
                                    makes the loop fail between acquiring that slot's write and signalling it (the run
                                    then fails with that cause and must still tear down cleanly).

        Exit codes: 0 success, 1 check or validation failed, 2 usage error, 70 internal error; 69 when a GPU or display is required but not there.

        """;
}
