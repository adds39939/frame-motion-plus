using System.Globalization;
using FrameMotionPlus.App.Bench;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Vfx;

namespace FrameMotionPlus.App.Vfg;

/// <summary>Printing, CSV and the gate verdict of <c>fmp bench vfg</c>.</summary>
internal static class VfgReport
{
    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public const string Header =
        "  size   mode    format   M     d p50    p90    p99    max   host p50    p99   load ms  warm ms   VRAM MiB  check";

    public static string ModeName(FrameGenQuality mode) => mode switch
    {
        FrameGenQuality.Low => "low",
        FrameGenQuality.High => "high",
        _ => "medium",
    };

    public static string Row(VfgCellResult c)
    {
        var check = c.Checked == 0
            ? "not checked"
            : c.TruthChecked > 0
                ? string.Create(Ic, $"{c.Checked - c.Copies}/{c.Checked} genuine, {c.BeatBlend}/{c.TruthChecked} beat blend ({c.MeanPsnrVfg:0.0} vs {c.MeanPsnrBlend:0.0} dB)")
                : string.Create(Ic, $"{c.Checked - c.Copies}/{c.Checked} genuine");
        return string.Create(Ic,
            $"  {VfgBenchOptions.SizeName(c.Size),-6} {ModeName(c.Mode),-7} {VfgBenchOptions.FormatName(c.Format),-7} {c.Multiplier}x {Ms(c.D.P50),7} {Ms(c.D.P90),6} {Ms(c.D.P99),6} {Ms(c.D.Max),6}" +
            $"  {Ms(c.Host.P50),8} {Ms(c.Host.P99),6} {LoadColumns(c.Load)}  {check}" +
            $"{(c.Late > 0 ? $", {c.Late} late" : string.Empty)}{(c.DroppedSpans > 0 ? $", {c.DroppedSpans} spans lost" : string.Empty)}");
    }

    public static string Ms(long ns) => (ns / 1e6).ToString("0.00", Ic);

    /// <summary>Load ms, warm-up ms and VRAM MiB, or "(same instance)" when the cell reused the previous cell's.</summary>
    private static string LoadColumns(VfgLoadReport load) => load.LoadNs == 0
        ? $"{"(same instance)",29}"
        : string.Create(Ic, $"{load.LoadNs / 1e6,9:0.0} {load.WarmupNs / 1e6,8:0.0} {load.DeviceBytes / 1048576.0,10:0}");

    public static void PrintHeader(TextWriter o, VfgBenchOptions options, CudaDevice device, int cudaVersion)
    {
        o.WriteLine(string.Create(Ic,
            $"fmp bench vfg: {options.Cells} cells, {options.Frames} generated frames each after {options.WarmupFrames} warm-up, " +
            $"{(options.Unpaced ? "unpaced (back to back)" : $"paced at {options.Rate} Hz output (2x: {options.Rate.Hz / 2:0.#} runs/s, 4x: {options.Rate.Hz * 3 / 4:0.#} runs/s)")}, " +
            $"content {ContentName(options)}, automatic shot-change detection {(options.ShotChangeDetection ? "on" : "off")}"));
        o.WriteLine(string.Create(Ic,
            $"  machine     {MachineInfo.RuntimeKind()} .NET {Environment.Version}, {device.Name}, CUDA driver API {cudaVersion / 1000}.{cudaVersion % 1000 / 10}, " +
            $"load average {MachineInfo.LoadAverage() ?? "n/a"}"));
        o.WriteLine($"  sdk         VFX SDK {VfxSdk.Version} from {VfxSdk.Location}");
        o.WriteLine("  d           GPU time of each generated frame's run on VFG's stream (CUDA event span), ms; host = wall time of the Enqueue call, ms");
        o.WriteLine("  load        create + NvVFX_Load, then the generator's warm-up runs; VRAM = device memory the instance took (load and warm-up)");
        o.WriteLine(Header);
    }

    public static string ContentName(VfgBenchOptions options) => options.Content switch
    {
        VfgContentKind.Pattern => "test pattern (box moving 4 x W/240 px per source frame)",
        VfgContentKind.Pan => "textured pan (whole-pixel steps, motion everywhere)",
        _ => $"video '{options.Video}' (decoded by ffmpeg)",
    };

    /// <summary>The gate verdict lines; true when it passed (or was not evaluated: no 1440p Medium cell).</summary>
    public static bool PrintVerdict(TextWriter o, VfgBenchOptions options, IReadOnlyList<VfgCellResult> cells)
    {
        var gate = cells.Where(static c => c.IsGateCell).ToList();
        var gateNs = (long)(options.GateMs * 1e6);
        var problems = cells.Where(static c => c.Problem is not null).ToList();
        foreach (var c in problems)
        {
            o.WriteLine($"  INVALID     {VfgBenchOptions.SizeName(c.Size)} {ModeName(c.Mode)} {VfgBenchOptions.FormatName(c.Format)} {c.Multiplier}x: {c.Problem}");
        }

        var passed = problems.Count == 0;
        if (gate.Count == 0)
        {
            o.WriteLine(string.Create(Ic, $"  gate        not evaluated: the matrix has no 1440p Medium cell (gate: p99 d <= {options.GateMs} ms)"));
        }
        else
        {
            var worst = gate.MaxBy(static c => c.D.P99)!;
            var gatePassed = gate.All(c => c.D.P99 <= gateNs && c.D.Count > 0);
            passed &= gatePassed;
            o.WriteLine(string.Create(Ic,
                $"  gate        {(gatePassed ? "PASS" : "FAIL")}: 1440p Medium p99 d {Ms(worst.D.P99)} ms at worst ({VfgBenchOptions.FormatName(worst.Format)} {worst.Multiplier}x) " +
                $"over {gate.Count} cell(s), gate <= {options.GateMs} ms (design doc Phase 0)"));
        }

        o.WriteLine($"  verdict     {(passed ? "PASS" : "FAIL")} (exit code {(passed ? CommandDispatcher.ExitOk : CommandDispatcher.ExitCheckFailed)})");
        return passed;
    }

    public static void WriteCsv(string path, IReadOnlyList<VfgCellResult> cells)
    {
        using var w = new StreamWriter(path, append: false);
        w.WriteLine("width,height,mode,format,multiplier,samples,d_p50_ns,d_p90_ns,d_p99_ns,d_max_ns,d_mean_ns,host_p50_ns,host_p90_ns,host_p99_ns,host_max_ns," +
                    "create_ns,load_ns,warmup_ns,vram_bytes,first_load_in_process,late,checked,copies,truth_checked,beat_blend,psnr_vfg_db,psnr_blend_db,spans_lost");
        foreach (var c in cells)
        {
            w.WriteLine(string.Create(Ic,
                $"{c.Size.Width},{c.Size.Height},{ModeName(c.Mode)},{VfgBenchOptions.FormatName(c.Format)},{c.Multiplier},{c.D.Count},{c.D.P50},{c.D.P90},{c.D.P99},{c.D.Max},{c.D.Mean:0}," +
                $"{c.Host.P50},{c.Host.P90},{c.Host.P99},{c.Host.Max},{c.Load.CreateNs},{c.Load.LoadNs},{c.Load.WarmupNs},{c.Load.DeviceBytes},{(c.Load.FirstInProcess ? 1 : 0)}," +
                $"{c.Late},{c.Checked},{c.Copies},{c.TruthChecked},{c.BeatBlend},{c.MeanPsnrVfg:0.00},{c.MeanPsnrBlend:0.00},{c.DroppedSpans}"));
        }
    }
}

/// <summary>The GPU and the VFX SDK a VFG command needs; missing either is a one-line <see cref="HardwareUnavailableException"/>.</summary>
internal static class VfgHardware
{
    /// <summary>
    /// The CUDA device and the SDK, found from <paramref name="sdkDirectory"/> (an option), $FMP_VFX_SDK, the settings file
    /// at <paramref name="settingsPath"/> (generator.vfxSdkDirectory), a checkout's lib/VideoFX or /usr/local/VideoFX.
    /// </summary>
    public static CudaDevice Require(string? sdkDirectory, string? settingsPath)
    {
        var settingsDirectory = settingsPath is null ? null : SettingsCommand.Load(settingsPath).Generator.VfxSdkDirectory;
        if (!CudaDriver.TryInitialize(out var reason))
        {
            throw new HardwareUnavailableException($"needs an NVIDIA GPU with the CUDA driver: {reason}");
        }

        if (CudaDriver.DeviceCount == 0)
        {
            throw new HardwareUnavailableException("needs an NVIDIA GPU: the CUDA driver reports no device");
        }

        if (!VfxSdk.TryLoad(out var why, sdkDirectory, settingsDirectory))
        {
            throw new HardwareUnavailableException($"needs the NVIDIA VFX SDK with Video Frame Generation: {why}");
        }

        return CudaDriver.GetDevice(0);
    }
}
