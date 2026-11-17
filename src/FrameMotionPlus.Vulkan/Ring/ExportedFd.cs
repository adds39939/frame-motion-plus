using System.Globalization;
using FrameMotionPlus.Vulkan.Interop;

namespace FrameMotionPlus.Vulkan;

/// <summary>
/// One exported POSIX file descriptor (opaque FD handle type) and who owns it.
/// <para>
/// Every export call creates a new descriptor, owned by this object. Importing an opaque FD into another API
/// (<c>cuImportExternalMemory</c>, <c>cuImportExternalSemaphore</c>, <c>vkAllocateMemory</c> with
/// <c>VkImportMemoryFdInfoKHR</c>, <c>vkImportSemaphoreFdKHR</c>) transfers ownership to that API <em>only when the
/// import succeeds</em>; the importer then closes it when its imported object is destroyed, and nobody may close it
/// here. So: pass <see cref="Fd"/> to the import, and on success call <see cref="MarkTransferred"/>. On failure, or
/// if it is never imported, <see cref="Dispose"/> closes it.
/// </para>
/// <para>
/// The exported payload is reference counted by the driver: the Vulkan memory or semaphore and every import keep it
/// alive independently, so destroying the ring while CUDA still holds an import is safe for the memory itself.
/// </para>
/// </summary>
public sealed class ExportedFd : IDisposable
{
    private int _fd;

    internal ExportedFd(int fd) => _fd = fd;

    /// <summary>The descriptor, or -1 once ownership has moved or it was closed.</summary>
    public int Fd => _fd;

    public bool IsOwned => _fd >= 0;

    /// <summary>An importer has taken ownership (its import call succeeded); this object will not close it.</summary>
    public void MarkTransferred()
    {
        ObjectDisposedException.ThrowIf(_fd < 0, this);
        _fd = -1;
    }

    /// <summary>Hands the descriptor to the caller, who now owns it (and must close it if an import fails).</summary>
    public int Take()
    {
        ObjectDisposedException.ThrowIf(_fd < 0, this);
        var fd = _fd;
        _fd = -1;
        return fd;
    }

    public void Dispose()
    {
        var fd = _fd;
        _fd = -1;
        if (fd >= 0 && !OperatingSystem.IsWindows())
        {
            _ = Libc.Close(fd);
        }
    }

    public override string ToString() => _fd >= 0 ? _fd.ToString(CultureInfo.InvariantCulture) : "(transferred)";
}
