using System.Runtime.InteropServices;

namespace FrameMotionPlus.Vulkan.Interop;

/// <summary>The few libc calls the presenter needs on Linux. Never called on other platforms.</summary>
internal static partial class Libc
{
    // The SONAME, not "libc": on glibc "libc.so" is a linker script that dlopen cannot load.
    private const string Library = "libc.so.6";

    [LibraryImport(Library, EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int SetEnv(string name, string value, int overwrite);

    [LibraryImport(Library, EntryPoint = "unsetenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int UnsetEnv(string name);

    [LibraryImport(Library, EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    [LibraryImport(Library, EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial byte* GetEnv(string name);
}

/// <summary>
/// Changes the native process environment. <see cref="Environment.SetEnvironmentVariable(string, string)"/> does not
/// do that on Unix (.NET keeps its own copy there), and the Vulkan loader reads the native environment with getenv.
/// </summary>
internal static class NativeEnvironment
{
    private static int _writes;

    /// <summary>Native environment writes so far (tests check that re-applying a policy writes nothing).</summary>
    public static int Writes => Volatile.Read(ref _writes);

    /// <summary>The value the native environment holds (what the Vulkan loader would read).</summary>
    public static unsafe string? Get(string name) =>
        OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name) : Utf8.FromPointer(Libc.GetEnv(name));

    public static void Set(string name, string? value)
    {
        Interlocked.Increment(ref _writes);
        if (OperatingSystem.IsWindows())
        {
            // On Windows the managed call is SetEnvironmentVariableW, which the loader's GetEnvironmentVariable sees.
            Environment.SetEnvironmentVariable(name, value);
            return;
        }

        var result = value is null ? Libc.UnsetEnv(name) : Libc.SetEnv(name, value, 1);
        if (result != 0)
        {
            throw new InvalidOperationException($"Could not change environment variable {name} (errno {Marshal.GetLastPInvokeError()}).");
        }

        // Keep the managed view consistent for code that reads it later in this process.
        Environment.SetEnvironmentVariable(name, value);
    }
}
