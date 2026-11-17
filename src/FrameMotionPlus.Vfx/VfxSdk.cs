using FrameMotionPlus.Vfx.Interop;

namespace FrameMotionPlus.Vfx;

/// <summary>
/// The NVIDIA VFX SDK in this process: found through <see cref="VfxSdkLocator"/>, loaded once from its directory (see
/// <see cref="VfxLibraries"/> for how, without <c>LD_LIBRARY_PATH</c>). A machine without the SDK simply reports it
/// unavailable with a reason; nothing here throws for that except <see cref="EnsureLoaded"/>.
/// </summary>
public static class VfxSdk
{
    /// <summary>True once the SDK has been loaded in this process.</summary>
    public static bool IsLoaded => VfxLibraries.IsLoaded;

    /// <summary>Where the SDK was loaded from (or the attempt that failed); null before any attempt that got that far.</summary>
    public static VfxSdkLocation? Location => VfxLibraries.Location;

    /// <summary>The SDK version, e.g. "1.3.0", once loaded.</summary>
    public static string? Version
    {
        get
        {
            if (!IsLoaded)
            {
                return null;
            }

            var v = VfxLibraries.RawVersion;
            return $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}";
        }
    }

    /// <summary>The libraries loaded, in order, by absolute path (or by soname when taken from the system).</summary>
    public static IReadOnlyList<string> LoadedLibraries => VfxLibraries.LoadedFiles;

    /// <summary>The SDK's latest log messages (warnings and errors), oldest first.</summary>
    public static IReadOnlyList<string> RecentLogMessages => VfxLog.Recent(16);

    /// <summary>
    /// Loads the SDK found by <see cref="VfxSdkLocator.Resolve"/> (explicit directory, <c>$FMP_VFX_SDK</c>, settings,
    /// dev checkout, installer default). False with a one-line reason when it is not there or cannot be loaded.
    /// </summary>
    public static bool TryLoad(out string? reason, string? explicitDirectory = null, string? settingsDirectory = null) =>
        TryLoad(VfxSdkLocator.Resolve(explicitDirectory, settingsDirectory), out reason);

    /// <summary>
    /// Loads the SDK at <paramref name="location"/>, once per process. A directory missing the SDK's files is refused
    /// without loading anything (another may be tried). Once loading has started the outcome is final for the
    /// process: a later call returns the same result, and a different directory is refused while one is loaded.
    /// </summary>
    public static bool TryLoad(VfxSdkLocation location, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(location);
        return VfxLibraries.TryLoad(location, out reason);
    }

    /// <summary>Files of <paramref name="location"/> that are missing (nothing is loaded); empty when it looks complete.</summary>
    public static IReadOnlyList<string> MissingFiles(VfxSdkLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return VfxLibraries.MissingFiles(location);
    }

    /// <summary>Throws <see cref="VfxUnavailableException"/> unless the SDK has been loaded.</summary>
    public static void EnsureLoaded()
    {
        if (!IsLoaded)
        {
            throw new VfxUnavailableException("The NVIDIA VFX SDK has not been loaded: call VfxSdk.TryLoad first.");
        }
    }
}
