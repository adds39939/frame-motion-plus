using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FrameMotionPlus.Cuda.Interop;

/// <summary>
/// Hands a POSIX file descriptor to a CUDA import so that the documented ownership rule holds whatever the driver
/// does: the driver owns the descriptor only when the import succeeds, and the caller keeps it when it fails.
/// <para>
/// The CUDA documentation promises exactly that, but the driver does not keep it: a failed <c>cuImportExternalMemory</c>
/// closes the descriptor it was given (seen on 620.70 with descriptors that are not exported GPU memory), while a failed
/// <c>cuImportExternalSemaphore</c> leaves it open. A caller that closes "its" descriptor after a failure would then
/// close a number that may already belong to something else. So the driver is given a private duplicate, placed high
/// in the descriptor table where no ordinary open (which takes the lowest free number) lands. After a failure the
/// duplicate is closed if it is still open; after a success the caller's descriptor is closed, because from the
/// caller's side CUDA has taken it.
/// </para>
/// <para>
/// Callers hold <see cref="Gate"/> from <see cref="Duplicate"/> to <see cref="Discard"/> or <see cref="Consumed"/>: the
/// check-then-close in <see cref="Discard"/> is only safe while no other import can create a duplicate.
/// </para>
/// </summary>
internal static partial class DescriptorHandoff
{
    /// <summary>
    /// The C library, resolved by <see cref="CudaLibrary"/> to the process's global symbol scope, which contains it
    /// whatever its file name (glibc's <c>libc.so.6</c>, musl, ...).
    /// </summary>
    internal const string Library = "c";

    private const int FDupFdCloexec = 1030;
    private const int FGetFd = 1;
    private const int RLimitNoFile = 7;

    static DescriptorHandoff() => CudaLibrary.EnsureResolver();

    /// <summary>Serialises duplicate, import and discard across all imports in the process.</summary>
    internal static Lock Gate { get; } = new();

    /// <summary>A private duplicate of <paramref name="fd"/> for the driver. Throws (the caller still owns <paramref name="fd"/>).</summary>
    [SupportedOSPlatform("linux")]
    internal static int Duplicate(int fd)
    {
        var duplicate = Fcntl(fd, FDupFdCloexec, HighWaterMark());
        if (duplicate < 0)
        {
            throw new IOException($"Could not duplicate descriptor {fd} for the CUDA import (errno {Marshal.GetLastPInvokeError()}).");
        }

        return duplicate;
    }

    /// <summary>The import failed: closes the duplicate unless the driver already did.</summary>
    [SupportedOSPlatform("linux")]
    internal static void Discard(int duplicate)
    {
        if (Fcntl(duplicate, FGetFd, 0) >= 0)
        {
            _ = Close(duplicate);
        }
    }

    /// <summary>The import succeeded: the driver owns the duplicate, so the caller's descriptor goes.</summary>
    [SupportedOSPlatform("linux")]
    internal static void Consumed(int fd) => _ = Close(fd);

    /// <summary>Lowest number for duplicates: near the top of the soft limit, at most 4096 (the table grows to it).</summary>
    private static unsafe int HighWaterMark()
    {
        RLimit limit;
        if (GetRLimit(RLimitNoFile, &limit) != 0 || limit.Current < 256)
        {
            return 3;
        }

        return (int)Math.Min(limit.Current - 64, 4096);
    }

    // fcntl is variadic; the optional argument is passed pointer-sized, which reads correctly as the int it is.
    [LibraryImport(Library, EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int fd, int command, nint argument);

    [LibraryImport(Library, EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);

    [LibraryImport(Library, EntryPoint = "getrlimit", SetLastError = true)]
    private static unsafe partial int GetRLimit(int resource, RLimit* limit);

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current;
        public ulong Maximum;
    }
}
