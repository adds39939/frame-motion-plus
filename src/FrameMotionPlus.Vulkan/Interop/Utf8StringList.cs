using System.Runtime.InteropServices;
using System.Text;

namespace FrameMotionPlus.Vulkan.Interop;

/// <summary>A native <c>const char* const*</c> array of NUL-terminated UTF-8 strings, for create-info structs.</summary>
internal sealed unsafe class Utf8StringList : IDisposable
{
    private byte** _pointers;
    private readonly int _count;

    public Utf8StringList(IReadOnlyList<string> strings)
    {
        _count = strings.Count;
        _pointers = (byte**)NativeMemory.AllocZeroed((nuint)Math.Max(1, _count), (nuint)sizeof(byte*));
        for (var i = 0; i < _count; i++)
        {
            _pointers[i] = Allocate(strings[i]);
        }
    }

    public uint Count => (uint)_count;

    public byte** Pointer => _count == 0 ? null : _pointers;

    public static byte* Allocate(string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        var p = (byte*)NativeMemory.Alloc((nuint)length + 1);
        Encoding.UTF8.GetBytes(value, new Span<byte>(p, length));
        p[length] = 0;
        return p;
    }

    public void Dispose()
    {
        if (_pointers == null)
        {
            return;
        }

        for (var i = 0; i < _count; i++)
        {
            NativeMemory.Free(_pointers[i]);
        }

        NativeMemory.Free(_pointers);
        _pointers = null;
    }
}

internal static unsafe class Utf8
{
    /// <summary>Reads a NUL-terminated UTF-8 string from a fixed-size Vulkan char array.</summary>
    public static string FromFixed(byte* chars, int capacity)
    {
        var span = new ReadOnlySpan<byte>(chars, capacity);
        var end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    public static string? FromPointer(byte* chars) =>
        chars == null ? null : Marshal.PtrToStringUTF8((nint)chars);
}
