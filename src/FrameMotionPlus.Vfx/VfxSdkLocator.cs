namespace FrameMotionPlus.Vfx;

/// <summary>Where a <see cref="VfxSdkLocation"/> came from, in the order <see cref="VfxSdkLocator.Resolve"/> tries them.</summary>
public enum VfxSdkSource
{
    /// <summary>Given by the caller (a command-line option).</summary>
    Explicit = 0,

    /// <summary>The <see cref="VfxSdkLocator.EnvironmentVariable"/> environment variable.</summary>
    Environment = 1,

    /// <summary>The settings file (<c>generator.vfxSdkDirectory</c>).</summary>
    Settings = 2,

    /// <summary><c>lib/VideoFX</c> in the repository checkout the program was built into (a developer machine).</summary>
    DevCheckout = 3,

    /// <summary>Where NVIDIA's installer puts the SDK: <c>/usr/local/VideoFX</c> on Linux.</summary>
    SystemDefault = 4,
}

/// <summary>
/// The VFX SDK to load: its root (holding <c>lib/</c> and, in NVIDIA's Linux archive, <c>external/cuda/lib/</c> with
/// the CUDA 12 runtime it was built against) and the Video Frame Generation feature's root (holding <c>lib/</c>).
/// </summary>
public sealed record VfxSdkLocation
{
    public required string Directory { get; init; }

    /// <summary>Defaults to <c>features/nvvfxvideoframegeneration</c> under <see cref="Directory"/>, NVIDIA's installer layout.</summary>
    public required string FeatureDirectory { get; init; }

    public VfxSdkSource Source { get; init; }

    /// <summary>A location with the installer's layout below <paramref name="directory"/>.</summary>
    public static VfxSdkLocation At(string directory, VfxSdkSource source = VfxSdkSource.Explicit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var full = Path.GetFullPath(directory);
        return new VfxSdkLocation
        {
            Directory = full,
            FeatureDirectory = Path.Combine(full, "features", VfxSdkLocator.FeatureName),
            Source = source,
        };
    }

    public string SourceDescription => Source switch
    {
        VfxSdkSource.Explicit => "given explicitly",
        VfxSdkSource.Environment => $"from ${VfxSdkLocator.EnvironmentVariable}",
        VfxSdkSource.Settings => "from the settings file",
        VfxSdkSource.DevCheckout => "the repository checkout's lib/VideoFX",
        _ => "the installer's default",
    };

    public override string ToString() => $"{Directory} ({SourceDescription})";
}

/// <summary>
/// Finds the VFX SDK. Nothing from the SDK is in the repository or the build output; it is looked for, in order, in
/// the directory given explicitly, <c>$FMP_VFX_SDK</c>, the settings file's <c>generator.vfxSdkDirectory</c>,
/// <c>lib/VideoFX</c> of a repository checkout that contains the running program (never one found from the current
/// directory: native code is not loaded from wherever the program happens to be started), and the installer's default
/// (<c>/usr/local/VideoFX</c>). The first one set wins, whether or not it holds an SDK, so a wrong
/// setting is reported rather than silently replaced by another SDK.
/// </summary>
public static class VfxSdkLocator
{
    public const string EnvironmentVariable = "FMP_VFX_SDK";

    /// <summary>The Video Frame Generation feature's directory name under <c>features/</c>.</summary>
    public const string FeatureName = "nvvfxvideoframegeneration";

    /// <summary>Where NVIDIA's installer puts the SDK on this platform.</summary>
    public static string SystemDefault => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVIDIA Video Effects")
        : "/usr/local/VideoFX";

    /// <param name="explicitDirectory">An SDK directory given explicitly (a command-line option); wins over everything.</param>
    /// <param name="settingsDirectory">The settings file's <c>generator.vfxSdkDirectory</c>, if set.</param>
    /// <param name="searchFrom">Another place to look for a repository checkout from, given by the caller (tests pass their checkout).</param>
    public static VfxSdkLocation Resolve(string? explicitDirectory = null, string? settingsDirectory = null, string? searchFrom = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return VfxSdkLocation.At(explicitDirectory, VfxSdkSource.Explicit);
        }

        var environment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment))
        {
            return VfxSdkLocation.At(environment, VfxSdkSource.Environment);
        }

        if (!string.IsNullOrWhiteSpace(settingsDirectory))
        {
            return VfxSdkLocation.At(settingsDirectory, VfxSdkSource.Settings);
        }

        return FindDevCheckout(searchFrom) is { } dev
            ? VfxSdkLocation.At(dev, VfxSdkSource.DevCheckout)
            : VfxSdkLocation.At(SystemDefault, VfxSdkSource.SystemDefault);
    }

    /// <summary>
    /// <c>lib/VideoFX</c> of the repository checkout (the directory with <c>FrameMotionPlus.slnx</c>) above the running
    /// program, or above <paramref name="searchFrom"/>, if it has one. The current directory is deliberately not searched.
    /// </summary>
    public static string? FindDevCheckout(string? searchFrom = null)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, searchFrom })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FrameMotionPlus.slnx")))
                {
                    var sdk = Path.Combine(dir.FullName, "lib", "VideoFX");
                    if (System.IO.Directory.Exists(sdk))
                    {
                        return sdk;
                    }

                    break;
                }
            }
        }

        return null;
    }
}
