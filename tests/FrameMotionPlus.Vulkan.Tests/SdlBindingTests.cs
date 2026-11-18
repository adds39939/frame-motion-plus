using System.Reflection;
using System.Runtime.InteropServices;
using FrameMotionPlus.Vulkan.Windowing;

namespace FrameMotionPlus.Vulkan.Tests;

/// <summary>
/// Every SDL3 entry point the window binds exists in the system library (a misspelt one would only fail when first
/// called, e.g. fullscreen, which no automated test switches on because it would cover the owner's screen). Needs SDL3
/// only, no display.
/// </summary>
public class SdlBindingTests
{
    [Fact]
    public void Every_bound_sdl_entry_point_is_exported()
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libSDL3.so.0", out var library))
        {
            Assert.Skip("SDL3 (libSDL3.so.0) is not installed");
            return;
        }

        var entryPoints = typeof(SdlNative)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<LibraryImportAttribute>()?.EntryPoint)
            .OfType<string>()
            .ToArray();
        Assert.Contains("SDL_SetWindowFullscreen", entryPoints);
        Assert.True(entryPoints.Length >= 20, $"only {entryPoints.Length} entry points found");
        var missing = entryPoints.Where(e => !NativeLibrary.TryGetExport(library, e, out _)).ToArray();
        Assert.True(missing.Length == 0, $"not exported by libSDL3.so.0: {string.Join(", ", missing)}");
    }
}
