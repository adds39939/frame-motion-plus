using System.Runtime.CompilerServices;

namespace FrameMotionPlus.Tests.Shared;

/// <summary>
/// Every test process (and every fmp it starts) looks for saved settings in a directory of its own, never in the owner's
/// ~/.config/fmp: XDG_CONFIG_HOME (Linux) and APPDATA (Windows) point at an empty temporary directory before any test runs.
/// </summary>
internal static class IsolatedSettings
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fmp-test-config-{Environment.ProcessId}");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
        Environment.SetEnvironmentVariable("APPDATA", directory);
    }
}
