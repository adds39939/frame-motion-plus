using System.Reflection;
using System.Runtime.InteropServices;

namespace FrameMotionPlus.Vfx.Interop;

/// <summary>
/// Loads the VFX SDK from a directory, without the user setting <c>LD_LIBRARY_PATH</c>.
/// <para>
/// <b>How the SDK finds its parts</b> (measured on SDK 1.3.0.0 with <c>LD_DEBUG=libs</c>): the dispatcher
/// <c>libVideoFX.so</c> opens <c>dlopen("libVideoFXLocal.so")</c> by bare name, and <c>libVideoFXLocal.so</c> opens the
/// feature as <c>dlopen("libnvVFXVideoFrameGeneration.so")</c>, also by bare name: neither looks next to itself or in
/// <c>features/</c> on Linux. Their dependencies (<c>libcudart.so.12</c>, <c>libnpp*.so.12</c>, <c>libcudnn.so.9</c>,
/// <c>libNVCVImage.so</c>) are plain <c>DT_NEEDED</c> sonames with no run path. A bare name is satisfied by an object
/// already loaded with that soname, so loading every file by absolute path, dependencies first, makes all those lookups
/// find what is already in the process. <c>LD_LIBRARY_PATH</c> could not be used instead: the loader reads it once, at
/// process start.
/// </para>
/// <para>
/// Order (Linux): the CUDA 12 runtime, NPP and cuDNN from <c>external/cuda/lib</c> (the copies NVIDIA ships with the
/// SDK; when that directory is absent each is loaded by soname from the system), then <c>lib/libNVCVImage.so</c>,
/// <c>lib/libVideoFXLocal.so</c>, <c>lib/libVideoFX.so</c> and the feature's <c>lib/libnvVFXVideoFrameGeneration.so</c>.
/// <c>libcuda.so.1</c> comes from the driver. Windows (<c>NVCVImage.dll</c>, <c>NVVideoEffects.dll</c>) is untested.
/// </para>
/// <para>
/// A directory whose SDK files are missing is refused before anything is loaded, so another can be tried. Once loading
/// has started the outcome is final for the process: libraries cannot be unloaded safely.
/// </para>
/// </summary>
internal static class VfxLibraries
{
    private static readonly Lock _gate = new();
    private static readonly List<string> _loaded = [];

    // Registered by the type's own initialisation, never under _gate: VfxNative's static constructor calls
    // EnsureResolver, and a load holding _gate calls into VfxNative, so taking _gate there could deadlock two threads.
    private static readonly bool _resolverRegistered = Register();
    private static int _published;
    private static bool _committed;
    private static nint _effects;
    private static nint _image;
    private static nint _local;
    private static string? _failure;
    private static VfxSdkLocation? _location;
    private static uint _version;

    /// <summary>True once the SDK is loaded and fully set up (version read, logger installed): published last.</summary>
    internal static bool IsLoaded => Volatile.Read(ref _published) != 0;

    internal static VfxSdkLocation? Location
    {
        get
        {
            lock (_gate)
            {
                return _location;
            }
        }
    }

    internal static uint RawVersion => _version;

    /// <summary>True when libVideoFXLocal.so was loaded on its own (Linux), so the logger can be configured there.</summary>
    internal static bool HasLocalBackend => _local != 0;

    internal static IReadOnlyList<string> LoadedFiles
    {
        get
        {
            lock (_gate)
            {
                return [.. _loaded];
            }
        }
    }

    /// <summary>The CUDA runtime libraries the SDK needs, in load order (they come with the SDK in external/cuda/lib).</summary>
    internal static readonly string[] RuntimeLibraries =
    [
        "libcudart.so.12", "libnppc.so.12", "libnppial.so.12", "libnppicc.so.12", "libnppidei.so.12", "libnppig.so.12", "libcudnn.so.9",
    ];

    internal static readonly string[] SdkLibraries = ["libNVCVImage.so", "libVideoFXLocal.so", "libVideoFX.so"];

    internal const string FeatureLibrary = "libnvVFXVideoFrameGeneration.so";

    /// <summary>Makes sure the resolver is registered (it is, by the type's initialisation; this only triggers that).</summary>
    internal static void EnsureResolver() => _ = _resolverRegistered;

    private static bool Register()
    {
        NativeLibrary.SetDllImportResolver(typeof(VfxLibraries).Assembly, Resolve);
        return true;
    }

    /// <summary>Files of <paramref name="location"/> that must exist before loading starts; empty when all do.</summary>
    internal static List<string> MissingFiles(VfxSdkLocation location)
    {
        var missing = new List<string>();
        foreach (var path in RequiredFiles(location))
        {
            if (!File.Exists(path))
            {
                missing.Add(path);
            }
        }

        return missing;
    }

    internal static IEnumerable<string> RequiredFiles(VfxSdkLocation location)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(location.Directory, "NVCVImage.dll");
            yield return Path.Combine(location.Directory, "NVVideoEffects.dll");
            yield break;
        }

        foreach (var name in SdkLibraries)
        {
            yield return Path.Combine(location.Directory, "lib", name);
        }

        yield return Path.Combine(location.FeatureDirectory, "lib", FeatureLibrary);
    }

    /// <summary>
    /// Loads the SDK from <paramref name="location"/> once per process. True when it is loaded (now or earlier from the
    /// same directory). False with a one-line <paramref name="reason"/> when it is not there or failed to load.
    /// </summary>
    internal static bool TryLoad(VfxSdkLocation location, out string? reason)
    {
        lock (_gate)
        {
            if (_committed)
            {
                if (IsLoaded)
                {
                    var same = string.Equals(_location!.Directory, location.Directory, StringComparison.Ordinal) &&
                               string.Equals(_location.FeatureDirectory, location.FeatureDirectory, StringComparison.Ordinal);
                    reason = same ? null : $"the VFX SDK is already loaded from {_location.Directory} in this process; it cannot be replaced by {location.Directory}";
                    return same;
                }

                reason = _failure;
                return false;
            }

            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            {
                reason = "the NVIDIA VFX SDK exists for Linux and Windows only";
                return false;
            }

            if (!Directory.Exists(location.Directory))
            {
                reason = $"VFX SDK directory {location} does not exist (set {VfxSdkLocator.EnvironmentVariable} or the SDK directory option)";
                return false;
            }

            var missing = MissingFiles(location);
            if (missing.Count > 0)
            {
                reason = $"VFX SDK at {location} is incomplete: {string.Join(", ", missing.Select(m => Path.GetRelativePath(location.Directory, m)))} not found" +
                         (missing.Any(m => m.EndsWith(FeatureLibrary, StringComparison.Ordinal))
                             ? $" (the Video Frame Generation feature belongs in {Path.Combine("features", VfxSdkLocator.FeatureName)})"
                             : string.Empty);
                return false;
            }

            // From here the outcome is final for the process.
            _committed = true;
            _location = location;
            try
            {
                LoadAll(location);
            }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException)
            {
                _failure = $"cannot load the VFX SDK from {location}: {OneLine(e.Message)}";
                _effects = 0;
                _image = 0;
                _local = 0;
                reason = _failure;
                return false;
            }

            reason = null;
            return true;
        }
    }

    private static unsafe void LoadAll(VfxSdkLocation location)
    {
        nint image;
        nint effects;
        if (OperatingSystem.IsWindows())
        {
            image = Load(Path.Combine(location.Directory, "NVCVImage.dll"));
            effects = Load(Path.Combine(location.Directory, "NVVideoEffects.dll"));
        }
        else
        {
            var runtime = Path.Combine(location.Directory, "external", "cuda", "lib");
            foreach (var name in RuntimeLibraries)
            {
                var bundled = Path.Combine(runtime, name);
                if (File.Exists(bundled))
                {
                    _ = Load(bundled);
                }
                else
                {
                    // Not shipped beside the SDK (a system-wide CUDA 12 runtime): by soname, from the system's path.
                    _ = NativeLibrary.Load(name, typeof(VfxLibraries).Assembly, DllImportSearchPath.SafeDirectories);
                    _loaded.Add(name + " (system)");
                }
            }

            image = Load(Path.Combine(location.Directory, "lib", "libNVCVImage.so"));
            _local = Load(Path.Combine(location.Directory, "lib", "libVideoFXLocal.so"));
            effects = Load(Path.Combine(location.Directory, "lib", "libVideoFX.so"));
            _ = Load(Path.Combine(location.FeatureDirectory, "lib", FeatureLibrary));
        }

        VfxFrameCalls.Resolve(effects, image);
        _image = image;
        // The resolver hands these out from now on; IsLoaded is published only once everything below has been set up.
        Volatile.Write(ref _effects, effects);

        uint version = 0;
        var status = VfxNative.NvVFX_GetVersion(&version);
        if (status != NvCVStatus.Success)
        {
            throw new InvalidOperationException($"NvVFX_GetVersion failed: {status}");
        }

        _version = version;
        VfxLog.Install();
        Volatile.Write(ref _published, 1);
    }

    private static nint Load(string path)
    {
        var handle = NativeLibrary.Load(path);
        _loaded.Add(path);
        return handle;
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) => libraryName switch
    {
        VfxNative.Effects => Volatile.Read(ref _effects),
        VfxNative.Image => Volatile.Read(ref _image),
        VfxNative.Local => Volatile.Read(ref _local),
        _ => 0,
    };

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();
}
