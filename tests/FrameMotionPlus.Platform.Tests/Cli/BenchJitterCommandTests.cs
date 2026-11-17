using System.Runtime.ExceptionServices;
using FrameMotionPlus.App.Bench;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Tests.Support;
using FrameMotionPlus.Platform.Threading;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Platform.Tests.Cli;

/// <summary>
/// <c>fmp bench jitter</c> in-process. Runs are kept to a fraction of a second, and their timing is never judged: in
/// this test host other tests allocate (so GCs happen) and CI runners are noisy, so a short run may legitimately
/// fail its gate. What is asserted is that it runs, measures both phases and reports, with exit 0 or 1.
/// </summary>
public sealed class BenchJitterCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fmp-bench-tests-" + Guid.NewGuid().ToString("N"));

    public BenchJitterCommandTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var timeout = TimeSpan.FromSeconds(60);
        (int Exit, string Out, string Err) result = default;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                var exit = CommandDispatcher.Run(args, output, error);
                result = (exit, output.ToString(), error.ToString());
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        }) { IsBackground = true, Name = "fmp-cli" };
        thread.Start();
        Assert.True(thread.Join(timeout), $"'fmp {string.Join(' ', args)}' did not return within {timeout.TotalSeconds:F0} s");
        failure?.Throw();
        return result;
    }

    [Fact]
    public void Defaults_are_the_design_doc_setup()
    {
        var o = JitterBenchOptions.Parse([]);

        Assert.Equal(10 * Nanoseconds.PerSecond, o.DurationNs);
        Assert.Equal(Nanoseconds.PerSecond, o.WarmupNs);
        Assert.Equal(5 * Nanoseconds.PerSecond, o.BlockNs);
        Assert.Equal(0, o.MaxMissed);
        Assert.Equal(FrameRate.FromHz(120), o.Rate);
        Assert.Equal(FrameRate.FromHz(60), o.SourceRate);
        Assert.Equal(WaitStrategy.Sleep, o.Wait);
        Assert.False(o.RealTime);
        Assert.Null(o.Nice);
        Assert.Null(o.PacingCore);
        Assert.Null(o.SourceCore);
        Assert.Equal(1, o.TimerSlackNs);
        Assert.Equal(500_000, o.GateNs);
        Assert.True(o.Baseline);
        Assert.Null(o.CsvPath);
        Assert.Equal(RealTimePolicy.None, o.PacingRequest.RealTime);
        Assert.Null(o.PacingRequest.Nice);
    }

    [Fact]
    public void Every_option_is_parsed()
    {
        var o = JitterBenchOptions.Parse([
            "--duration-s", "2", "--warmup-s", "0.5", "--rate", "240", "--source-fps", "59.94", "--wait", "sleep",
            "--rt", "--rt-priority", "20", "--nice", "-5", "--pin-core", "3", "--source-core", "4", "--timer-slack-ns", "50000",
            "--d-ms", "2", "--gate-ms", "1", "--max-missed", "3", "--block-s", "2", "--no-baseline", "--csv", "x.csv",
        ]);

        Assert.Equal(2 * Nanoseconds.PerSecond, o.DurationNs);
        Assert.Equal(Nanoseconds.PerSecond / 2, o.WarmupNs);
        Assert.Equal(FrameRate.FromHz(240), o.Rate);
        Assert.Equal(new FrameRate(60000, 1001), o.SourceRate);
        Assert.Equal(WaitStrategy.Sleep, o.Wait);
        Assert.Equal(0, o.SpinWindowNs);
        Assert.True(o.RealTime);
        Assert.Equal(-5, o.Nice);
        Assert.Equal(50_000, o.TimerSlackNs);
        Assert.Equal(3, o.MaxMissed);
        Assert.Equal(2 * Nanoseconds.PerSecond, o.BlockNs);
        Assert.Equal(2 * Nanoseconds.PerMillisecond, o.GenerationTimeNs);
        Assert.Equal(Nanoseconds.PerMillisecond, o.GateNs);
        Assert.False(o.Baseline);
        Assert.Equal("x.csv", o.CsvPath);

        var pacing = o.PacingRequest;
        Assert.Equal(RealTimePolicy.Fifo, pacing.RealTime);
        Assert.Equal(20, pacing.RealTimePriority);
        Assert.Equal(-5, pacing.Nice);
        Assert.Equal(3, pacing.Core);
        Assert.Equal(50_000, pacing.TimerSlackNs);
        Assert.Equal(4, o.SourceRequest.Core);
        Assert.Equal(RealTimePolicy.Fifo, o.SourceRequest.RealTime);
    }

    [Fact]
    public void Real_time_falls_back_to_nice_minus_10_unless_told_otherwise()
    {
        var o = JitterBenchOptions.Parse(["--rt", "--wait", "hybrid", "--spin-us", "100"]);

        Assert.Equal(-10, o.PacingRequest.Nice);
        Assert.Equal(100_000, o.SpinWindowNs);
        Assert.Equal("hybrid (spin 100 us)", o.WaitDescription);
    }

    [Theory]
    [InlineData("bench")]
    [InlineData("bench", "nope")]
    [InlineData("bench", "jitter", "--wait", "fast")]
    [InlineData("bench", "jitter", "--wait", "sleep", "--spin-us", "100")]
    [InlineData("bench", "jitter", "--spin-us", "100")]
    [InlineData("bench", "jitter", "--wait", "hybrid", "--spin-us", "9000")]
    [InlineData("bench", "jitter", "--wait", "hybrid", "--rate", "240", "--spin-us", "4200")]
    [InlineData("bench", "jitter", "--rate", "0")]
    [InlineData("bench", "jitter", "--rate", "5000")]
    [InlineData("bench", "jitter", "--duration-s", "0")]
    [InlineData("bench", "jitter", "--rt-priority", "100")]
    [InlineData("bench", "jitter", "--nice", "-21")]
    [InlineData("bench", "jitter", "--pin-core", "-1")]
    [InlineData("bench", "jitter", "--gate-ms", "0")]
    [InlineData("bench", "jitter", "--csv")]
    [InlineData("bench", "jitter", "--timer-slack-ns", "0")]
    [InlineData("bench", "jitter", "--max-missed", "-1")]
    [InlineData("bench", "jitter", "--block-s", "0.1")]
    [InlineData("bench", "jitter", "--duration-s", "86400", "--warmup-s", "3600", "--rate", "1000")]
    [InlineData("bench", "jitter", "--bogus", "1")]
    [InlineData("bench", "jitter", "extra")]
    public void Invalid_arguments_are_usage_errors(params string[] args)
    {
        var (exit, output, error) = Run(args);

        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Empty(output);
        Assert.StartsWith("fmp: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unwritable_csv_path_is_a_usage_error_before_measuring()
    {
        var (exit, output, error) = Run("bench", "jitter", "--csv", Path.Combine(_directory, "missing", "dir", "x.csv"));

        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Empty(output);
        Assert.Contains("Cannot write", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_lists_the_command()
    {
        var (exit, output, _) = Run("--help");

        Assert.Equal(CommandDispatcher.ExitOk, exit);
        Assert.Contains("fmp bench jitter", output, StringComparison.Ordinal);
        Assert.Contains("--spin-us", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hybrid")]
    [InlineData("sleep")]
    public void A_short_run_measures_both_phases_and_writes_every_sample(string wait)
    {
        var csv = Path.Combine(_directory, wait + ".csv");
        var (exit, output, error) = Run(
            "bench", "jitter", "--duration-s", "0.3", "--warmup-s", "0.1", "--rate", "240", "--wait", wait, "--rt", "--csv", csv);

        Assert.True(exit is CommandDispatcher.ExitOk or CommandDispatcher.ExitCheckFailed, $"exit {exit}: {error}\n{output}");
        Assert.Empty(error);
        Assert.Contains("wake lateness (us)", output, StringComparison.Ordinal);
        Assert.Contains("    timer only ", output, StringComparison.Ordinal);
        Assert.Contains("    full loop ", output, StringComparison.Ordinal);
        Assert.Contains("  jitter gate ", output, StringComparison.Ordinal);
        Assert.Contains("  missed      ", output, StringComparison.Ordinal);
        Assert.Contains("the timer-only loop missed ", output, StringComparison.Ordinal);
        Assert.Contains(exit == CommandDispatcher.ExitOk ? "  verdict     PASS (exit code 0)" : "  verdict     FAIL (exit code 1)", output, StringComparison.Ordinal);
        Assert.Contains("requested SCHED_FIFO priority 10, else nice -10", output, StringComparison.Ordinal);

        var lines = File.ReadAllLines(csv);
        Assert.Equal("phase,block,index,measured,deadline_ns,wake_ns,lateness_ns,work_ns,target_ns,kind,reason,skipped", lines[0]);
        var timer = lines.Count(l => l.StartsWith("timer,", StringComparison.Ordinal));
        var full = lines.Count(l => l.StartsWith("full,", StringComparison.Ordinal));

        // 0.4 s at 240 Hz is 96 slots per loop; a stalled test host skips some (never judged here). Elsewhere the
        // portable waiter sleeps in timer ticks (15.6 ms on Windows by default), so only a few slots are certain.
        var floor = OperatingSystem.IsLinux() ? 10 : 1;
        Assert.InRange(timer, floor, 110);
        Assert.InRange(full, floor, 110);
        Assert.Equal(lines.Length - 1, timer + full);

        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(',');
            Assert.True(long.Parse(f[6], System.Globalization.CultureInfo.InvariantCulture) >= 0, $"woke before the deadline: {line}");
            Assert.True(long.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture) >= 0, line);
        }
    }

    private static JitterBenchResult Result(long p99Ns, long fullMissed, long timerMissed, int collections = 0)
    {
        var thread = new ThreadSchedulingReport { Requested = ThreadSchedulingRequest.Default, Policy = "SCHED_OTHER" };
        PhaseStats Stats(string name, long missed) => new()
        {
            Name = name,
            Slots = 7200,
            Skipped = missed,
            WakeLateness = new Distribution(7200 + missed, 1_000, 13_000, 17_000, p99Ns, p99Ns, p99Ns, 12_000),
        };

        return new JitterBenchResult
        {
            Options = new JitterBenchOptions(),
            Blocks = [],
            Full = new JitterSamples("full loop", 1),
            FullStats = Stats("full loop", fullMissed),
            BaselineStats = Stats("timer only", timerMissed),
            PacingThread = thread,
            SourceThread = thread,
            GcConfiguration = "workstation, concurrent, latency mode Interactive",
            MeasuredGc = new FrameMotionPlus.Platform.Runtime.GcSnapshot(collections, 0, 0, 0, TimeSpan.Zero),
        };
    }

    /// <summary>
    /// The design doc's runtime-jitter gate and the missed-slot budget are reported on their own lines, with the
    /// timer-only loop's misses beside the full loop's; the exit code is 1 when either fails.
    /// </summary>
    [Theory]
    [InlineData(20_000, 0, 0, 0, "  jitter gate PASS: p99 wake lateness 20.0 us <= 0.500 ms", "  missed      PASS: full loop missed 0 slot(s)", 0)]
    [InlineData(20_000, 2, 2, 0, "  jitter gate PASS: p99 wake lateness 20.0 us <= 0.500 ms", "  missed      FAIL: full loop missed 2 slot(s)", 1)]
    [InlineData(900_000, 0, 0, 0, "              - p99 wake lateness 900.0 us exceeds the 0.500 ms gate", "  missed      PASS: full loop missed 0 slot(s)", 1)]
    [InlineData(20_000, 0, 3, 1, "              - 1 GC collection(s) during the measured time", "  missed      PASS: full loop missed 0 slot(s)", 1)]
    public void The_verdict_reports_the_runtime_gate_and_the_missed_slot_budget_separately(
        long p99Ns, long fullMissed, long timerMissed, int collections, string gateLine, string missedLine, int exit)
    {
        var r = Result(p99Ns, fullMissed, timerMissed, collections);
        using var o = new StringWriter();
        BenchCommand.PrintVerdict(o, r);
        var text = o.ToString();

        Assert.Contains(gateLine, text, StringComparison.Ordinal);
        Assert.Contains(gateLine.StartsWith("  jitter gate PASS", StringComparison.Ordinal) ? "  jitter gate PASS" : "  jitter gate FAIL (design doc gate):", text, StringComparison.Ordinal);
        Assert.Contains(missedLine, text, StringComparison.Ordinal);
        Assert.Contains(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the timer-only loop missed {timerMissed} in the same run"), text, StringComparison.Ordinal);
        Assert.Contains(exit == 0 ? "  verdict     PASS (exit code 0)" : "  verdict     FAIL (exit code 1)", text, StringComparison.Ordinal);
        Assert.Equal(exit, BenchCommand.ExitCode(r));
        Assert.Equal(exit == 0, r.GatePassed);
    }

    [Fact]
    public void Linux_a_run_over_its_gate_exits_1_and_says_why()
    {
        TestPlatform.SkipUnlessLinux();

        // No kernel wakes a sleeping thread within 1 us of its deadline 99% of the time.
        var (exit, output, _) = Run("bench", "jitter", "--duration-s", "0.2", "--warmup-s", "0.05", "--wait", "sleep", "--gate-ms", "0.001", "--no-baseline");

        Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
        Assert.Contains("  jitter gate FAIL (design doc gate):", output, StringComparison.Ordinal);
        Assert.Contains("exceeds the", output, StringComparison.Ordinal);
        Assert.Contains("  missed      ", output, StringComparison.Ordinal);
        Assert.Contains("; timer-only loop not run", output, StringComparison.Ordinal);
        Assert.Contains("  verdict     FAIL (exit code 1)", output, StringComparison.Ordinal);
        Assert.Contains("not measured (--no-baseline)", output, StringComparison.Ordinal);
    }
}
