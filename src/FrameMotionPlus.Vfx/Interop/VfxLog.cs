using System.Runtime.InteropServices;

namespace FrameMotionPlus.Vfx.Interop;

/// <summary>
/// The SDK's own log (warnings and errors, through <c>NvVFX_ConfigureLogger</c>), kept in a small ring so a failed call
/// can say what the SDK said about it. A message allocates a string when it arrives; the SDK logs nothing on the
/// per-frame path when nothing is wrong (the zero-allocation tests would see it).
/// </summary>
internal static unsafe class VfxLog
{
    private const int Capacity = 64;

    private static readonly Lock _gate = new();
    private static readonly string[] _messages = new string[Capacity];
    private static long _count;
    private static bool _installed;

    /// <summary>Messages logged so far in the process.</summary>
    internal static long Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    internal static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        _ = VfxLibraries.HasLocalBackend
            ? VfxNative.NvVFX_ConfigureLoggerLocal(NvCVConstants.LogWarning, null, &OnMessage, null)
            : VfxNative.NvVFX_ConfigureLogger(NvCVConstants.LogWarning, null, &OnMessage, null);
    }

    /// <summary>The messages logged after <paramref name="mark"/> (a <see cref="Count"/> read earlier), oldest first.</summary>
    internal static List<string> Since(long mark)
    {
        lock (_gate)
        {
            var list = new List<string>();
            for (var i = Math.Max(mark, _count - Capacity); i < _count; i++)
            {
                list.Add(_messages[i % Capacity]);
            }

            return list;
        }
    }

    internal static List<string> Recent(int count) => Since(Math.Max(0, Count - count));

    /// <summary>Records a message (also called by tests).</summary>
    internal static void Add(string message)
    {
        lock (_gate)
        {
            _messages[_count % Capacity] = message;
            _count++;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnMessage(void* data, byte* message)
    {
        try
        {
            var text = Marshal.PtrToStringUTF8((nint)message);
            if (!string.IsNullOrWhiteSpace(text))
            {
                Add(text.TrimEnd());
            }
        }
        catch (Exception)
        {
            // Never let an exception unwind into the SDK: a lost log line is the lesser harm.
        }
    }
}
