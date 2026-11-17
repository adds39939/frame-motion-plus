using System.Globalization;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.App.Vfg;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Vfx;

namespace FrameMotionPlus.App.Cli;

/// <summary>
/// <c>fmp bench vfg-quality</c>: the design doc's PQ check (specular highlights, dark gradients, banding) and its SDR
/// counterpart: VFG's middle frame of scenes with known motion, scored against the truth per quality mode, with frame
/// dumps (16-bit PNG) and an index for inspection by eye. Exit 0 when it ran; 69 with one line without a GPU or SDK.
/// </summary>
internal static class VfgQualityCommand
{
    private static readonly HashSet<string> FlagNames = new(StringComparer.Ordinal) { "no-dumps", "no-photo" };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var r = new ArgumentReader(args, FlagNames);
        var (width, height) = r.String("size") is { } size ? VfgBenchOptions.ParseSize(size) : (1920, 1080);
        var modes = r.String("mode") is { } m
            ? m.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(VfgBenchOptions.ParseMode).Distinct().ToList()
            : [FrameGenQuality.Low, FrameGenQuality.Medium, FrameGenQuality.High];
        var dumps = !r.Flag("no-dumps");
        var outDirectory = r.String("out") ?? DefaultOutDirectory();
        var noPhoto = r.Flag("no-photo");
        var photo = r.String("photo");
        var sdkDirectory = r.String("vfx-sdk");
        var settingsPath = r.String("settings");
        r.EnsureAllConsumed();
        if (photo is not null && noPhoto)
        {
            throw new UsageException("'--photo' and '--no-photo' contradict each other.");
        }

        if (photo is not null && !File.Exists(photo))
        {
            throw new UsageException($"'--photo': no file '{photo}'.");
        }

        photo ??= noPhoto ? null : QualityScenes.PhotoCandidates.FirstOrDefault(File.Exists);
        CudaDevice device;
        try
        {
            device = VfgHardware.Require(sdkDirectory, settingsPath);
        }
        catch (HardwareUnavailableException e)
        {
            error.WriteLine($"fmp: bench vfg-quality {e.Message}");
            return PatternCommand.ExitUnavailable;
        }

        var ic = CultureInfo.InvariantCulture;
        var header = string.Create(ic, $"{width}x{height}, modes {string.Join("/", modes.Select(VfgReport.ModeName))}, {device.Name}, VFX SDK {VfxSdk.Version}");
        output.WriteLine($"fmp bench vfg-quality: {header}, t = 0.5, {(dumps ? $"frames written to {outDirectory}" : "no frame dumps")}");
        if (photo is null)
        {
            output.WriteLine("  photo       none (no --photo and none of the usual wallpapers here): the sdr-photo and pq-photo scenes are skipped");
        }

        using var cuda = CudaContext.RetainPrimary(device);
        using var kernels = ImageKernels.Load(cuda);
        List<QualityScene> scenes;
        try
        {
            scenes = QualityScenes.Build(width, height, cuda, kernels, photo, output);
        }
        catch (HardwareUnavailableException e)
        {
            error.WriteLine($"fmp: bench vfg-quality {e.Message}");
            return PatternCommand.ExitUnavailable;
        }

        if (dumps)
        {
            Directory.CreateDirectory(outDirectory);
        }

        output.WriteLine(VfgQuality.Header);
        var results = new VfgQuality(cuda, width, height, modes, dumps ? outDirectory : null).Run(scenes, output);
        if (dumps)
        {
            var index = Path.Combine(outDirectory, "index.md");
            VfgQuality.WriteIndex(index, scenes, results, width, height, header);
            output.WriteLine($"  index       {Path.GetFullPath(index)}");
        }

        foreach (var worse in results.Where(static x => x.PsnrVfg <= x.PsnrBlend))
        {
            output.WriteLine(string.Create(ic,
                $"  note        {worse.Scene.Name} {VfgReport.ModeName(worse.Mode)}: VFG {worse.PsnrVfg:0.0} dB is not better than the blend {worse.PsnrBlend:0.0} dB"));
        }

        return CommandDispatcher.ExitOk;
    }

    /// <summary><c>out/vfg-quality</c> in the repository checkout the program runs in (git-ignored), else under the current directory.</summary>
    private static string DefaultOutDirectory()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FrameMotionPlus.slnx")))
                {
                    return Path.Combine(dir.FullName, "out", "vfg-quality");
                }
            }
        }

        return Path.Combine(Environment.CurrentDirectory, "out", "vfg-quality");
    }
}
