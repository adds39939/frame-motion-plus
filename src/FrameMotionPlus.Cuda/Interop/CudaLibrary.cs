using System.Reflection;
using System.Runtime.InteropServices;

namespace FrameMotionPlus.Cuda.Interop;

/// <summary>
/// Locates the CUDA driver library. Every binding uses the logical name <see cref="Name"/>, which a resolver maps to
/// the platform's file: the <c>libcuda.so.1</c> soname on Linux (plain <c>libcuda.so</c> is a development symlink
/// that driver-only and container installs lack) and <c>nvcuda.dll</c> on Windows.
/// </summary>
internal static class CudaLibrary
{
    internal const string Name = "cuda";

    private static readonly Lock _gate = new();
    private static bool _resolverRegistered;
    private static bool _loadAttempted;
    private static nint _handle;

    internal static string FileName => OperatingSystem.IsWindows() ? "nvcuda.dll" : "libcuda.so.1";

    internal static void EnsureResolver()
    {
        lock (_gate)
        {
            if (!_resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(typeof(CudaLibrary).Assembly, Resolve);
                _resolverRegistered = true;
            }
        }
    }

    /// <summary>Loads the driver library once; false when it is not installed.</summary>
    internal static bool TryLoad(out nint handle)
    {
        lock (_gate)
        {
            if (!_loadAttempted)
            {
                _loadAttempted = true;
                if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
                {
                    _ = NativeLibrary.TryLoad(FileName, typeof(CudaLibrary).Assembly, DllImportSearchPath.SafeDirectories, out _handle);
                }
            }

            handle = _handle;
            return handle != 0;
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == Name)
        {
            return TryLoad(out var handle) ? handle : 0;
        }

        // The C library (DescriptorHandoff): the main program's handle looks symbols up in the global scope, which
        // always contains the C library the process runs on, so no file name (glibc or musl) is assumed.
        return libraryName == DescriptorHandoff.Library && !OperatingSystem.IsWindows() ? NativeLibrary.GetMainProgramHandle() : 0;
    }
}
