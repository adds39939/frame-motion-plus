using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FrameMotionPlus.Platform.Tests.Support;

/// <summary>Helpers for Linux-specific assertions, read independently of the code under test.</summary>
internal static partial class TestPlatform
{
    public const int Sigwinch = 28;

    public static bool IsLinux64 => OperatingSystem.IsLinux() && Environment.Is64BitProcess;

    public static void SkipUnlessLinux() => Assert.SkipUnless(IsLinux64, "Linux-specific (64-bit Linux only)");

    /// <summary>A field of /proc/self/task/&lt;tid&gt;/stat by its man-page number (19 nice, 40 rt_priority, 41 policy).</summary>
    public static long ThreadStatField(int tid, int field)
    {
        var text = File.ReadAllText($"/proc/self/task/{tid}/stat");
        var afterComm = text[(text.LastIndexOf(')') + 2)..];
        var fields = afterComm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return long.Parse(fields[field - 3], CultureInfo.InvariantCulture);
    }

    public static string ThreadComm(int tid) => File.ReadAllText($"/proc/self/task/{tid}/comm").TrimEnd('\n');

    [SupportedOSPlatform("linux")]
    [LibraryImport("libc", EntryPoint = "tgkill", SetLastError = true)]
    public static partial int TgKill(int tgid, int tid, int signal);

    [SupportedOSPlatform("linux")]
    [LibraryImport("libc", EntryPoint = "getpid")]
    public static partial int GetPid();
}
