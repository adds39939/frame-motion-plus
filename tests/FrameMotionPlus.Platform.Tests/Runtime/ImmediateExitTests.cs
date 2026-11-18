using System.Runtime.InteropServices;
using FrameMotionPlus.Platform.Runtime;

namespace FrameMotionPlus.Platform.Tests.Runtime;

/// <summary>
/// The immediate exit (no exit handlers) is glibc's <c>_exit</c> on Linux. What it is for, a process with GPU work that will
/// never finish, is checked end to end out of process by the pipeline's CLI tests (<c>fmp play</c> with a never-finishing
/// GPU wait exits with its summary); here: the binding resolves, so the call cannot fail with a missing entry point at the
/// one moment it is needed.
/// </summary>
public class ImmediateExitTests
{
    [Fact]
    public void On_linux_it_is_bound_to_libcs_underscore_exit()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.False(ImmediateExit.SkipsExitHandlers);
            Assert.Skip("Environment.Exit elsewhere");
        }

        Assert.True(ImmediateExit.SkipsExitHandlers == Environment.Is64BitProcess);
        Assert.True(NativeLibrary.TryLoad("libc.so.6", out var libc));
        try
        {
            Assert.True(NativeLibrary.TryGetExport(libc, "_exit", out var entry));
            Assert.NotEqual(0, entry);
        }
        finally
        {
            NativeLibrary.Free(libc);
        }
    }
}
