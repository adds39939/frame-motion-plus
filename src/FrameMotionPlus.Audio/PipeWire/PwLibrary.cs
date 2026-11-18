using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using FrameMotionPlus.Audio.PipeWire.Interop;

namespace FrameMotionPlus.Audio.PipeWire;

/// <summary>
/// Whether libpipewire can be used in this process: 64-bit Linux, <c>libpipewire-0.3.so.0</c> present and exporting every
/// function the binding calls (checked before any call, so an old or missing library is a reason, never a crash), then
/// <c>pw_init</c> once. Says nothing about a server: <see cref="PwConnection"/> finds that out.
/// </summary>
internal static unsafe class PwLibrary
{
    private static readonly Lock Gate = new();
    private static bool _probed;
    private static string? _reason;
    private static string? _version;

    /// <summary>The library's version once loaded (e.g. "1.6.11").</summary>
    public static string? Version => _version;

    public static bool IsAvailable([NotNullWhen(false)] out string? reason)
    {
        lock (Gate)
        {
            if (!_probed)
            {
                _probed = true;
                _reason = Probe();
            }

            reason = _reason;
            return reason is null;
        }
    }

    private static string? Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "audio needs Linux (PipeWire)";
        }

        if (!Environment.Is64BitProcess)
        {
            return "audio needs a 64-bit process (the PipeWire structures are laid out for LP64)";
        }

        if (!NativeLibrary.TryLoad(PwNative.FileName, out var handle))
        {
            return $"{PwNative.FileName} was not found (PipeWire is not installed)";
        }

        foreach (var symbol in PwNative.Symbols)
        {
            if (!NativeLibrary.TryGetExport(handle, symbol, out _))
            {
                return $"{PwNative.FileName} does not export {symbol} (this PipeWire is too old: newer ones export it; 1.6 does)";
            }
        }

        PwNative.Init(null, null);
        _version = NativeText.ToManaged(PwNative.GetLibraryVersion());
        return null;
    }
}
