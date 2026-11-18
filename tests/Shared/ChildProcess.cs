using System.Diagnostics;

namespace FrameMotionPlus.TestSupport;

/// <summary>
/// Out-of-process tests that send SIGINT: a child inherits its parent's signal dispositions, and a test run started as a
/// background job (`... &amp;` in a script) has SIGINT ignored, which the child then honours (a background job must not
/// react to the terminal's Ctrl+C). So the child is started with SIGINT back at its default (GNU env's
/// <c>--default-signal</c>), and where that is not possible while SIGINT is ignored here, the test is skipped with that
/// reason instead of failing.
/// </summary>
public static class ChildProcess
{
    private static readonly Lazy<bool> EnvResetsSignals = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("env") { ArgumentList = { "--default-signal=INT", "true" }, UseShellExecute = false, RedirectStandardError = true });
            p!.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    /// <summary>Why a SIGINT test cannot run here (null when it can).</summary>
    public static string? SigintUnavailable =>
        !OperatingSystem.IsLinux() ? "POSIX signals"
        : EnvResetsSignals.Value || !SigintIgnoredHere() ? null
        : "this test process has SIGINT ignored (started as a background job) and env --default-signal is not available to give the child its default back";

    /// <summary>A start info for <paramref name="file"/> with SIGINT at its default in the child.</summary>
    public static ProcessStartInfo WithDefaultSigint(string file, IEnumerable<string> args)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsLinux() && EnvResetsSignals.Value)
        {
            info = new ProcessStartInfo("env");
            info.ArgumentList.Add("--default-signal=INT");
            info.ArgumentList.Add(file);
        }
        else
        {
            info = new ProcessStartInfo(file);
        }

        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        return info;
    }

    private static bool SigintIgnoredHere()
    {
        foreach (var line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith("SigIgn:", StringComparison.Ordinal))
            {
                return (Convert.ToUInt64(line[7..].Trim(), 16) & 0x2) != 0;
            }
        }

        return false;
    }
}
