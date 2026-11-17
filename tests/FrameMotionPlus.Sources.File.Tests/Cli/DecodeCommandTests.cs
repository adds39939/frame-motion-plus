using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.App.Decode;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.Sources.File.Tests.Support;

namespace FrameMotionPlus.Sources.File.Tests.Cli;

/// <summary>
/// <c>fmp decode</c>: options; real runs in-process through the dispatcher (with --verify against the software decode and
/// the CPU reference); the no-FFmpeg and no-GPU paths out of process (libraries and the driver are per process), which
/// exit 69 with one line while other commands keep working.
/// </summary>
public sealed class DecodeCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fmp-decode-cli-" + Guid.NewGuid().ToString("N"));

    public DecodeCommandTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Options_have_defaults_and_parse()
    {
        var o = DecodeOptions.Parse(["clip.mp4"]);
        Assert.Equal("clip.mp4", o.Path);
        Assert.Equal(0, o.Frames);
        Assert.False(o.Loop);
        Assert.False(o.Verify);
        Assert.Equal("ffmpeg", o.Ffmpeg);
        Assert.Equal(4, o.DecodeAhead);
        Assert.True(o.StallProbe);
        o = DecodeOptions.Parse(["x.mkv", "--frames", "50", "--loop", "--verify", "--ffmpeg", "/opt/ffmpeg", "--decode-ahead", "8", "--no-stall-probe", "--csv", "out.csv"]);
        Assert.Equal((50L, true, true, "/opt/ffmpeg", 8, false, "out.csv"), (o.Frames, o.Loop, o.Verify, o.Ffmpeg, o.DecodeAhead, o.StallProbe, o.CsvPath));
        Assert.Equal(2.5, DecodeOptions.Parse(["x", "--duration-s", "2.5"]).DurationS);
        Assert.Throws<UsageException>(() => DecodeOptions.Parse([]));
        Assert.Throws<UsageException>(() => DecodeOptions.Parse(["--verify"]));
        Assert.Throws<UsageException>(() => DecodeOptions.Parse(["x", "--frames", "5", "--duration-s", "1"]));
        Assert.Throws<UsageException>(() => DecodeOptions.Parse(["x", "--decode-ahead", "0"]));
        Assert.Throws<UsageException>(() => DecodeOptions.Parse(["x", "--bogus"]));
    }

    [Fact]
    public void Usage_errors_exit_2()
    {
        var (exit, _, err) = Run("decode");
        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Contains("decode needs a file", err, StringComparison.Ordinal);
    }

    /// <summary>
    /// An 8-bit H.264 clip with B-frames, an HEVC Main10 PQ one and an MPEG-TS (whose stream ffprobe lists twice, once
    /// under its program), every frame verified, CSV written.
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Media)]
    [InlineData("h264", "h264 (High) 640x360 8-bit Nv12")]
    [InlineData("hevc10-pq", "hevc (Main 10) 640x360 10-bit P010")]
    [InlineData("h264-ts", "h264 (High) 640x360 8-bit Nv12")]
    public void Decode_verify_passes_and_reports(string clip, string stream)
    {
        var env = Media.Require();
        var csv = Path.Combine(_directory, clip + ".csv");
        var (exit, output, err) = Run("decode", env.Clip(clip), "--verify", "--csv", csv);
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains(stream, output, StringComparison.Ordinal);
        Assert.Contains("decoded planes bit-exact with the software decode; RGB bit-exact with the CPU reference", output, StringComparison.Ordinal);
        Assert.Contains("FFmpeg frame-pool growth after start-up 0", output, StringComparison.Ordinal);
        Assert.Contains("consumer path 0 B", output, StringComparison.Ordinal);
        Assert.Contains("other thread", output, StringComparison.Ordinal);
        Assert.Contains("verdict     pass", output, StringComparison.Ordinal);
        var rows = System.IO.File.ReadAllLines(csv);
        Assert.Equal("frame,content_ns,media_ns,host_decode_ns,decode_copy_gpu_ns,convert_gpu_ns,queue_depth,discontinuity,format_changed,events", rows[0]);
        Assert.Equal(env.FrameTimes(env.Clip(clip)).Length, rows.Length - 1);
        if (clip == "hevc10-pq")
        {
            Assert.Contains("Pq transfer", output, StringComparison.Ordinal);
            Assert.Contains("MaxCLL 1000, MaxFALL 400", output, StringComparison.Ordinal);
            Assert.Contains("0.0001-1000 nits", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(Category.Name, Category.Media)]
    public void Looping_and_variable_frame_rate_are_reported()
    {
        var env = Media.Require();
        var (exit, output, err) = Run("decode", env.Clip("vfr"), "--loop", "--frames", "70", "--no-stall-probe");
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("variable frame rate", output, StringComparison.Ordinal);
        Assert.Contains("discontinuities 2", output, StringComparison.Ordinal);
        Assert.Contains("frames      70 in", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// An elementary stream (no timestamps, no time index) loops and verifies: each lap restarts the software decode,
    /// which must have ended where ours did.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Media)]
    public void An_elementary_stream_loops_and_verifies()
    {
        var env = Media.Require();
        var (exit, output, err) = Run("decode", env.Clip("raw-h264"), "--loop", "--verify", "--frames", "60", "--no-stall-probe");
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("frames      60 in", output, StringComparison.Ordinal);
        Assert.Contains("2 loops, 0 seeks; 2 from the first byte; 0 failed", output, StringComparison.Ordinal);
        Assert.Contains("verdict     pass", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// An MPEG-TS with a keyframe every second loops and verifies: each lap restarts at the first frame (by byte), not at
    /// the next keyframe after the first packet FFmpeg's time seek finds.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Media)]
    public void An_mpeg_ts_loops_from_its_first_frame_and_verifies()
    {
        var env = Media.Require();
        var (exit, output, err) = Run("decode", env.Clip("ts-2s"), "--loop", "--verify", "--frames", "150", "--no-stall-probe");
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("verify      150 frames: decoded planes bit-exact", output, StringComparison.Ordinal);
        Assert.Contains("2 loops, 0 seeks; 2 from the first byte", output, StringComparison.Ordinal);
        Assert.Contains("gaps forward over 10 s are marked and collapsed to one frame", output, StringComparison.Ordinal);
        Assert.Contains("verdict     pass", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two recordings concatenated: the jump in the stream's timestamps is reported (where, at which media time) and is not
    /// a restart for the software decode, which goes straight through; every frame verifies.
    /// </summary>
    [Fact]
    [Trait(Category.Name, Category.Media)]
    public void A_jump_in_the_stream_timestamps_is_reported_and_verifies()
    {
        var env = Media.Require();
        var (exit, output, err) = Run("decode", env.Clip("ts-concat"), "--verify", "--no-stall-probe");
        Assert.True(exit == CommandDispatcher.ExitOk, output + err);
        Assert.Contains("1 jumps in the stream's own timestamps (frame 30 at media 0.000 s)", output, StringComparison.Ordinal);
        Assert.Contains("0 isolated collisions moved 1 ns on; 0 steps under 1 µs, 0 runs of them", output, StringComparison.Ordinal);
        Assert.Contains("discontinuities 1", output, StringComparison.Ordinal);
        Assert.Contains("verify      60 frames", output, StringComparison.Ordinal);
        Assert.Contains("verdict     pass", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// --verify catches a difference of one bit, wherever it is: an ffmpeg wrapper changes the software decode (one luma
    /// sample of frame 30, one Cr sample of the last frame, one 10-bit luma sample, or one frame too many at the end) and
    /// the run fails (exit 1) naming what differs. A 10-bit difference is reported in code values on both sides.
    /// </summary>
    [Theory]
    [Trait(Category.Name, Category.Media)]
    [InlineData("h264", "lum=if(eq(N\\,30)*eq(X\\,5)*eq(Y\\,7)\\,FLIP(lum)\\,lum(X\\,Y)):cb=cb(X\\,Y):cr=cr(X\\,Y)", "frame 30 (content 1000.000 ms): decoded luma (5, 7): ")]
    [InlineData("h264", "lum=lum(X\\,Y):cb=cb(X\\,Y):cr=if(eq(N\\,44)*eq(X\\,3)*eq(Y\\,2)\\,FLIP(cr)\\,cr(X\\,Y))", "frame 44 (content 1466.667 ms): decoded chroma (3, 2): ")]
    [InlineData("hevc10-pq", "lum=if(eq(N\\,3)*eq(X\\,10)*eq(Y\\,4)\\,FLIP(lum)\\,lum(X\\,Y)):cb=cb(X\\,Y):cr=cr(X\\,Y)", "frame 3 (content 100.000 ms): decoded luma (10, 4): ")]
    [InlineData("h264", "", "the software decode had 1 frames more")]
    public void Verify_fails_on_a_single_changed_bit_or_an_extra_frame(string clip, string geq, string expected)
    {
        var env = Media.Require();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("the ffmpeg wrapper is a bash script");
        }

        var filter = geq.Length == 0 ? "tpad=stop=1" : "geq=" + geq
            .Replace("FLIP(lum)", "if(mod(lum(X\\,Y)\\,2)\\,lum(X\\,Y)-1\\,lum(X\\,Y)+1)", StringComparison.Ordinal)
            .Replace("FLIP(cr)", "if(mod(cr(X\\,Y)\\,2)\\,cr(X\\,Y)-1\\,cr(X\\,Y)+1)", StringComparison.Ordinal);
        var wrapper = Wrapper(env.Ffmpeg!, filter);
        var (exit, output, err) = Run("decode", env.Clip(clip), "--verify", "--ffmpeg", wrapper, "--no-stall-probe");
        Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
        Assert.Contains("verdict     FAIL", output, StringComparison.Ordinal);
        Assert.Contains(expected, output, StringComparison.Ordinal);
        if (expected.Contains("luma", StringComparison.Ordinal) || expected.Contains("chroma", StringComparison.Ordinal))
        {
            // "... (x, y): ours vs theirs" in code values: one apart (the low bit), whatever the bit depth.
            var tail = output[(output.IndexOf(expected, StringComparison.Ordinal) + expected.Length)..].Split('\n')[0];
            var values = tail.Split(" vs ").Select(v => v.Split(' ')[0].Split(',')).ToArray();
            var ours = values[0].Select(int.Parse).ToArray();
            var theirs = values[1].Select(int.Parse).ToArray();
            Assert.Equal(1, ours.Zip(theirs, (a, b) => Math.Abs(a - b)).Sum());
            Assert.All(ours.Concat(theirs), v => Assert.InRange(v, 0, clip.Contains("10", StringComparison.Ordinal) ? 1023 : 255));
        }

        _ = err;
    }

    /// <summary>The verdict counts every check, not only the comparisons (each of these alone fails a run).</summary>
    [Fact]
    public void The_verdict_fails_on_any_failed_check()
    {
        Assert.True(new DecodeResult().Passed);
        var failing = new Func<DecodeResult>[]
        {
            () => new DecodeResult { Statistics = new FileSourceStatistics { CorruptPackets = 1 } },
            () => new DecodeResult { Statistics = new FileSourceStatistics { CorruptFrames = 1 } },
            () => new DecodeResult { Statistics = new FileSourceStatistics { DecodeThreadAllocatedBytes = 24 } },
            () => new DecodeResult { Statistics = new FileSourceStatistics { PoolGrowth = 1 } },
            () => new DecodeResult { Statistics = new FileSourceStatistics { FailedSeeks = 1 } },
            () => new DecodeResult { DemuxError = "Invalid data found when processing input" },
            () => new DecodeResult { CollapsedRuns = 1 },
            () => new DecodeResult { SoftwareFramesExtra = 1 },
            () => new DecodeResult { SoftwareFramesShort = 1 },
            () => new DecodeResult { NonIncreasing = 1 },
            () => new DecodeResult { YuvMismatches = 1 },
            () => new DecodeResult { RgbMismatches = 1 },
            () => new DecodeResult { ConsumerAllocatedBytes = 1 },
            () => new DecodeResult { Leaked = true },
            () => new DecodeResult { Failure = "no frame for 30 s" },
        };
        foreach (var make in failing)
        {
            var r = make();
            Assert.False(r.Passed, string.Join("; ", r.Problems()));
            Assert.Single(r.Problems());
        }
    }

    /// <summary>ffprobe lists an MPEG-TS stream once under its program and once on its own: the first line is the answer.</summary>
    [Fact]
    public void The_probed_pixel_format_is_the_first_line()
    {
        Assert.Equal("yuv420p", SoftwareDecode.FirstLine("yuv420p\n\nyuv420p\n"));
        Assert.Equal("yuv420p10le", SoftwareDecode.FirstLine("\n yuv420p10le \n"));
        Assert.Equal(string.Empty, SoftwareDecode.FirstLine(string.Empty));
    }

    [Fact]
    [Trait(Category.Name, Category.Media)]
    public void An_unplayable_or_missing_file_exits_1_with_one_line()
    {
        var env = Media.Require();
        var (exit, output, err) = Run("decode", env.Clip("mjpeg"));
        Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("fmp: decode: ", err, StringComparison.Ordinal);
        Assert.Contains("mjpeg", err, StringComparison.Ordinal);
        (exit, _, err) = Run("decode", Path.Combine(_directory, "missing.mp4"));
        Assert.Equal(CommandDispatcher.ExitCheckFailed, exit);
        Assert.Contains("no such file", err, StringComparison.Ordinal);
        _ = output;
    }

    /// <summary>--verify without the ffmpeg CLI cannot check anything: 69 with one line.</summary>
    [Fact]
    [Trait(Category.Name, Category.Media)]
    public void Verify_without_the_ffmpeg_cli_exits_69()
    {
        var env = Media.Require();
        var (exit, _, err) = Run("decode", env.Clip("h264"), "--verify", "--ffmpeg", Path.Combine(_directory, "no-ffmpeg"));
        Assert.Equal(PatternCommand.ExitUnavailable, exit);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("needs the ffmpeg CLI", err, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without the FFmpeg 9 libraries ($FMP_FFMPEG_DIR pointing at an empty directory) or without a GPU, <c>fmp decode</c>
    /// exits 69 with one line, and the rest of the application is unaffected (<c>fmp simulate --check</c>, <c>--help</c>).
    /// </summary>
    [Theory]
    [InlineData("FMP_FFMPEG_DIR", "empty", "needs the FFmpeg 9 shared libraries")]
    [InlineData("CUDA_VISIBLE_DEVICES", "", "needs an NVIDIA GPU")]
    public async Task Without_ffmpeg_or_a_gpu_decode_exits_69_and_other_commands_work(string variable, string value, string message)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("the hidden-library variables are Linux ones");
        }

        if (variable == "FMP_FFMPEG_DIR")
        {
            // With no GPU at all the GPU message comes first; the FFmpeg message needs a GPU.
            Media.RequireCuda();
        }

        var environment = new Dictionary<string, string> { [variable] = value == "empty" ? _directory : value };
        var clip = Path.Combine(_directory, "any.mp4");
        System.IO.File.WriteAllBytes(clip, [0]);
        var (exit, _, err) = await RunOutOfProcess(environment, "decode", clip);
        Assert.Equal(PatternCommand.ExitUnavailable, exit);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("fmp: decode ", err, StringComparison.Ordinal);
        Assert.Contains(message, err, StringComparison.Ordinal);

        var (simulate, output, simulateErr) = await RunOutOfProcess(environment, "simulate", "--duration-s", "5", "--check");
        Assert.True(simulate == CommandDispatcher.ExitOk, output + simulateErr);
        var (help, helpOutput, _) = await RunOutOfProcess(environment, "--help");
        Assert.Equal(CommandDispatcher.ExitOk, help);
        Assert.Contains("fmp decode <file>", helpOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bash wrapper around the real ffmpeg that adds <paramref name="filter"/> to the software decode (the run whose
    /// output is a pipe, '-'), and passes everything else (-version) through.
    /// </summary>
    private string Wrapper(string ffmpeg, string filter)
    {
        var path = Path.Combine(_directory, $"ffmpeg-{Guid.NewGuid():N}");
        System.IO.File.WriteAllText(path,
            "#!/bin/bash\n" +
            "args=(\"$@\")\n" +
            "n=${#args[@]}\n" +
            $"if [[ $n -gt 0 && \"${{args[n-1]}}\" == \"-\" ]]; then exec '{ffmpeg}' \"${{args[@]:0:n-1}}\" -vf '{filter}' -; fi\n" +
            $"exec '{ffmpeg}' \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

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

    private static async Task<(int Exit, string Out, string Err)> RunOutOfProcess(IReadOnlyDictionary<string, string> environment, params string[] args)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(typeof(CommandDispatcher).Assembly.Location);
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        foreach (var (k, v) in environment)
        {
            info.Environment[k] = v;
        }

        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var (o, e) = (await stdout, await stderr);
        Log.WriteLine($"exit {process.ExitCode}\n{o}{e}");
        return (process.ExitCode, o, e);
    }
}
