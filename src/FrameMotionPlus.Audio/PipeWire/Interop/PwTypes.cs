using System.Runtime.InteropServices;

namespace FrameMotionPlus.Audio.PipeWire.Interop;

/// <summary>
/// The PipeWire and SPA constants this binding uses (pipewire-0.3 and spa-0.2 headers, PipeWire 1.6). Every value is
/// checked against the system headers by StructLayoutTests when they are installed.
/// </summary>
internal static class PwDefs
{
    public const int DirectionInput = 0;   // SPA_DIRECTION_INPUT: a capture stream
    public const int DirectionOutput = 1;  // SPA_DIRECTION_OUTPUT: a playback stream

    public const int StreamFlagAutoconnect = 1 << 0;
    public const int StreamFlagMapBuffers = 1 << 2;
    public const int StreamFlagRtProcess = 1 << 4;
    public const int StreamFlagDontReconnect = 1 << 7;

    public const int StreamStateError = -1;
    public const int StreamStateUnconnected = 0;
    public const int StreamStateConnecting = 1;
    public const int StreamStatePaused = 2;
    public const int StreamStateStreaming = 3;

    public const uint IdAny = 0xffffffff;
    public const uint IdCore = 0;

    public const uint VersionStreamEvents = 2;
    public const uint VersionCoreEvents = 1;
    public const uint VersionRegistryEvents = 0;
    public const uint VersionMetadataEvents = 0;
    public const uint VersionRegistry = 3;
    public const uint VersionMetadata = 3;
    public const uint VersionNode = 3;

    public const uint ParamEnumFormat = 3;      // SPA_PARAM_EnumFormat
    public const uint TypeId = 3;               // SPA_TYPE_Id
    public const uint TypeInt = 4;              // SPA_TYPE_Int
    public const uint TypeArray = 13;           // SPA_TYPE_Array
    public const uint TypeObject = 15;          // SPA_TYPE_Object
    public const uint TypeObjectFormat = 0x40003; // SPA_TYPE_OBJECT_Format
    public const uint FormatMediaType = 1;      // SPA_FORMAT_mediaType
    public const uint FormatMediaSubtype = 2;   // SPA_FORMAT_mediaSubtype
    public const uint FormatAudioFormat = 0x10001;   // SPA_FORMAT_AUDIO_format
    public const uint FormatAudioRate = 0x10003;     // SPA_FORMAT_AUDIO_rate
    public const uint FormatAudioChannels = 0x10004; // SPA_FORMAT_AUDIO_channels
    public const uint FormatAudioPosition = 0x10005; // SPA_FORMAT_AUDIO_position
    public const uint MediaTypeAudio = 1;       // SPA_MEDIA_TYPE_audio
    public const uint MediaSubtypeRaw = 1;      // SPA_MEDIA_SUBTYPE_raw
    public const uint AudioFormatF32 = 0x11b;   // SPA_AUDIO_FORMAT_F32 (F32_LE on little-endian)
    public const uint AudioChannelFl = 3;       // SPA_AUDIO_CHANNEL_FL
    public const uint AudioChannelFr = 4;       // SPA_AUDIO_CHANNEL_FR
}

/// <summary>The property keys and interface types used (PW_KEY_*, PW_TYPE_INTERFACE_*), checked against the headers.</summary>
internal static class PwKeys
{
    public const string MediaType = "media.type";
    public const string MediaCategory = "media.category";
    public const string MediaClass = "media.class";
    public const string MediaName = "media.name";
    public const string NodeName = "node.name";
    public const string NodeNick = "node.nick";
    public const string NodeDescription = "node.description";
    public const string NodeLatency = "node.latency";
    public const string NodeDontReconnect = "node.dont-reconnect";
    public const string NodeVirtual = "node.virtual";
    public const string TargetObject = "target.object";
    public const string StreamCaptureSink = "stream.capture.sink";
    public const string AppName = "application.name";
    public const string ObjectSerial = "object.serial";
    public const string LinkOutputNode = "link.output.node";
    public const string LinkInputNode = "link.input.node";
    public const string FactoryName = "factory.name";
    public const string PrioritySession = "priority.session";
    public const string PriorityDriver = "priority.driver";
    public const string AudioRate = "audio.rate";
    public const string AudioChannels = "audio.channels";

    public const string TypeNode = "PipeWire:Interface:Node";
    public const string TypeLink = "PipeWire:Interface:Link";
    public const string TypeMetadata = "PipeWire:Interface:Metadata";
}

/// <summary>struct spa_fraction.</summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct SpaFraction
{
    [FieldOffset(0)]
    public uint Num;

    [FieldOffset(4)]
    public uint Denom;
}

/// <summary>struct pw_time (pipewire/stream.h, since 1.1: with <c>size</c>).</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct PwTime
{
    [FieldOffset(0)]
    public long Now;

    [FieldOffset(8)]
    public SpaFraction Rate;

    [FieldOffset(16)]
    public ulong Ticks;

    [FieldOffset(24)]
    public long Delay;

    [FieldOffset(32)]
    public ulong Queued;

    [FieldOffset(40)]
    public ulong Buffered;

    [FieldOffset(48)]
    public uint QueuedBuffers;

    [FieldOffset(52)]
    public uint AvailBuffers;

    [FieldOffset(56)]
    public ulong Size;
}

/// <summary>struct pw_buffer.</summary>
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal unsafe struct PwBuffer
{
    [FieldOffset(0)]
    public SpaBuffer* Buffer;

    [FieldOffset(8)]
    public nint UserData;

    [FieldOffset(16)]
    public ulong Size;

    [FieldOffset(24)]
    public ulong Requested;

    [FieldOffset(32)]
    public ulong Time;
}

/// <summary>struct spa_buffer.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal unsafe struct SpaBuffer
{
    [FieldOffset(0)]
    public uint NMetas;

    [FieldOffset(4)]
    public uint NDatas;

    [FieldOffset(8)]
    public nint Metas;

    [FieldOffset(16)]
    public SpaData* Datas;
}

/// <summary>struct spa_data.</summary>
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal unsafe struct SpaData
{
    [FieldOffset(0)]
    public uint Type;

    [FieldOffset(4)]
    public uint Flags;

    [FieldOffset(8)]
    public long Fd;

    [FieldOffset(16)]
    public uint MapOffset;

    [FieldOffset(20)]
    public uint MaxSize;

    [FieldOffset(24)]
    public void* Data;

    [FieldOffset(32)]
    public SpaChunk* Chunk;
}

/// <summary>struct spa_chunk.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct SpaChunk
{
    [FieldOffset(0)]
    public uint Offset;

    [FieldOffset(4)]
    public uint Size;

    [FieldOffset(8)]
    public int Stride;

    [FieldOffset(12)]
    public int Flags;
}

/// <summary>struct spa_dict_item.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal unsafe struct SpaDictItem
{
    [FieldOffset(0)]
    public byte* Key;

    [FieldOffset(8)]
    public byte* Value;
}

/// <summary>struct spa_dict.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal unsafe struct SpaDict
{
    [FieldOffset(0)]
    public uint Flags;

    [FieldOffset(4)]
    public uint NItems;

    [FieldOffset(8)]
    public SpaDictItem* Items;
}

/// <summary>struct spa_hook: opaque to us (zeroed native memory the library links into its lists).</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct SpaHook
{
    [FieldOffset(0)]
    public nint LinkNext;
}

/// <summary>struct pw_stream_events (version 2): function pointers, unused ones null.</summary>
[StructLayout(LayoutKind.Explicit, Size = 96)]
internal struct PwStreamEvents
{
    [FieldOffset(0)]
    public uint Version;

    [FieldOffset(8)]
    public nint Destroy;

    [FieldOffset(16)]
    public nint StateChanged;

    [FieldOffset(24)]
    public nint ControlInfo;

    [FieldOffset(32)]
    public nint IoChanged;

    [FieldOffset(40)]
    public nint ParamChanged;

    [FieldOffset(48)]
    public nint AddBuffer;

    [FieldOffset(56)]
    public nint RemoveBuffer;

    [FieldOffset(64)]
    public nint Process;

    [FieldOffset(72)]
    public nint Drained;

    [FieldOffset(80)]
    public nint Command;

    [FieldOffset(88)]
    public nint TriggerDone;
}

/// <summary>struct pw_core_events (version 1).</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct PwCoreEvents
{
    [FieldOffset(0)]
    public uint Version;

    [FieldOffset(8)]
    public nint Info;

    [FieldOffset(16)]
    public nint Done;

    [FieldOffset(24)]
    public nint Ping;

    [FieldOffset(32)]
    public nint Error;

    [FieldOffset(40)]
    public nint RemoveId;

    [FieldOffset(48)]
    public nint BoundId;

    [FieldOffset(56)]
    public nint AddMem;

    [FieldOffset(64)]
    public nint RemoveMem;

    [FieldOffset(72)]
    public nint BoundProps;
}

/// <summary>struct pw_registry_events (version 0).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PwRegistryEvents
{
    [FieldOffset(0)]
    public uint Version;

    [FieldOffset(8)]
    public nint Global;

    [FieldOffset(16)]
    public nint GlobalRemove;
}

/// <summary>struct pw_metadata_events (version 0).</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct PwMetadataEvents
{
    [FieldOffset(0)]
    public uint Version;

    [FieldOffset(8)]
    public nint Property;
}
