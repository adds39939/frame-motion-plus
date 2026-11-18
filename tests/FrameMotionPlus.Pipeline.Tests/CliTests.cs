using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.App.Play;
using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// <c>fmp play</c>: option checks (exit 2), real offscreen runs in-process (exit 0 with a PASS verdict), the gate verdict
/// rules on made-up reports, and out of process the missing-hardware paths (exit 69 with one line) and the fallback to
/// blend when only the VFX SDK is missing.
/// </summary>
public sealed class CliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fmp-play-cli-" + Guid.NewGuid().ToString("N"));

    public CliTests()
    {
        // In this process fmp must never end the process itself (it does after a teardown that abandoned GPU work).
        PlayCommand.ExitNow = null;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(new[] { "play" }, "play needs a file")]
    [InlineData(new[] { "play", "--offscreen" }, "play needs a file")]
    [InlineData(new[] { "play", "x.mp4", "--frames", "10", "--duration-s", "1" }, "not both")]
    [InlineData(new[] { "play", "x.mp4", "--generator", "rife" }, "--generator")]
    [InlineData(new[] { "play", "x.mp4", "--quality", "ultra" }, "ultra")]
    [InlineData(new[] { "play", "x.mp4", "--multiplier", "9" }, "--multiplier")]
    [InlineData(new[] { "play", "x.mp4", "--multiplier", "2", "--output-hz", "120" }, "not both")]
    [InlineData(new[] { "play", "x.mp4", "--realtime" }, "only apply with '--offscreen'")]
    [InlineData(new[] { "play", "x.mp4", "--rate", "60" }, "only apply with '--offscreen'")]
    [InlineData(new[] { "play", "x.mp4", "--offscreen", "--fullscreen" }, "do not apply with '--offscreen'")]
    [InlineData(new[] { "play", "x.mp4", "--offscreen", "--window", "640x360" }, "do not apply with '--offscreen'")]
    [InlineData(new[] { "play", "x.mp4", "--gate", "--offscreen" }, "--gate")]
    [InlineData(new[] { "play", "x.mp4", "--gate", "--generator", "blend" }, "--gate")]
    [InlineData(new[] { "play", "x.mp4", "--gate", "--multiplier", "4" }, "--gate")]
    [InlineData(new[] { "play", "x.mp4", "--window", "10x10" }, "--window")]
    [InlineData(new[] { "play", "x.mp4", "--cuda-sync", "fast" }, "--cuda-sync")]
    [InlineData(new[] { "play", "x.mp4", "--bogus", "1" }, "Unknown option")]
    public void Bad_options_are_usage_errors(string[] args, string message)
    {
        var (exit, _, err) = Run(args);
        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Contains(message, err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gate_option_runs_exactly_the_milestone_run()
    {
        var o = PlayOptions.Parse(["clip.mp4", "--gate"]);
        Assert.Equal(GeneratorChoice.Vfg, o.Generator);
        Assert.Equal(Core.Abstractions.FrameGenQuality.Medium, o.Quality);
        Assert.Equal(2, o.Multiplier);
        Assert.True(o.Loop);
        Assert.Equal(600, o.DurationS);
        Assert.False(o.Offscreen);
        Assert.Equal(Core.Time.FrameRate.FromHz(60), Core.Time.FrameRate.FromHz(60));
        Assert.Equal(4, PlayOptions.Parse(["clip.mp4", "--output-hz", "240"]).MultiplierFor(Core.Time.FrameRate.FromHz(60)));
        Assert.Equal(2, PlayOptions.Parse(["clip.mp4", "--output-hz", "60"]).MultiplierFor(Core.Time.FrameRate.FromHz(30)));
    }

    [Fact]
    public void Help_describes_play()
    {
        var (exit, output, _) = Run("--help");
        Assert.Equal(CommandDispatcher.ExitOk, exit);
        Assert.Contains("fmp play <file>", output, StringComparison.Ordinal);
        Assert.Contains("--gate", output, StringComparison.Ordinal);
        Assert.Contains("--offscreen", output, StringComparison.Ordinal);
        Assert.Contains("fmp verify <file.csv>", output, StringComparison.Ordinal);
        Assert.Contains("FMP_PLAY_FAULT", output, StringComparison.Ordinal);
    }

    /// <summary>Real offscreen runs (virtual and real clock, each generator but VFG): exit 0, a PASS verdict, the CSV.</summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData("blend", false)]
    [InlineData("repeat", false)]
    [InlineData("none", false)]
    [InlineData("blend", true)]
    public void A_short_offscreen_run_passes(string generator, bool realtime)
    {
        Hardware.RequireGpu();
        var csv = Path.Combine(_directory, "t.csv");
        string[] args = ["play", Clips.Get("c30"), "--offscreen", "--generator", generator, "--duration-s", "1.5", "--warmup-s", "0.3", "--loop", "--csv", csv];
        var (exit, output, err) = Run(realtime ? [.. args, "--realtime"] : args);
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
        Assert.Contains("missed      0 slot(s)", output, StringComparison.Ordinal);
        var lines = File.ReadAllLines(csv);
        Assert.StartsWith("slot,kind,reason", lines[0], StringComparison.Ordinal);
        Assert.True(lines.Length > 100, $"{lines.Length} CSV lines");
    }

    [Fact]
    [Trait(Category.Name, Category.Vfx)]
    public void A_short_offscreen_vfg_run_passes()
    {
        Hardware.RequireVfx();
        var (exit, output, err) = Run("play", Clips.Get("c60"), "--offscreen", "--generator", "vfg", "--duration-s", "1.5", "--warmup-s", "0.3", "--loop");
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("VFG Medium", output, StringComparison.Ordinal);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_missing_file_fails_with_one_line()
    {
        Hardware.RequireGpu();
        var (exit, _, err) = Run("play", Path.Combine(_directory, "nothing.mp4"), "--offscreen");
        Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("no such file", err, StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdict: clean, PASS; compositor skips only, FAIL that says the pipeline missed nothing (environment);
    /// a miss of the pipeline's, FAIL; a gen0 collection, FAIL (gate) and a warning otherwise.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 0, "", true, "gate        PASS")]
    [InlineData(3, 0, 2, 0, "", true, "(the display's: 3; during a rate change: 2)")]
    [InlineData(3, 2, 0, 0, "", false, "2 missed slot(s) caused by the pipeline")]
    [InlineData(0, 0, 0, 1, "", false, "gen0 collection")]
    [InlineData(0, 0, 0, 0, "unjudged", false, "without a first-pixel time that nothing can judge")]
    [InlineData(0, 0, 0, 0, "disagrees", false, "disagrees with the telemetry verifier")]
    [InlineData(0, 0, 0, 0, "none", false, "the telemetry verifier did not run")]
    public void The_gate_verdict_counts_every_miss_and_says_whose(int display, int pipeline, int rateChange, int gen0, string verifier, bool pass, string text)
    {
        var report = new PlaybackReport
        {
            Options = new PlaybackOptions { Path = "x.mp4" },
            Setup = new PlaybackSetup
            {
                Stream = new Sources.File.FileStreamInfo { Path = "x.mp4", Container = "mp4", Codec = "h264", Profile = "", Width = 2560, Height = 1440, NominalRate = Core.Time.FrameRate.FromHz(60) },
                GeneratorName = "VFG Medium",
                Display = "window",
                DeviceName = "test",
                VulkanDriver = "test",
                MaxLead = 5,
            },
            StopReason = StopReason.Duration,
            Measured = true,
            DisplayRate = Core.Time.FrameRate.FromHz(120),
            DisplayRateKnown = true,
            MeasuredGc = new Platform.Runtime.GcSnapshot(gen0, 0, 0, 0, TimeSpan.Zero),
            Diagnostics = new Core.Scheduling.SchedulerDiagnostics { Stride = 1, Multiplier = 2, SourceIntervalNs = 16_666_667 },
        };
        report.Statistics.Slots = 72_000;
        if (verifier != "none")
        {
            var v = new VerificationResult { Judged = 72_000, Unjudged = verifier == "unjudged" ? 1 : 0, Disagreements = verifier == "disagrees" ? 1 : 0 };
            if (verifier == "disagrees")
            {
                v.Slots.Add((4711, VerifierFinding.Early | VerifierFinding.Disagrees));
            }

            report.Verification = v;
        }
        else
        {
            report.VerificationSkipped = "not windowed";
        }
        report.Statistics.FirstMeasuredNs = 1;
        report.Statistics.LastMeasuredNs = 600_000_000_001;
        for (var i = 0; i < display; i++)
        {
            report.Statistics.AddMiss(i * 1_000_000_000L, i % 2 == 0 ? MissKind.DisplayLate : MissKind.CatchUp, 1);
        }

        for (var i = 0; i < pipeline; i++)
        {
            report.Statistics.AddMiss(i * 1_000_000_000L, i % 2 == 0 ? MissKind.LatePresent : MissKind.Dropped, 1);
        }

        for (var i = 0; i < rateChange; i++)
        {
            report.Statistics.AddMiss(i * 1_000_000_000L, MissKind.RateChange, 1);
        }

        using var output = new StringWriter();
        var verdict = PlayPrinter.PrintSummary(output, PlayOptions.Parse(["x.mp4", "--gate"]), report);
        Log.WriteLine(output.ToString());
        Assert.Equal(pass, verdict);
        Assert.Contains(text, output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A run that ends inside its warm-up has measured nothing: it fails, with or without the gate (it used to print "0
    /// measured" and PASS).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_run_that_measured_nothing_does_not_pass(bool gate)
    {
        var report = new PlaybackReport
        {
            Options = new PlaybackOptions { Path = "x.mp4" },
            Setup = new PlaybackSetup
            {
                Stream = new Sources.File.FileStreamInfo { Path = "x.mp4", Container = "mp4", Codec = "h264", Profile = "", Width = 2560, Height = 1440, NominalRate = Core.Time.FrameRate.FromHz(60) },
                GeneratorName = "blend",
                Display = "offscreen",
                DeviceName = "test",
                VulkanDriver = "test",
                MaxLead = 5,
            },
            StopReason = StopReason.Duration,
            Measured = false,
            DisplayRate = Core.Time.FrameRate.FromHz(120),
            DisplayRateKnown = true,
            Diagnostics = new Core.Scheduling.SchedulerDiagnostics { Stride = 1, Multiplier = 2, SourceIntervalNs = 16_666_667 },
        };
        using var output = new StringWriter();
        string[] args = gate ? ["x.mp4", "--gate"] : ["x.mp4", "--offscreen"];
        var verdict = PlayPrinter.PrintSummary(output, PlayOptions.Parse(args), report);
        Log.WriteLine(output.ToString());
        Assert.False(verdict);
        Assert.Contains("nothing was measured", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a GPU, without FFmpeg 9 or without a display, <c>fmp play</c> exits 69 with one line; asked for VFG without
    /// the SDK, too; with the generator left to choose and no SDK, it says so and plays with blend.
    /// </summary>
    [Theory]
    [InlineData("CUDA_VISIBLE_DEVICES", "", false, "needs an NVIDIA GPU")]
    [InlineData("FMP_FFMPEG_DIR", "empty", false, "needs the FFmpeg 9 shared libraries")]
    [InlineData("WAYLAND_DISPLAY", "", true, "needs a Wayland session")]
    [InlineData("FMP_VFX_SDK", "empty", true, "needs the NVIDIA VFX SDK")]
    public async Task Missing_hardware_exits_69_with_one_line(string variable, string value, bool needsGpu, string message)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("the hidden-library variables are Linux ones");
        }

        var clip = Path.Combine(_directory, "any.mp4");
        if (needsGpu)
        {
            Hardware.RequireGpu();
            clip = Clips.Get("c30");
        }
        else
        {
            if (variable == "FMP_FFMPEG_DIR")
            {
                Hardware.RequireCuda(); // with no GPU at all the GPU message comes first
            }

            File.WriteAllBytes(clip, [0]);
        }

        var environment = new Dictionary<string, string> { [variable] = value == "empty" ? _directory : value };
        // Bounded, should the missing piece ever not stop it before a window opens.
        string[] args = variable == "FMP_VFX_SDK" ? ["play", clip, "--offscreen", "--generator", "vfg", "--duration-s", "2"] : ["play", clip, "--duration-s", "2"];
        var (exit, _, err) = await RunOutOfProcess(environment, args);
        Assert.Equal(PatternCommand.ExitUnavailable, exit);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("fmp: play ", err, StringComparison.Ordinal);
        Assert.Contains(message, err, StringComparison.Ordinal);

        if (!needsGpu)
        {
            // The rest of the application is unaffected.
            var (simulate, output, simulateErr) = await RunOutOfProcess(environment, "simulate", "--duration-s", "5", "--check");
            Assert.True(simulate == CommandDispatcher.ExitOk, output + simulateErr);
        }
    }

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public async Task Without_the_sdk_the_default_generator_falls_back_to_blend()
    {
        Hardware.RequireGpu();
        var environment = new Dictionary<string, string> { ["FMP_VFX_SDK"] = _directory };
        var (exit, output, err) = await RunOutOfProcess(environment, "play", Clips.Get("c30"), "--offscreen", "--duration-s", "1", "--warmup-s", "0.2");
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("generator   blend: VFG is the default but the NVIDIA VFX SDK is not available", output, StringComparison.Ordinal);
        Assert.Contains("falling back to blend", output, StringComparison.Ordinal);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// GPU work that never finishes (FMP_PLAY_FAULT=gpu-wait: the 40th generated frame waits for a release point nothing
    /// will reach): <c>fmp play</c> still stops (the stall check), prints its summary with what teardown abandoned, and the
    /// process exits within bounds with exit 1. It used to hang for good in teardown (cuModuleUnload, NVDEC) and print
    /// nothing. With VFG the generator thread itself is stuck inside the SDK's call; that is abandoned too.
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData("blend")]
    [InlineData("vfg")]
    public async Task A_gpu_wait_that_never_completes_still_ends_fmp_play_with_its_summary(string generator)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("the fault is injected through the Linux pipeline");
        }

        Hardware.RequireGpu();
        if (generator == "vfg")
        {
            Hardware.RequireVfx();
        }

        var environment = new Dictionary<string, string> { ["FMP_PLAY_FAULT"] = "gpu-wait@40" };
        var watch = Stopwatch.StartNew();
        var (exit, output, err) = await RunOutOfProcess(environment, null, 90,
            "play", Clips.Get("c60-long"), "--offscreen", "--realtime", "--duration-s", "60", "--warmup-s", "0.5", "--generator", generator);
        Log.WriteLine($"ended after {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
        Assert.Contains("teardown    ABANDONED", output, StringComparison.Ordinal);
        Assert.Contains("No ring write finished", output, StringComparison.Ordinal);
        Assert.Contains("verdict     FAIL", output, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>
    /// SIGINT during the start-up (as soon as the CUDA driver is loaded, while the device is being opened): the stop is kept
    /// and honoured as soon as there is a session; the run ends at once with its summary ("stopped: by the user"). It used to
    /// be lost or fatal: the handlers were registered only after the session had opened, so the signal either killed the
    /// process without a summary or (windowed) became SDL's quit event, which the pre-roll threw away, and the run played to
    /// its end.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public async Task A_signal_during_start_up_stops_fmp_play_with_its_summary()
    {
        if (TestSupport.ChildProcess.SigintUnavailable is { } why)
        {
            Assert.Skip(why);
        }

        Hardware.RequireGpu();
        var watch = Stopwatch.StartNew();
        var (exit, output, err) = await RunOutOfProcess(new Dictionary<string, string>(), p =>
        {
            // The JIT host takes a while to reach fmp's code; the CUDA driver is loaded just after the handlers are set up,
            // and the device is opened right after that: the signal lands in the start-up.
            var maps = $"/proc/{p.Id}/maps";
            var giveUp = Stopwatch.StartNew();
            while (giveUp.Elapsed < TimeSpan.FromSeconds(20) && !(File.Exists(maps) && File.ReadAllText(maps).Contains("libcuda.so", StringComparison.Ordinal)))
            {
                Thread.Sleep(5);
            }

            using var kill = Process.Start("kill", ["-INT", p.Id.ToString(CultureInfo.InvariantCulture)]);
            kill.WaitForExit();
        }, 60, "play", Clips.Get("c60-long"), "--offscreen", "--realtime", "--duration-s", "30", "--generator", "blend");
        Log.WriteLine($"ended after {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed.TotalSeconds:0.0} s: the signal was lost");
        Assert.Contains("stopping on SIGINT", err, StringComparison.Ordinal);
        Assert.Contains("stopped: by the user", output, StringComparison.Ordinal);
        Assert.Contains("STOPPED by the user before anything was measured", output, StringComparison.Ordinal);
        Assert.Equal(CommandDispatcher.ExitOk, exit); // a stop the user asked for is not a failure
    }

    /// <summary>
    /// <c>fmp bench present</c> (windowed, short) ends on SIGINT after the current probe, printing what it measured and a
    /// "stopped" line. It used to ignore signals, Escape and closing the window and run its full duration, so a fullscreen
    /// probe covered its output for as long as asked.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Display)]
    public async Task A_signal_stops_the_present_probe_with_what_it_measured()
    {
        if (TestSupport.ChildProcess.SigintUnavailable is { } why)
        {
            Assert.Skip(why);
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Assert.Skip("needs a Wayland session");
        }

        var watch = Stopwatch.StartNew();
        var (exit, output, err) = await RunOutOfProcess(new Dictionary<string, string>(), p =>
        {
            Thread.Sleep(4000); // past the start-up (the handlers are set before the window opens) and into the probes
            using var kill = Process.Start("kill", ["-INT", p.Id.ToString(CultureInfo.InvariantCulture)]);
            kill.WaitForExit();
        }, 60, "bench", "present", "--test", "cadence", "--duration-s", "30", "--window", "320x180");
        Log.WriteLine($"ended after {watch.Elapsed.TotalSeconds:0.0} s");
        if (output.Contains("needs a Wayland session", StringComparison.Ordinal) || output.Contains("no first-pixel times", StringComparison.Ordinal))
        {
            Assert.Skip(output.Trim());
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed.TotalSeconds:0.0} s: the signal was ignored");
        Assert.Contains("stopped     by SIGINT", output, StringComparison.Ordinal);
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("600")]
    public void The_present_probe_runs_at_most_two_minutes(string seconds)
    {
        var (exit, _, err) = Run("bench", "present", "--duration-s", seconds);
        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Contains("at most 120 s", err, StringComparison.Ordinal);
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var timeout = TimeSpan.FromSeconds(120);
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
        Log.WriteLine(result.Out + result.Err);
        return result;
    }

    private static Task<(int Exit, string Out, string Err)> RunOutOfProcess(IReadOnlyDictionary<string, string> environment, params string[] args) =>
        RunOutOfProcess(environment, null, 120, args);

    private static async Task<(int Exit, string Out, string Err)> RunOutOfProcess(IReadOnlyDictionary<string, string> environment, Action<Process>? started, double timeoutS, params string[] args)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = TestSupport.ChildProcess.WithDefaultSigint(dotnet, [typeof(CommandDispatcher).Assembly.Location, .. args]);

        foreach (var (k, v) in environment)
        {
            info.Environment[k] = v;
        }

        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutS));
        started?.Invoke(process);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"'fmp {string.Join(' ', args)}' did not end within {timeoutS:0} s");
            throw;
        }

        var (o, e) = (await stdout, await stderr);
        Log.WriteLine($"exit {process.ExitCode}\n{o}{e}");
        return (process.ExitCode, o, e);
    }
}
