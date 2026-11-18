using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FrameMotionPlus.Sources.V4l2.Interop;

/// <summary>
/// The glibc calls a V4L2 device needs. Only called on 64-bit Linux (the source reports itself unavailable elsewhere).
/// <c>ioctl</c> is variadic in C; a pointer argument is passed exactly like a fixed one on x86-64 and AArch64 Linux. Every
/// call that can fail sets errno, read with <see cref="Marshal.GetLastPInvokeError"/> (the generated stub saves it without
/// allocating). The constants are checked against the system headers by StructLayoutTests.
/// </summary>
internal static unsafe partial class LibC
{
    private const string Library = "libc";

    public const int ORdWr = 0x0002;
    public const int ONonBlock = 0x0800;
    public const int OCloExec = 0x80000;

    public const short PollIn = 0x0001;
    public const short PollPri = 0x0002;
    public const short PollErr = 0x0008;
    public const short PollHup = 0x0010;
    public const short PollNval = 0x0020;

    public const int ProtRead = 0x1;
    public const int ProtWrite = 0x2;
    public const int MapShared = 0x01;
    public const int MapPrivate = 0x02;
    public const int MapAnonymous = 0x20;

    public const int EfdCloExec = 0x80000;
    public const int EfdNonBlock = 0x0800;

    public const int Enoent = 2;
    public const int Eintr = 4;
    public const int Eio = 5;
    public const int Enxio = 6;
    public const int Eagain = 11;
    public const int Enomem = 12;
    public const int Eacces = 13;
    public const int Efault = 14;
    public const int Ebusy = 16;
    public const int Enodev = 19;
    public const int Einval = 22;
    public const int Enotty = 25;
    public const int Enospc = 28;
    public const int Epipe = 32;
    public const int Enolink = 67;

    public static readonly nint MapFailed = -1;

    [SupportedOSPlatform("linux")]
    [LibraryImport(Library, EntryPoint = "open", SetLastError = true)]
    public static partial int Open(byte* path, int flags);

    [SupportedOSPlatform("linux")]
    [LibraryImport(Library, EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [SupportedOSPlatform("linux")]
    [LibraryImport(Library, EntryPoint = "ioctl", SetLastError = true)]
    public static partial int Ioctl(int fd, nuint request, void* argument);

    [SupportedOSPlatform("linux")]
    [LibraryImport(Library, EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(PollFd* fds, nuint count, int timeoutMs);

    [SupportedOSPlatform("linux")]
    [LibraryImport(Library, EntryPoint = "mmap", SetLastError = true)]
    public static partial nint Mmap(void* address, nuint length, int protection, int flags, int fd, long offset);

    [SupportedOSPlatform("linux")]
    [LibraryImport(Library, EntryPoint = "munmap", SetLastError = true)]
    public static partial int Munmap(void* address, nuint length);

    [SupportedOSPlatform("linux")]
    [LibraryImport(Library, EntryPoint = "eventfd", SetLastError = true)]
    public static partial int EventFd(uint initial, int flags);

    [SupportedOSPlatform("linux")]
    [LibraryImport(Library, EntryPoint = "read", SetLastError = true)]
    public static partial nint Read(int fd, void* buffer, nuint count);

    [SupportedOSPlatform("linux")]
    [LibraryImport(Library, EntryPoint = "write", SetLastError = true)]
    public static partial nint Write(int fd, void* buffer, nuint count);

    /// <summary>errno's name for the messages (a handful are named; the rest by number).</summary>
    public static string ErrorName(int errno) => errno switch
    {
        0 => "no error",
        Enoent => "ENOENT (no such file or directory)",
        Eintr => "EINTR",
        Eio => "EIO (input/output error)",
        Enxio => "ENXIO (no such device or address)",
        Eagain => "EAGAIN",
        Enomem => "ENOMEM",
        Eacces => "EACCES (permission denied)",
        Efault => "EFAULT (bad address)",
        Ebusy => "EBUSY (device or resource busy)",
        Enodev => "ENODEV (no such device)",
        Einval => "EINVAL (invalid argument)",
        Enotty => "ENOTTY (not a V4L2 device, or the request is not supported)",
        Enospc => "ENOSPC (no space left: USB bandwidth)",
        Epipe => "EPIPE (broken pipe)",
        Enolink => "ENOLINK (no link: no signal)",
        _ => "errno " + errno.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
