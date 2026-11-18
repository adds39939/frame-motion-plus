using System.Buffers.Binary;

namespace FrameMotionPlus.Audio.PipeWire.Interop;

/// <summary>
/// The one SPA pod the streams need: an EnumFormat object for raw audio (F32, 48 kHz, stereo FL/FR), byte for byte what
/// <c>spa_format_audio_raw_build</c> (spa/param/audio/raw-utils.h, a macro-built inline in C) produces for the same
/// <c>spa_audio_info_raw</c>. A pod is a header (u32 size of the body, u32 type) and a body padded to 8 bytes; an object's
/// body is its object type and id followed by properties (u32 key, u32 flags, a pod). StructLayoutTests compiles the C
/// builder and compares the bytes.
/// </summary>
internal static class SpaFormatPod
{
    /// <summary>The pod for stereo (FL, FR) at <paramref name="rate"/> Hz: 168 bytes.</summary>
    public static byte[] AudioRawF32(int rate)
    {
        uint[] positions = [PwDefs.AudioChannelFl, PwDefs.AudioChannelFr];
        var w = new Writer(256);
        w.U32(0); // object body size, patched below
        w.U32(PwDefs.TypeObject);
        w.U32(PwDefs.TypeObjectFormat);
        w.U32(PwDefs.ParamEnumFormat);
        w.PropId(PwDefs.FormatMediaType, PwDefs.MediaTypeAudio);
        w.PropId(PwDefs.FormatMediaSubtype, PwDefs.MediaSubtypeRaw);
        w.PropId(PwDefs.FormatAudioFormat, PwDefs.AudioFormatF32);
        w.PropInt(PwDefs.FormatAudioRate, rate);
        w.PropInt(PwDefs.FormatAudioChannels, positions.Length);
        w.PropIdArray(PwDefs.FormatAudioPosition, positions);
        var bytes = w.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)(bytes.Length - 8));
        return bytes;
    }

    private sealed class Writer(int capacity)
    {
        private readonly byte[] _buffer = new byte[capacity];
        private int _length;

        public void U32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
            _length += 4;
        }

        public void PropId(uint key, uint id)
        {
            U32(key);
            U32(0);
            U32(4);
            U32(PwDefs.TypeId);
            U32(id);
            U32(0); // padding to 8
        }

        public void PropInt(uint key, int value)
        {
            U32(key);
            U32(0);
            U32(4);
            U32(PwDefs.TypeInt);
            U32((uint)value);
            U32(0);
        }

        public void PropIdArray(uint key, uint[] ids)
        {
            U32(key);
            U32(0);
            var body = 8 + (4 * ids.Length);
            U32((uint)body);
            U32(PwDefs.TypeArray);
            U32(4);           // child pod: size of one element
            U32(PwDefs.TypeId);
            foreach (var id in ids)
            {
                U32(id);
            }

            while ((_length & 7) != 0)
            {
                _buffer[_length++] = 0;
            }
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();
    }
}
