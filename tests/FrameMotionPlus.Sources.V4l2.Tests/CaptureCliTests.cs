using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Pipeline;
using FrameMotionPlus.Sources.V4l2.Interop;
using FrameMotionPlus.Sources.V4l2.Tests.Support;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// <c>fmp capture</c> end to end: the device list, the missing-hardware exits (69, one line) in and out of process, a mode
/// the device does not offer (2), real runs through the whole pipeline on vivid and on the card (offscreen and windowed),
/// a stop by SIGINT, and the device released on every way out, also when teardown abandons GPU work that never finishes.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class CaptureCliTests
{
    public CaptureCliTests()
    {
        // In this process fmp must never end the process itself (it does after a teardown that abandoned GPU work).
        PlayCommand.ExitNow = null;
    }

    [Fact]
    public void A_missing_device_exits_69_with_one_line()
    {
        CaptureHardware.RequireLinux();
        var (exit, _, err) = Run("capture", "/dev/video-none", "--offscreen");
        if (err.Contains("needs an NVIDIA GPU", StringComparison.Ordinal))
        {
            Assert.Equal(PatternCommand.ExitUnavailable, exit); // no GPU: that message comes first
            return;
        }

        Assert.Equal(PatternCommand.ExitUnavailable, exit);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("fmp: capture: no such capture device /dev/video-none", err, StringComparison.Ordinal);

        (exit, _, err) = Run("capture", "/dev/video-none", "--list");
        Assert.Equal(PatternCommand.ExitUnavailable, exit);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    [Trait(Category.Name, Category.Capture)]
    public void The_list_shows_vivid_and_its_modes()
    {
        var path = CaptureHardware.RequireVivid();
        var (exit, output, _) = Run("capture", "--list");
        Assert.Equal(CommandDispatcher.ExitOk, exit);
        Assert.Contains($"{path}: vivid (vivid", output, StringComparison.Ordinal);
        Assert.Contains("NV12", output, StringComparison.Ordinal);
        Assert.Contains("640x360:", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(Category.Name, Category.Capture)]
    public void A_mode_the_device_does_not_offer_is_a_usage_error()
    {
        var path = CaptureHardware.RequireVivid();
        CaptureHardware.RequireCuda();
        var (exit, _, err) = Run("capture", path, "--size", "999x999", "--offscreen", "--generator", "blend");
        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Contains("does not offer 999x999", err, StringComparison.Ordinal);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Without a GPU (hidden) or without a display, <c>fmp capture</c> exits 69 with one line; the rest of fmp works.</summary>
    [Theory]
    [Trait(Category.Name, Category.Capture)]
    [InlineData("CUDA_VISIBLE_DEVICES", "", "capture needs an NVIDIA GPU")]
    [InlineData("WAYLAND_DISPLAY", "", "needs a Wayland session")]
    public async Task Missing_hardware_exits_69_with_one_line(string variable, string value, string message)
    {
        var path = CaptureHardware.RequireVivid();
        if (variable == "WAYLAND_DISPLAY")
        {
            CaptureHardware.RequireCuda();
        }

        var environment = new Dictionary<string, string> { [variable] = value };
        // Windowed and bounded, should the missing piece ever not stop it before a window opens (capture is fullscreen and
        // unlimited by default).
        var (exit, _, err) = await RunOutOfProcess(environment, null, 120, "capture", path, "--size", "320x180", "--generator", "blend", "--windowed", "--duration-s", "2");
        Assert.Equal(PatternCommand.ExitUnavailable, exit);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("fmp: capture", err, StringComparison.Ordinal);
        Assert.Contains(message, err, StringComparison.Ordinal);

        var (simulate, output, simulateErr) = await RunOutOfProcess(environment, null, 120, "simulate", "--duration-s", "5", "--check");
        Assert.True(simulate == CommandDispatcher.ExitOk, output + simulateErr);
    }

    /// <summary>The whole pipeline on vivid, offscreen on the real clock: a clean run with the capture's lines.</summary>
    [Fact]
    [Trait(Category.Name, Category.Capture)]
    public void An_offscreen_capture_of_vivid_runs_clean()
    {
        var path = CaptureHardware.RequireVivid();
        CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.HorizontalMovement, VividControls.FastRight);
        var (exit, output, err) = Run("capture", path, "--size", "320x180", "--fps", "60", "--format", "nv12", "--offscreen", "--duration-s", "3", "--warmup-s", "1",
            "--generator", "blend");
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("negotiated  320x180 NV12 @ 60 fps", output, StringComparison.Ordinal);
        Assert.Contains("USERPTR into CUDA pinned memory", output, StringComparison.Ordinal);
        Assert.Contains("  capture     ", output, StringComparison.Ordinal);
        // vivid's 60 fps clock steps 15 to 18 ms (seen: 59.81 to 60.00 fps over a 32-interval window): about 60, every frame new.
        Assert.Matches(@"detected game rate (5[89]\.\d\d|60\.\d\d|61\.\d\d) fps", output);
        Assert.Contains(" captured → ", output, StringComparison.Ordinal);
        Assert.Contains(" 0 repeats dropped", output, StringComparison.Ordinal);
        Assert.Contains("capture thread 0 B", output, StringComparison.Ordinal);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    /// <summary>A capture from which no frame ever arrives (vivid dropping every buffer): a run with a duration ends at it,
    /// failing with the reason, instead of running for ever.</summary>
    [Fact]
    [Trait(Category.Name, Category.Capture)]
    public void A_capture_with_no_frames_ends_at_its_duration_saying_why()
    {
        var path = CaptureHardware.RequireVivid();
        CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.PercentageDroppedBuffers, 100);
        var watch = Stopwatch.StartNew();
        var (exit, output, err) = Run("capture", path, "--size", "320x180", "--fps", "60", "--offscreen", "--duration-s", "2", "--generator", "blend");
        Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
        Assert.Contains("No frames arrived from the source in the run's 2 s", output + err, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>A short windowed run on vivid (a few seconds in a small window on the desktop).</summary>
    [Fact]
    [Trait(Category.Name, Category.Capture)]
    public void A_windowed_capture_of_vivid_runs_clean()
    {
        var path = CaptureHardware.RequireVivid();
        CaptureHardware.RequireCuda();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Assert.Skip("no Wayland session");
        }

        using var controls = new VividControls(path).Set(VividControls.HorizontalMovement, VividControls.FastRight);
        var (exit, output, err) = Run("capture", path, "--size", "640x360", "--fps", "30", "--format", "nv12", "--window", "480x270", "--duration-s", "3",
            "--warmup-s", "1", "--generator", "blend");
        if (err.Contains("needs a", StringComparison.Ordinal) && exit == PatternCommand.ExitUnavailable)
        {
            Assert.Skip(err.Trim());
        }

        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    /// <summary>SIGINT while capturing: the run stops with its summary and the device is free at once for another program.</summary>
    [Fact]
    [Trait(Category.Name, Category.Capture)]
    public async Task A_signal_stops_the_capture_and_releases_the_device()
    {
        if (TestSupport.ChildProcess.SigintUnavailable is { } why)
        {
            Assert.Skip(why);
        }

        var path = CaptureHardware.RequireVivid();
        CaptureHardware.RequireCuda();
        var watch = Stopwatch.StartNew();
        var (exit, output, err) = await RunOutOfProcess(new Dictionary<string, string>(), p =>
        {
            var giveUp = Stopwatch.StartNew();
            while (giveUp.Elapsed < TimeSpan.FromSeconds(30) && !Streaming(p.Id))
            {
                Thread.Sleep(20);
            }

            Thread.Sleep(2500);
            using var kill = Process.Start("kill", ["-INT", p.Id.ToString(CultureInfo.InvariantCulture)]);
            kill.WaitForExit();
        }, 60, "capture", path, "--size", "320x180", "--fps", "60", "--offscreen", "--warmup-s", "0.5", "--generator", "blend");
        Assert.Contains("stopping on SIGINT", err, StringComparison.Ordinal);
        Assert.Contains("stopped: by the user", output, StringComparison.Ordinal);
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(45), $"took {watch.Elapsed.TotalSeconds:0.0} s");
        AssertReleased(path);
    }

    /// <summary>
    /// GPU work that does not finish (12 s of it on the ring writes' stream): teardown abandons the GPU side, and the capture
    /// device is still released at once (the abandon path closes it without any GPU wait).
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Capture)]
    public void An_abandoned_teardown_still_releases_the_device()
    {
        var path = CaptureHardware.RequireVivid();
        var cuda = CaptureHardware.RequireCuda();
        using var controls = new VividControls(path).Set(VividControls.HorizontalMovement, VividControls.FastRight);
        Cuda.CudaStream? stream = null;
        var fault = PlaybackFaults.Parse("gpu-busy@60:12")!;
        var hooks = new PlaybackHooks
        {
            WrapGenerator = (h, s, k) =>
            {
                stream = s;
                return fault(h, s, k);
            },
        };
        var options = new PlaybackOptions
        {
            Path = path,
            Capture = new CaptureSourceOptions { Device = path, Request = new CaptureRequest { Width = 320, Height = 180, Fps = 60, Format = CapturePixelFormat.Nv12 } },
            Generator = GeneratorKind.Blend,
            Display = DisplayKind.Offscreen,
            Clock = PlaybackClock.Real,
            OffscreenRate = FrameRate.FromHz(120),
            DurationNs = 30 * Nanoseconds.PerSecond,
            WarmupNs = 200 * Nanoseconds.PerMillisecond,
            StallTimeoutNs = Nanoseconds.PerSecond,
        };
        PlaybackReport report;
        using (var session = new PlaybackSession(options, cuda, hooks))
        {
            try
            {
                session.Open();
            }
            catch (PlaybackUnavailableException e)
            {
                Assert.Skip(e.Message);
            }

            report = session.Run();
        }

        Log.WriteLine($"{report.StopReason}: {report.Failure?.Message}; abandoned: {report.Abandoned}");
        Assert.True(report.GpuAbandoned);
        Assert.Contains("the device itself was released", report.Abandoned, StringComparison.Ordinal);
        Assert.NotNull(report.Capture);
        AssertReleased(path);

        // The GPU work ends by itself; leave the GPU idle for the next test.
        Assert.NotNull(stream);
        var giveUp = Stopwatch.StartNew();
        while (!stream!.IsIdle)
        {
            Assert.True(giveUp.Elapsed < TimeSpan.FromSeconds(40), "the abandoned stream never drained");
            Thread.Sleep(100);
        }
    }

    /// <summary>The card, offscreen: 4K HDR 30 to 60 with the default generator (VFG when the SDK is there), a few seconds.</summary>
    [Fact]
    [Trait(Category.Name, Category.Elgato)]
    public void An_offscreen_4k30_hdr_capture_of_the_console_runs_clean()
    {
        var path = CaptureHardware.RequireElgatoSignal();
        CaptureHardware.RequireCuda();
        var (exit, output, err) = Run("capture", path, "--mode", "4k30-hdr", "--offscreen", "--duration-s", "4", "--warmup-s", "1");
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("negotiated  3840x2160 P010 @ 30 fps", output, StringComparison.Ordinal);
        Assert.Contains("frames Rgb10A2 Pq/Bt2020", output, StringComparison.Ordinal);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    /// <summary>The card in a small window: 1440p HDR 60 to 120, three seconds.</summary>
    [Fact]
    [Trait(Category.Name, Category.Elgato)]
    public void A_windowed_1440p60_hdr_capture_of_the_console_runs_clean()
    {
        var path = CaptureHardware.RequireElgatoSignal();
        CaptureHardware.RequireCuda();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Assert.Skip("no Wayland session");
        }

        var (exit, output, err) = Run("capture", path, "--mode", "1440p60-hdr", "--window", "640x360", "--duration-s", "3", "--warmup-s", "1");
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("negotiated  2560x1440 P010 @ 60 fps", output, StringComparison.Ordinal);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    /// <summary>Another opener can take the device and stream from it at once.</summary>
    private static void AssertReleased(string path)
    {
        using var other = V4l2Device.Open(path);
        Assert.True(other.RequestBuffers(2, V4l2Defs.MemoryMmap, out _) == 2, $"{path} was not released");
        _ = other.RequestBuffers(0, V4l2Defs.MemoryMmap, out _);
    }

    /// <summary>Whether process <paramref name="pid"/> has the capture thread running (its name in the task list).</summary>
    private static bool Streaming(int pid)
    {
        try
        {
            return Directory.GetDirectories($"/proc/{pid}/task").Any(t => System.IO.File.Exists(Path.Combine(t, "comm")) && System.IO.File.ReadAllText(Path.Combine(t, "comm")).StartsWith("fmp-capture", StringComparison.Ordinal));
        }
        catch (IOException)
        {
            return false;
        }
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

    private static async Task<(int Exit, string Out, string Err)> RunOutOfProcess(IReadOnlyDictionary<string, string> environment, Action<Process>? started, double timeoutS, params string[] args)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = TestSupport.ChildProcess.WithDefaultSigint(dotnet, [typeof(CommandDispatcher).Assembly.Location, .. args]);

        // As this test host (see its csproj): the JIT host's tier-up re-binds a P/Invoke stub and allocates its library
        // name on the calling thread, which the zero-allocation verdict would see; NativeAOT has no tiers.
        info.Environment["DOTNET_TieredPGO"] = "0";
        foreach (var (k, v) in environment)
        {
            info.Environment[k] = v;
        }

        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutS));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        started?.Invoke(process);
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
