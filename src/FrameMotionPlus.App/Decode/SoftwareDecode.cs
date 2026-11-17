using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FrameMotionPlus.App.Decode;

/// <summary>
/// The ffmpeg CLI's software decode of a file's first video stream, frame by frame from a pipe, as planar 4:2:0 in the
/// decoder's own sample format (no conversion: <c>-f rawvideo</c> without <c>-pix_fmt</c>), one output frame per
/// decoded frame in presentation order (<c>-fps_mode passthrough</c>: nothing dropped or duplicated). Conformant
/// decoders are bit-exact, so NVDEC's output must equal it.
/// </summary>
internal sealed class SoftwareDecode : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdout;
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrPump;

    private SoftwareDecode(Process process, int width, int height, int bytesPerSample)
    {
        _process = process;
        _stdout = process.StandardOutput.BaseStream;
        Width = width;
        Height = height;
        BytesPerSample = bytesPerSample;
        var cw = (width + 1) / 2;
        var ch = (height + 1) / 2;
        FrameBytes = ((long)width * height + (2L * cw * ch)) * bytesPerSample;
        _stderrPump = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (_stderr)
                {
                    if (_stderr.Length < 4000)
                    {
                        _stderr.AppendLine(line);
                    }
                }
            }
        });
    }

    public int Width { get; }

    public int Height { get; }

    public int BytesPerSample { get; }

    public long FrameBytes { get; }

    public string Errors
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString().Trim();
            }
        }
    }

    /// <summary>
    /// The software decoder's output pixel format for the file's first video stream (ffprobe), e.g. yuv420p. ffprobe
    /// prints a stream once per program it belongs to as well (an MPEG-TS lists it under its program and again on its
    /// own), so the first non-empty line is the answer.
    /// </summary>
    public static string ProbePixelFormat(string ffprobe, string path)
    {
        var (exit, output, error) = Run(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt", "-of", "csv=p=0", path]);
        if (exit != 0)
        {
            throw new InvalidOperationException($"ffprobe failed on '{path}': {error.Trim()}");
        }

        return FirstLine(output);
    }

    /// <summary>The first non-empty line of <paramref name="output"/>, trimmed (empty if none).</summary>
    internal static string FirstLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Length > 0) ?? string.Empty;

    /// <summary>Starts the software decode; <paramref name="bytesPerSample"/> 1 for 8-bit, 2 for 10-bit (little-endian).</summary>
    public static SoftwareDecode Start(string ffmpeg, string path, int width, int height, int bytesPerSample)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-threads", "0", "-i", path, "-map", "0:v:0", "-an", "-sn", "-dn", "-fps_mode", "passthrough", "-f", "rawvideo", "-" })
        {
            info.ArgumentList.Add(a);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start '{ffmpeg}'");
        process.StandardInput.Close();
        return new SoftwareDecode(process, width, height, bytesPerSample);
    }

    /// <summary>Reads the next frame into <paramref name="frame"/> (at least <see cref="FrameBytes"/>); false at the end.</summary>
    public bool Read(byte[] frame)
    {
        var total = 0;
        while (total < FrameBytes)
        {
            var n = _stdout.Read(frame, total, (int)(FrameBytes - total));
            if (n == 0)
            {
                if (total == 0)
                {
                    return false;
                }

                throw new InvalidOperationException($"the software decode ended inside a frame ({total} of {FrameBytes} bytes)");
            }

            total += n;
        }

        return true;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            _process.WaitForExit(5000);
            _stderrPump.Wait(1000);
        }
        catch (Exception e) when (e is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        _process.Dispose();
    }

    /// <summary>The path of a tool next to <paramref name="ffmpeg"/> (ffprobe beside ffmpeg), else the bare name.</summary>
    public static string Sibling(string ffmpeg, string tool)
    {
        var dir = System.IO.Path.GetDirectoryName(ffmpeg);
        if (string.IsNullOrEmpty(dir))
        {
            return tool;
        }

        var candidate = System.IO.Path.Combine(dir, tool + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        return File.Exists(candidate) ? candidate : tool;
    }

    /// <summary>True when <paramref name="tool"/> runs (<c>-version</c>).</summary>
    public static bool Exists(string tool)
    {
        try
        {
            return Run(tool, ["-version"]).Exit == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    private static (int Exit, string Output, string Error) Run(string tool, string[] args)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start '{tool}'");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"'{tool}' did not finish within 60 s"));
        }

        return (process.ExitCode, output, error.Result);
    }
}
