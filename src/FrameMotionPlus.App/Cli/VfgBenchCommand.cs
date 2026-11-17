using System.Globalization;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.App.Vfg;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;

namespace FrameMotionPlus.App.Cli;

/// <summary>
/// <c>fmp bench vfg</c>: VFG's cost per generated frame over sizes × modes × formats × multipliers (design doc
/// Phase 0), with a gate on 1440p Medium. Exit 0 when the gate passes (or the matrix has no 1440p Medium cell) and
/// every checked output is a genuine interpolation; 1 otherwise; 69 with one line when the GPU or the VFX SDK is not
/// there.
/// </summary>
internal static class VfgBenchCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var options = VfgBenchOptions.Parse(args);
        if (options.CsvPath is { } csv)
        {
            // Fail on an unwritable path before measuring for minutes, not after.
            CheckWritable(csv);
        }

        CudaDevice device;
        try
        {
            device = VfgHardware.Require(options.SdkDirectory, options.SettingsPath);
        }
        catch (HardwareUnavailableException e)
        {
            error.WriteLine($"fmp: bench vfg {e.Message}");
            return PatternCommand.ExitUnavailable;
        }

        using var cuda = CudaContext.RetainPrimary(device);
        using var kernels = ImageKernels.Load(cuda);
        VfgReport.PrintHeader(output, options, device, CudaDriver.DriverVersion);
        output.Flush();
        List<VfgCellResult> cells;
        try
        {
            cells = new VfgBench(options, cuda, kernels, output).Run();
        }
        catch (HardwareUnavailableException e)
        {
            error.WriteLine($"fmp: bench vfg {e.Message}");
            return PatternCommand.ExitUnavailable;
        }

        var first = cells.FirstOrDefault(static c => c.Load.FirstInProcess);
        if (first is not null)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  first load  {first.Load.LoadNs / 1e6:0.0} ms in this process ({VfgBenchOptions.SizeName(first.Size)} {VfgReport.ModeName(first.Mode)} " +
                $"{VfgBenchOptions.FormatName(first.Format)}), including the SDK's one-time start-up; the CUDA JIT cache (~/.nv/ComputeCache or $CUDA_CACHE_PATH) decides cold vs warm"));
        }

        if (options.CsvPath is { } path)
        {
            VfgReport.WriteCsv(path, cells);
            output.WriteLine($"  csv         {cells.Count} cells written to {path}");
        }

        return VfgReport.PrintVerdict(output, options, cells) ? CommandDispatcher.ExitOk : CommandDispatcher.ExitCheckFailed;
    }

    internal static void CheckWritable(string path)
    {
        try
        {
            using var _ = new StreamWriter(path, append: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new UsageException($"Cannot write '{path}': {e.Message}", e);
        }
    }
}
