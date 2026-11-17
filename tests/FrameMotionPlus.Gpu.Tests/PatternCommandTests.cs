using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Gpu.Tests.Support;
using FrameMotionPlus.Vulkan;
using Reference = FrameMotionPlus.Cuda.Tests.Support.Reference;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>
/// <c>fmp pattern</c>: options and the verifier everywhere; real runs in-process through the dispatcher when the GPU
/// (offscreen) or a Wayland session (windowed) is there; the no-GPU path out of process, with the GPU hidden.
/// </summary>
public sealed partial class PatternCommandTests
{
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
        Interop.WriteLine(result.Out + result.Err);
        return result;
    }

    [Fact]
    public void Defaults_are_1440p_8_bit_windowed_for_five_seconds()
    {
        var o = PatternOptions.Parse([]);
        Assert.Equal((2560, 1440), (o.Width, o.Height));
        Assert.Equal(PixelFormat.Rgba8, o.Format);
        Assert.Equal(5, o.DurationSeconds);
        Assert.Null(o.Frames);
        Assert.False(o.Offscreen);
        Assert.False(o.Blend);
        Assert.False(o.Validate);
        Assert.True(o.Verify);
        Assert.Equal((960, 540), (o.WindowWidth, o.WindowHeight));
        Assert.Null(o.Rate);
        Assert.Equal(4, o.Slots);
        Assert.Equal(30, o.WarmupFrames);
    }

    [Fact]
    public void Every_option_is_parsed()
    {
        var o = PatternOptions.Parse([
            "--size", "3840x2160", "--format", "10", "--duration-s", "2.5", "--frames", "300", "--offscreen", "--blend",
            "--validate", "--no-verify", "--rate", "59.94", "--slots", "6", "--warmup-frames", "10",
        ]);
        Assert.Equal((3840, 2160), (o.Width, o.Height));
        Assert.Equal(PixelFormat.Rgb10A2, o.Format);
        Assert.Equal(2.5, o.DurationSeconds);
        Assert.Equal(300, o.Frames);
        Assert.True(o.Offscreen && o.Blend && o.Validate);
        Assert.False(o.Verify);
        Assert.Equal(new FrameRate(60000, 1001), o.Rate);
        Assert.Equal(6, o.Slots);
        Assert.Equal(10, o.WarmupFrames);

        var w = PatternOptions.Parse(["--window", "640x360", "--format", "rgba8"]);
        Assert.Equal((640, 360), (w.WindowWidth, w.WindowHeight));
        Assert.Equal(PixelFormat.Rgba8, w.Format);
    }

    [Theory]
    [InlineData("pattern", "--size", "1920")]
    [InlineData("pattern", "--size", "0x10")]
    [InlineData("pattern", "--size", "16x16")]
    [InlineData("pattern", "--size", "99999x10")]
    [InlineData("pattern", "--format", "12")]
    [InlineData("pattern", "--frames", "0")]
    [InlineData("pattern", "--slots", "3")]
    [InlineData("pattern", "--no-verify")]
    [InlineData("pattern", "--unpaced")]
    [InlineData("pattern", "--offscreen", "--window", "640x360")]
    [InlineData("pattern", "--bogus")]
    public void Bad_options_are_usage_errors(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(CommandDispatcher.ExitUsage, CommandDispatcher.Run(args, output, error));
        Assert.StartsWith("fmp: ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Help_lists_the_command_and_its_exit_codes()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CommandDispatcher.Run(["--help"], output, error));
        var help = output.ToString();
        Assert.Contains("fmp pattern [options]", help, StringComparison.Ordinal);
        Assert.Contains("--offscreen", help, StringComparison.Ordinal);
        Assert.Contains("69 when a GPU or display is required but not there", help, StringComparison.Ordinal);
        Assert.Contains("FMP_PATTERN_FAIL_AT=<slot>", help, StringComparison.Ordinal); // the teardown fault-injection switch
    }

    /// <summary>
    /// The verdict fails on what the bridge reports (findings 7 and 8) and on a rejected request, and an unpaced run
    /// may hold back and drop where a paced offscreen run may not.
    /// </summary>
    [Fact]
    public void The_verdict_fails_on_unreaped_imports_out_of_order_signals_and_rejections()
    {
        static PatternResult Clean(params string[] args) => new()
        {
            Options = PatternOptions.Parse(args),
            Presented = 100,
            Verified = 100,
            AllocatedBytes = 0,
        };

        Assert.Empty(Clean("--offscreen").Failures());
        var unreaped = Clean("--offscreen");
        unreaped.AbandonedImports = 1;
        unreaped.UnreapedImports = 1;
        Assert.Contains(unreaped.Failures(), f => f.Contains("not freed within 3 s of the presenter letting go", StringComparison.Ordinal));
        var reaped = Clean("--offscreen");
        reaped.AbandonedImports = 1; // handed to the reaper and freed in time: a slow drain, not a leak
        Assert.Empty(reaped.Failures());
        var outOfOrder = Clean("--offscreen");
        outOfOrder.OutOfOrderSignals = 2;
        Assert.Contains(outOfOrder.Failures(), f => f.Contains("signalled out of order", StringComparison.Ordinal));
        var rejected = Clean("--offscreen");
        rejected.PresenterStats = new PresenterStats { RejectedRequests = 1 };
        Assert.Contains(rejected.Failures(), f => f.Contains("rejected by the presenter", StringComparison.Ordinal));
        var held = Clean("--offscreen");
        held.HeldBack = 5;
        held.Dropped = 3;
        Assert.Single(held.Failures());
        var unpaced = Clean("--offscreen", "--unpaced");
        unpaced.HeldBack = 5;
        unpaced.Dropped = 3;
        Assert.Empty(unpaced.Failures());
    }

    public static TheoryData<int, int, PixelFormat> VerifierShapes => new()
    {
        { 32, 12, PixelFormat.Rgba8 },
        { 333, 97, PixelFormat.Rgb10A2 },
        { 1277, 719, PixelFormat.Rgba8 },
        { 640, 360, PixelFormat.Rgb10A2 },
    };

    [Theory]
    [MemberData(nameof(VerifierShapes))]
    public void Verifier_agrees_with_the_independent_reference_and_catches_one_wrong_pixel(int width, int height, PixelFormat format)
    {
        var verifier = new PatternVerifier(width, height, format);
        foreach (var frame in new uint[] { 0, 1, 7, 241, 1000, 0x8000_0001, uint.MaxValue - 1 })
        {
            var pixels = Reference.Pattern(width, height, frame, format);
            Assert.True(verifier.IsFrame(Bytes(pixels), frame), $"frame {frame}");
            Assert.False(verifier.IsFrame(Bytes(pixels), frame + 1), $"frame {frame} accepted as {frame + 1}");
            Assert.Equal(frame, verifier.DecodeCounter(Bytes(pixels)));

            var blend = Reference.Blend(pixels, Reference.Pattern(width, height, frame + 1, format), Reference.BlendWeight(0.5f), format);
            Assert.True(verifier.IsBlend(Bytes(blend), frame), $"blend {frame}/{frame + 1}");
            Assert.False(verifier.IsFrame(Bytes(blend), frame) && frame % 2 == 1, "a blend passed as a real frame");

            foreach (var (x, y) in new[] { (0, 0), (width - 1, height - 1), (width / 2, height * 8 / 12), (width / 3, height * 11 / 12) })
            {
                var wrong = (uint[])pixels.Clone();
                wrong[(y * width) + x] ^= 1;
                Assert.False(verifier.IsFrame(Bytes(wrong), frame), $"a flipped bit at ({x}, {y}) went unnoticed");
            }
        }
    }

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData("8", false)]
    [InlineData("10", false)]
    [InlineData("8", true)]
    [InlineData("10", true)]
    public void Offscreen_run_verifies_every_frame_under_validation(string format, bool blend)
    {
        Hardware.RequireGpu();
        string[] args = ["pattern", "--offscreen", "--validate", "--size", "1277x719", "--format", format, "--frames", "150", "--rate", "240"];
        var (exit, output, error) = Run(blend ? [.. args, "--blend"] : args);
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        Assert.Contains("150 presented", output, StringComparison.Ordinal);
        Assert.Contains("0 dropped, 0 skipped", output, StringComparison.Ordinal);
        Assert.Contains("150 of 150 presented frames bit-exact", output, StringComparison.Ordinal);
        Assert.Contains("0 B allocated on the present thread in 120 measured slots", output, StringComparison.Ordinal);
        Assert.Contains("synchronization checks: 0 message(s)", output, StringComparison.Ordinal);
        Assert.Contains(blend ? "blend t=0.5 into slot" : "pattern into the slot", output, StringComparison.Ordinal);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(Category.Name, Category.Display)]
    public void Windowed_run_presents_at_the_display_rate_under_validation()
    {
        Hardware.RequireDisplay();
        var (exit, output, error) = Run("pattern", "--validate", "--size", "1280x720", "--window", "480x270", "--duration-s", "1.5", "--format", "10");
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        Assert.Contains("swapchain", output, StringComparison.Ordinal);
        Assert.Contains("synchronization checks: 0 message(s)", output, StringComparison.Ordinal);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CUDA_VISIBLE_DEVICES", "", true, "needs an NVIDIA GPU")]
    [InlineData("VK_DRIVER_FILES", "/nonexistent/fmp-no-icd.json", true, "Vulkan")]
    [InlineData("WAYLAND_DISPLAY", "", false, "needs a Wayland session")]
    public async Task Without_the_hardware_it_fails_cleanly_with_one_line(string variable, string value, bool offscreen, string message)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("the hidden-hardware variables are Linux ones (elsewhere the message is 'needs Linux')");
        }

        // Hiding CUDA works everywhere (no driver at all gives the same message). The later checks are only reached
        // where CUDA is present.
        if (variable != "CUDA_VISIBLE_DEVICES")
        {
            Hardware.RequireGpu();
        }

        // Out of process: the driver and the loader read these variables once, at start-up.
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string[] args = [typeof(CommandDispatcher).Assembly.Location, "pattern", "--size", "640x360", "--frames", "10"];
        foreach (var a in offscreen ? [.. args, "--offscreen"] : args)
        {
            info.ArgumentList.Add(a);
        }

        info.Environment[variable] = value;
        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var err = await stderr;
        Interop.WriteLine($"exit {process.ExitCode}\n{await stdout}{err}");
        Assert.Equal(PatternCommand.ExitUnavailable, process.ExitCode);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("fmp: pattern ", err, StringComparison.Ordinal);
        Assert.Contains(message, err, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finding 11: without the demo's pacing and readiness gate, the bridge's back-pressure and the presenter's rules
    /// alone keep the loop going: every slot is tried at once, some are held back or dropped (allowed here), none is
    /// rejected, and every frame shown is verified bit-exact.
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Unpaced_offscreen_run_keeps_going_on_back_pressure_alone(bool blend, bool verify)
    {
        Hardware.RequireGpu();
        string[] args = ["pattern", "--offscreen", "--unpaced", "--validate", "--size", "640x360", "--frames", "3000"];
        args = blend ? [.. args, "--blend"] : args;
        var (exit, output, error) = Run(verify ? args : [.. args, "--no-verify"]);
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        Assert.Contains("pacing      none (--unpaced)", output, StringComparison.Ordinal);
        Assert.Contains("0 rejected by the presenter", output, StringComparison.Ordinal);
        Assert.Contains("synchronization checks: 0 message(s)", output, StringComparison.Ordinal);
        Assert.Contains("the bridge drained the ring and freed its import", output, StringComparison.Ordinal);
        if (verify)
        {
            // Reading every shown frame back paces the loop by itself.
            Assert.Matches(@"verified    (\d+) of \1 presented frames bit-exact", output);
        }

        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finding 9: Ctrl+C (SIGINT) or SIGTERM stops a run in the middle of its loop; it tears down and prints its verdict,
    /// and exits by it. Out of process, with a real signal.
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(2, "SIGINT")]
    [InlineData(15, "SIGTERM")]
    public async Task A_signal_stops_the_run_cleanly_with_its_verdict(int signal, string name)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("POSIX signals");
        }

        Hardware.RequireGpu();
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { typeof(CommandDispatcher).Assembly.Location, "pattern", "--offscreen", "--size", "640x360", "--duration-s", "60" })
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(4), timeout.Token); // set up and well into the loop
        Assert.False(process.HasExited, "the run ended before the signal");
        var sent = Stopwatch.StartNew();
        Assert.Equal(0, Kill(process.Id, signal));
        await process.WaitForExitAsync(timeout.Token);
        var exitMs = sent.Elapsed.TotalMilliseconds;
        var (o, e) = (await stdout, await stderr);
        Interop.WriteLine($"exit {process.ExitCode} {exitMs:F0} ms after {name}\n{o}{e}");
        Assert.Equal(0, process.ExitCode);
        Assert.Contains($"stopping on {name}", e, StringComparison.Ordinal);
        Assert.Contains("stopped by the user", o, StringComparison.Ordinal);
        Assert.Contains("the bridge drained the ring and freed its import", o, StringComparison.Ordinal);
        Assert.Matches(@"verified    (\d+) of \1 presented frames bit-exact", o);
        Assert.Contains("verdict     PASS", o, StringComparison.Ordinal);
        Assert.True(exitMs < 10_000, $"the process took {exitMs:F0} ms to exit after the signal");
    }

    /// <summary>
    /// Re-review B: in a fresh process (the JIT host, where a first use can allocate: binding a P/Invoke hands its
    /// resolver the library name) with no warm-up frames at all, the present thread allocates nothing from the first
    /// measured slot on, because the pre-roll has run every per-frame path. Without the pre-roll these runs allocate in
    /// their first slots (280 B, 608 B with blend); with the default 30 warm-up frames that normally fell inside the
    /// warm-up, except when a path was first used after it (the first wait for a free image: a 32-byte string).
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fresh_process_allocates_nothing_from_the_first_measured_slot(bool blend)
    {
        Hardware.RequireGpu();
        string[] args = ["pattern", "--offscreen", "--size", "640x360", "--frames", "240", "--rate", "240", "--warmup-frames", "0"];
        var (exit, output, error) = await RunOutOfProcess(blend ? [.. args, "--blend"] : args);
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        Assert.Contains("0 B allocated on the present thread in 240 measured slots", output, StringComparison.Ordinal);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-review B, windowed, where the loop does wait for free images (the path the 32 bytes came from): measured from
    /// slot 0, so any first use in the windowed loop lands in the measurement (280 B without the pre-roll).
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Display)]
    public async Task A_fresh_windowed_process_allocates_nothing_from_the_first_measured_slot()
    {
        Hardware.RequireDisplay();
        var (exit, output, error) = await RunOutOfProcess("pattern", "--size", "1280x720", "--window", "480x270", "--duration-s", "1", "--warmup-frames", "0");
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        Assert.Matches(@"memory      0 B allocated on the present thread in \d+ measured slots", output);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-review A and C: a producer failure between acquiring a write and signalling it (injected) fails the run with
    /// its cause, the summary says what ran before it (slots, time, rate; memory not measured because the loop failed),
    /// and the teardown in its natural order completes: the bridge abandons its import to the reaper, the CUDA buffers
    /// are freed at once, the presenter goes last, and the reaper has freed the import within 3 s. With the reaper's old
    /// GPU-side wait this run never returned (the buffer frees blocked behind it, before the presenter could force it).
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void A_producer_failure_between_acquire_and_signal_tears_down_cleanly()
    {
        Hardware.RequireGpu();
        Environment.SetEnvironmentVariable("FMP_PATTERN_FAIL_AT", "60");
        try
        {
            var watch = Stopwatch.StartNew();
            var (exit, output, _) = Run("pattern", "--offscreen", "--validate", "--size", "640x360", "--frames", "200", "--rate", "240");
            Interop.WriteLine($"took {watch.Elapsed.TotalSeconds:F1} s");
            Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
            Assert.Contains("injected producer failure at slot 60", output, StringComparison.Ordinal);
            Assert.Contains("pacing      240 Hz on CLOCK_MONOTONIC", output, StringComparison.Ordinal);
            Assert.Matches(@"frames      60 slots in 0\.2\d s: 60 presented", output);
            Assert.Contains("memory      not measured (the loop failed before its end", output, StringComparison.Ordinal);
            Assert.Contains("1 ring import(s) could not be drained in time and went to the reaper; 0 not freed within 3 s of the presenter letting go", output, StringComparison.Ordinal);
            Assert.DoesNotContain("teardown failed", output, StringComparison.Ordinal);
            Assert.Contains("synchronization checks: 0 message(s)", output, StringComparison.Ordinal);
            Assert.True(watch.Elapsed.TotalSeconds < 15, "the teardown took too long");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FMP_PATTERN_FAIL_AT", null);
        }
    }

    private static async Task<(int Exit, string Out, string Err)> RunOutOfProcess(params string[] args)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(typeof(CommandDispatcher).Assembly.Location);
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var (o, e) = (await stdout, await stderr);
        Interop.WriteLine($"exit {process.ExitCode}\n{o}{e}");
        return (process.ExitCode, o, e);
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    private static byte[] Bytes(uint[] pixels) => MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
}
