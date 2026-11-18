using FrameMotionPlus.Platform.Interop;

namespace FrameMotionPlus.Platform.Runtime;

/// <summary>
/// Ends the process at once, without running exit handlers: for a process that still has GPU work queued that will never
/// finish, where the driver's own clean-up at a normal exit (and the runtime's shutdown) would wait for it for good. The
/// caller has reported and flushed what it needed to; the kernel reclaims the GPU. Linux: <c>_exit(2)</c>; elsewhere
/// <see cref="Environment.Exit"/>.
/// </summary>
public static class ImmediateExit
{
    /// <summary>True when <see cref="Now"/> skips the exit handlers (Linux).</summary>
    public static bool SkipsExitHandlers => NativePlatform.IsLinux64;

    public static void Now(int exitCode)
    {
        if (NativePlatform.IsLinux64)
        {
            LibC.ExitImmediately(exitCode);
        }

        Environment.Exit(exitCode);
    }
}
