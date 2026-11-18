using System.Runtime.InteropServices;
using System.Text;

namespace FrameMotionPlus.Audio.PipeWire.Interop;

/// <summary>NUL-terminated UTF-8 in native memory, and reading C strings without allocating where it matters.</summary>
internal static unsafe class NativeText
{
    /// <summary>A copy of <paramref name="text"/> in native memory; free with <see cref="NativeMemory.Free"/>.</summary>
    public static byte* Alloc(string text)
    {
        var count = Encoding.UTF8.GetByteCount(text);
        var p = (byte*)NativeMemory.Alloc((nuint)count + 1);
        _ = Encoding.UTF8.GetBytes(text, new Span<byte>(p, count));
        p[count] = 0;
        return p;
    }

    /// <summary>The C string as a managed string (allocates); null for a null pointer.</summary>
    public static string? ToManaged(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((nint)p);

    public static int Length(byte* p)
    {
        var n = 0;
        while (p[n] != 0)
        {
            n++;
        }

        return n;
    }

    /// <summary>Whether the C string equals the UTF-8 bytes (no allocation).</summary>
    public static bool Equals(byte* p, ReadOnlySpan<byte> utf8) => p != null && new ReadOnlySpan<byte>(p, Length(p)).SequenceEqual(utf8);

    /// <summary>A decimal unsigned value; false when it is not one (no allocation).</summary>
    public static bool TryParseUInt64(byte* p, out ulong value)
    {
        value = 0;
        if (p == null || *p == 0)
        {
            return false;
        }

        for (var i = 0; p[i] != 0; i++)
        {
            var c = p[i];
            if (c is < (byte)'0' or > (byte)'9' || value > (ulong.MaxValue - 9) / 10)
            {
                return false;
            }

            value = (value * 10) + (ulong)(c - '0');
        }

        return true;
    }

    /// <summary>The value of <paramref name="key"/> in a spa_dict, or null.</summary>
    public static byte* Lookup(SpaDict* dict, ReadOnlySpan<byte> key)
    {
        if (dict == null)
        {
            return null;
        }

        for (var i = 0u; i < dict->NItems; i++)
        {
            if (Equals(dict->Items[i].Key, key))
            {
                return dict->Items[i].Value;
            }
        }

        return null;
    }

    /// <summary>The "name" member of a metadata value such as <c>{"name":"alsa_output.x"}</c>, or null.</summary>
    public static string? JsonName(string? json)
    {
        if (json is null)
        {
            return null;
        }

        var key = json.IndexOf("\"name\"", StringComparison.Ordinal);
        if (key < 0)
        {
            return null;
        }

        var colon = json.IndexOf(':', key + 6);
        var open = colon < 0 ? -1 : json.IndexOf('"', colon + 1);
        var close = open < 0 ? -1 : json.IndexOf('"', open + 1);
        return close < 0 ? null : json[(open + 1)..close];
    }
}

/// <summary>
/// Properties for <c>pw_stream_new</c>, <c>pw_context_new</c> and friends, built as a spa_dict over native strings and
/// copied by <c>pw_properties_new_dict</c> (no quoting rules to get wrong).
/// </summary>
internal sealed unsafe class NativeDict : IDisposable
{
    private readonly List<nint> _strings = [];
    private SpaDictItem* _items;
    private SpaDict* _dict;

    public NativeDict(IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        _items = (SpaDictItem*)NativeMemory.AllocZeroed((nuint)Math.Max(1, entries.Count), (nuint)sizeof(SpaDictItem));
        for (var i = 0; i < entries.Count; i++)
        {
            var k = NativeText.Alloc(entries[i].Key);
            var v = NativeText.Alloc(entries[i].Value);
            _strings.Add((nint)k);
            _strings.Add((nint)v);
            _items[i].Key = k;
            _items[i].Value = v;
        }

        _dict = (SpaDict*)NativeMemory.AllocZeroed((nuint)sizeof(SpaDict));
        _dict->NItems = (uint)entries.Count;
        _dict->Items = _items;
    }

    public SpaDict* Dict => _dict;

    /// <summary>A new pw_properties with these entries (ownership passes to whatever it is given to).</summary>
    public nint ToProperties() => PwNative.PropertiesNewDict(_dict);

    public void Dispose()
    {
        foreach (var s in _strings)
        {
            NativeMemory.Free((void*)s);
        }

        _strings.Clear();
        NativeMemory.Free(_items);
        NativeMemory.Free(_dict);
        _items = null;
        _dict = null;
    }
}
