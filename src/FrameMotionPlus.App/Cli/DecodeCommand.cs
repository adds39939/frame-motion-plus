using FrameMotionPlus.App.Decode;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Sources.File;

namespace FrameMotionPlus.App.Cli;

/// <summary>
/// <c>fmp decode &lt;file&gt;</c>: decodes a file through the real source path (FFmpeg demux, NVDEC in the app's CUDA
/// context, the convert kernel, the frame history) and checks it (<see cref="DecodeRun"/>). Exit 0 when every check
/// passes; 1 on a mismatch, a failed check or a file that cannot be played (one line saying why); 69 with one line when
/// the GPU, the FFmpeg 9 libraries or (for --verify) the ffmpeg CLI is missing.
/// </summary>
internal static class DecodeCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var options = DecodeOptions.Parse(args);
        if (options.CsvPath is { } csv)
        {
            VfgBenchCommand.CheckWritable(csv);
        }

        CudaDevice device;
        try
        {
            device = DecodeRun.Require(options);
        }
        catch (HardwareUnavailableException e)
        {
            error.WriteLine($"fmp: decode {e.Message}");
            return PatternCommand.ExitUnavailable;
        }

        if (!File.Exists(options.Path))
        {
            error.WriteLine($"fmp: decode: no such file: {options.Path}");
            return CommandDispatcher.ExitCheckFailed;
        }

        using var cuda = CudaContext.RetainPrimary(device);
        using var kernels = ImageKernels.Load(cuda);
        var run = new DecodeRun(options, output);
        DecodeResult result;
        try
        {
            result = run.Run(cuda, kernels);
        }
        catch (FileSourceException e)
        {
            error.WriteLine($"fmp: decode: {e.Message}");
            return CommandDispatcher.ExitCheckFailed;
        }
        catch (FFmpegUnavailableException e)
        {
            error.WriteLine($"fmp: decode needs the FFmpeg 9 shared libraries: {e.Message}");
            return PatternCommand.ExitUnavailable;
        }

        run.Print(result);
        if (options.CsvPath is { } path)
        {
            DecodeRun.WriteCsv(path, result);
            output.WriteLine($"  csv         {result.Timings.Count} frames written to {path}");
        }

        return result.Passed ? CommandDispatcher.ExitOk : CommandDispatcher.ExitCheckFailed;
    }
}
