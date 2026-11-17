using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.App.Vfg;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Vfx.Tests.Support;

namespace FrameMotionPlus.Vfx.Tests.Cli;

/// <summary>
/// <c>fmp bench vfg</c>, <c>fmp bench vfg-quality</c> and <c>fmp pattern --vfg</c>: options everywhere; short real runs
/// in-process through the dispatcher when the GPU and SDK are there; the no-SDK and no-GPU paths out of process (the
/// SDK and the driver are process-wide), which must exit 69 with one line.
/// </summary>
public sealed class VfgCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fmp-vfg-cli-" + Guid.NewGuid().ToString("N"));

    public VfgCommandTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var timeout = TimeSpan.FromSeconds(240);
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

    [Fact]
    public void Bench_vfg_defaults_are_the_design_doc_matrix_and_gate()
    {
        var o = VfgBenchOptions.Parse([]);
        Assert.Equal([(1920, 1080), (2560, 1440), (3840, 2160)], o.Sizes);
        Assert.Equal([FrameGenQuality.Low, FrameGenQuality.Medium, FrameGenQuality.High], o.Modes);
        Assert.Equal([PixelFormat.Rgba8, PixelFormat.Rgb10A2], o.Formats);
        Assert.Equal([2, 4], o.Multipliers);
        Assert.Equal(36, o.Cells);
        Assert.Equal(600, o.Frames);
        Assert.Equal(7, o.GateMs);
        Assert.Equal(FrameRate.FromHz(120), o.Rate);
        Assert.True(o.ShotChangeDetection);
        Assert.Equal(VfgContentKind.Pattern, o.Content);
        Assert.False(o.Unpaced);
    }

    [Fact]
    public void Bench_vfg_parses_every_option()
    {
        var o = VfgBenchOptions.Parse([
            "--size", "1440p,1280x720", "--mode", "medium", "--format", "10", "--multiplier", "4", "--frames", "100",
            "--warmup-frames", "5", "--content", "pan", "--rate", "240", "--scd", "off", "--verify-every", "7",
            "--gate-ms", "3.5", "--csv", "x.csv", "--vfx-sdk", "/opt/sdk", "--settings", "s.json",
        ]);
        Assert.Equal([(2560, 1440), (1280, 720)], o.Sizes);
        Assert.Equal([FrameGenQuality.Medium], o.Modes);
        Assert.Equal([PixelFormat.Rgb10A2], o.Formats);
        Assert.Equal([4], o.Multipliers);
        Assert.Equal((100, 5), (o.Frames, o.WarmupFrames));
        Assert.Equal(VfgContentKind.Pan, o.Content);
        Assert.Equal(FrameRate.FromHz(240), o.Rate);
        Assert.False(o.ShotChangeDetection);
        Assert.Equal(7, o.VerifyEvery);
        Assert.Equal(3.5, o.GateMs);
        Assert.Equal(("x.csv", "/opt/sdk", "s.json"), (o.CsvPath, o.SdkDirectory, o.SettingsPath));
        Assert.Equal(VfgContentKind.Video, VfgBenchOptions.Parse(["--video", "lavfi:testsrc2"]).Content);
        Assert.True(VfgBenchOptions.Parse(["--unpaced"]).Unpaced);
    }

    [Theory]
    [InlineData("bench", "vfg", "--size", "100")]
    [InlineData("bench", "vfg", "--size", "32x32")]
    [InlineData("bench", "vfg", "--mode", "ultra")]
    [InlineData("bench", "vfg", "--format", "12")]
    [InlineData("bench", "vfg", "--multiplier", "3")]
    [InlineData("bench", "vfg", "--frames", "1")]
    [InlineData("bench", "vfg", "--scd", "maybe")]
    [InlineData("bench", "vfg", "--content", "video")]
    [InlineData("bench", "vfg", "--unpaced", "--rate", "60")]
    [InlineData("bench", "vfg", "--bogus")]
    [InlineData("bench", "vfg-quality", "--mode", "x")]
    [InlineData("bench", "vfg-quality", "--photo", "/nonexistent.png")]
    [InlineData("bench", "vfg-quality", "--photo", "/nonexistent.png", "--no-photo")]
    [InlineData("pattern", "--vfg", "--blend")]
    [InlineData("pattern", "--quality", "high")]
    [InlineData("pattern", "--vfx-sdk", "/opt")]
    [InlineData("pattern", "--vfg", "--quality", "ultra")]
    public void Bad_options_are_usage_errors(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(CommandDispatcher.ExitUsage, CommandDispatcher.Run(args, output, error));
        Assert.StartsWith("fmp: ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Help_lists_the_vfg_commands()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CommandDispatcher.Run(["--help"], output, error));
        var help = output.ToString();
        Assert.Contains("fmp bench vfg [options]", help, StringComparison.Ordinal);
        Assert.Contains("fmp bench vfg-quality [options]", help, StringComparison.Ordinal);
        Assert.Contains("--vfg ", help, StringComparison.Ordinal);
        Assert.Contains("--vfx-sdk <dir>", help, StringComparison.Ordinal);
        Assert.Contains("--scd <on|off>", help, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(Category.Name, Category.Vfx)]
    public void A_short_matrix_runs_checks_its_outputs_and_writes_csv()
    {
        var sdk = VfxHardware.Require().Location.Directory;
        var csv = Path.Combine(_directory, "cells.csv");
        var (exit, output, error) = Run("bench", "vfg", "--size", "640x360", "--mode", "low,medium", "--format", "8", "--multiplier", "2,4",
            "--frames", "60", "--warmup-frames", "5", "--rate", "240", "--verify-every", "10", "--csv", csv, "--vfx-sdk", sdk);
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        Assert.Contains("640x360 low     RGBA8   2x", output, StringComparison.Ordinal);
        Assert.Contains("640x360 medium  RGBA8   4x", output, StringComparison.Ordinal);
        Assert.Contains("6/6 genuine, 6/6 beat blend", output, StringComparison.Ordinal);
        Assert.Contains("(same instance)", output, StringComparison.Ordinal); // the 4x cell reuses the 2x cell's instance
        Assert.Contains("gate        not evaluated", output, StringComparison.Ordinal);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
        var rows = File.ReadAllLines(csv);
        Assert.Equal(5, rows.Length);
        Assert.StartsWith("width,height,mode,format,multiplier,samples,d_p50_ns", rows[0], StringComparison.Ordinal);
        Assert.All(rows.Skip(1), r => Assert.StartsWith("640,360,", r, StringComparison.Ordinal));
    }

    [Fact]
    [Trait(Category.Name, Category.Vfx)]
    public void The_gate_fails_the_run_when_d_exceeds_it()
    {
        var sdk = VfxHardware.Require().Location.Directory;
        var (exit, output, _) = Run("bench", "vfg", "--size", "1440p", "--mode", "medium", "--format", "8", "--multiplier", "2",
            "--frames", "20", "--warmup-frames", "2", "--unpaced", "--gate-ms", "0.01", "--content", "pan", "--vfx-sdk", sdk);
        Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
        Assert.Contains("gate        FAIL: 1440p Medium p99 d", output, StringComparison.Ordinal);
        Assert.Contains("verdict     FAIL (exit code 1)", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(Category.Name, Category.Vfx)]
    public void The_quality_check_writes_png_dumps_and_an_index()
    {
        var sdk = VfxHardware.Require().Location.Directory;
        var (exit, output, error) = Run("bench", "vfg-quality", "--size", "640x360", "--mode", "low", "--out", _directory, "--no-photo", "--vfx-sdk", sdk);
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        foreach (var scene in new[] { "sdr-pattern", "sdr-pan", "pq-pattern", "pq-ramp-pan", "pq-specular", "pq-dark-gradient" })
        {
            Assert.Contains($"  {scene}", output, StringComparison.Ordinal);
            foreach (var file in new[] { "a-prev.png", "b-truth.png", "c-curr.png", "d-blend.png", "e-vfg-low.png", "f-error-low.png" })
            {
                var path = Path.Combine(_directory, scene, file);
                var bytes = File.ReadAllBytes(path);
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
                var tagged = bytes.AsSpan().IndexOf("cICP"u8) >= 0;
                Assert.Equal(scene.StartsWith("pq-", StringComparison.Ordinal) && !file.StartsWith('f'), tagged);
            }
        }

        var index = File.ReadAllText(Path.Combine(_directory, "index.md"));
        Assert.Contains("| pq-specular | low |", index, StringComparison.Ordinal);
        Assert.Contains("cICP", index, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(Category.Name, Category.Vfx)]
    [InlineData("8")]
    [InlineData("10")]
    public void Pattern_with_vfg_writes_into_the_ring_and_every_generated_frame_interpolates(string format)
    {
        var sdk = VfxHardware.Require().Location.Directory;
        var (exit, output, error) = Run("pattern", "--vfg", "--offscreen", "--validate", "--size", "1277x719", "--format", format, "--frames", "160", "--rate", "240", "--vfx-sdk", sdk);
        if (exit == PatternCommand.ExitUnavailable)
        {
            Assert.Skip(error.Trim());
        }

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error);
        Assert.Contains("160 presented", output, StringComparison.Ordinal);
        Assert.Contains("0 dropped, 0 skipped", output, StringComparison.Ordinal);
        Assert.Contains("0 rejected by the presenter", output, StringComparison.Ordinal);
        Assert.Matches(@"verified    160 of 160 presented frames: real frames bit-exact .* (\d+) of \1 genuine", output);
        Assert.Contains("0 B allocated on the present thread in 130 measured slots", output, StringComparison.Ordinal);
        Assert.Contains("synchronization checks: 0 message(s)", output, StringComparison.Ordinal);
        Assert.Contains("VFG t=0.5 into slot (d)", output, StringComparison.Ordinal);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(Category.Name, Category.Vfx)]
    public void Pattern_with_vfg_presents_in_a_window()
    {
        var sdk = VfxHardware.Require().Location.Directory;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Assert.Skip("no Wayland session (WAYLAND_DISPLAY is not set)");
        }

        var (exit, output, error) = Run("pattern", "--vfg", "--validate", "--size", "1280x720", "--window", "480x270", "--duration-s", "1.5", "--format", "10", "--vfx-sdk", sdk);
        if (exit == PatternCommand.ExitUnavailable)
        {
            Assert.Skip(error.Trim());
        }

        Assert.Equal(0, exit);
        Assert.Contains("swapchain", output, StringComparison.Ordinal);
        Assert.Contains("0 dropped", output, StringComparison.Ordinal);
        Assert.Contains("synchronization checks: 0 message(s)", output, StringComparison.Ordinal);
        Assert.Contains("verdict     PASS", output, StringComparison.Ordinal);
    }

    /// <summary>The settings file's generator.vfxSdkDirectory is where the SDK is looked for (out of process: the SDK is per process).</summary>
    [Fact]
    public async Task The_settings_file_names_the_sdk_directory()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only");
        }

        VfxHardware.RequireCuda();
        var settings = Path.Combine(_directory, "settings.json");
        var nowhere = Path.Combine(_directory, "no-sdk-here");
        File.WriteAllText(settings, $$"""{ "generator": { "vfxSdkDirectory": "{{nowhere}}" } }""");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { typeof(CommandDispatcher).Assembly.Location, "bench", "vfg", "--settings", settings })
        {
            info.ArgumentList.Add(a);
        }

        info.Environment.Remove("FMP_VFX_SDK");
        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var err = await stderr;
        Log.WriteLine($"exit {process.ExitCode}\n{await stdout}{err}");
        Assert.Equal(PatternCommand.ExitUnavailable, process.ExitCode);
        Assert.Contains(nowhere, err, StringComparison.Ordinal);
        Assert.Contains("from the settings file", err, StringComparison.Ordinal);
    }

    /// <summary>Without the SDK (an empty SDK directory) or without a GPU, each VFG command exits 69 with one line.</summary>
    [Theory]
    [InlineData("FMP_VFX_SDK", "empty", "needs the NVIDIA VFX SDK", "bench", "vfg", "--frames", "10")]
    [InlineData("FMP_VFX_SDK", "empty", "needs the NVIDIA VFX SDK", "bench", "vfg-quality", "--no-dumps")]
    [InlineData("FMP_VFX_SDK", "empty", "needs the NVIDIA VFX SDK", "pattern", "--vfg", "--offscreen", "--frames", "10")]
    [InlineData("CUDA_VISIBLE_DEVICES", "", "needs an NVIDIA GPU", "bench", "vfg", "--frames", "10")]
    [InlineData("CUDA_VISIBLE_DEVICES", "", "needs an NVIDIA GPU", "bench", "vfg-quality", "--no-dumps")]
    [InlineData("CUDA_VISIBLE_DEVICES", "", "needs an NVIDIA GPU", "pattern", "--vfg", "--offscreen", "--frames", "10")]
    public async Task Without_the_sdk_or_a_gpu_it_exits_69_with_one_line(string variable, string value, string message, params string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("the hidden-hardware variables are Linux ones");
        }

        if (variable == "FMP_VFX_SDK")
        {
            // With no GPU at all the GPU message comes first; the SDK message needs a GPU.
            VfxHardware.RequireCuda();
        }

        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(typeof(CommandDispatcher).Assembly.Location);
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        info.Environment[variable] = value == "empty" ? _directory : value;
        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var err = await stderr;
        Log.WriteLine($"exit {process.ExitCode}\n{await stdout}{err}");
        Assert.Equal(PatternCommand.ExitUnavailable, process.ExitCode);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith($"fmp: {(args[0] == "bench" ? $"bench {args[1]}" : "pattern")} ", err, StringComparison.Ordinal);
        Assert.Contains(message, err, StringComparison.Ordinal);
    }
}
