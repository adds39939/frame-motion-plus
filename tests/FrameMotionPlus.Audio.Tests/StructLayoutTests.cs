using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FrameMotionPlus.Audio.PipeWire.Interop;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Audio.Tests;

/// <summary>
/// Proves the hand-written PipeWire interop against the system's headers (/usr/include/pipewire-0.3 and spa-0.2): a C
/// program compiled with gcc prints the size of every structure, the offset and size of every field the C# declarations
/// name, every constant, every property key and interface type, and the bytes <c>spa_format_audio_raw_build</c> (an inline
/// in C) makes for F32 stereo at 48 kHz; each must equal the C# value, and the pod must equal <see cref="SpaFormatPod"/>'s
/// byte for byte. Reflection checks that every explicit-layout field and every constant is covered. Skips without gcc or
/// the headers (CI has neither PipeWire's development headers nor, on Windows, gcc).
/// </summary>
public class StructLayoutTests
{
    private const string Header = "/usr/include/pipewire-0.3/pipewire/stream.h";

    [Fact]
    public void Interop_sizes_offsets_constants_keys_and_the_format_pod_match_the_pipewire_headers()
    {
        var gcc = Tools.Which("gcc");
        if (gcc is null || OperatingSystem.IsWindows() || !File.Exists(Header) || !File.Exists("/usr/include/spa-0.2/spa/param/audio/format-utils.h"))
        {
            Assert.Skip($"needs gcc and the PipeWire development headers ({Header}; gcc: {gcc ?? "missing"})");
        }

        var checks = Checks();
        var strings = Strings();
        CheckCoverage();
        var source = new StringBuilder();
        source.AppendLine("#include <stdio.h>\n#include <stddef.h>\n#include <stdint.h>");
        source.AppendLine("#include <pipewire/pipewire.h>\n#include <pipewire/extensions/metadata.h>\n#include <spa/param/audio/format-utils.h>");
        source.AppendLine("int main(void)\n{");
        foreach (var (label, expression, _) in checks)
        {
            source.Append(CultureInfo.InvariantCulture, $"    printf(\"%s=%lld\\n\", \"{label}\", (long long)({expression}));\n");
        }

        foreach (var (label, expression, _) in strings)
        {
            source.Append(CultureInfo.InvariantCulture, $"    printf(\"%s=%s\\n\", \"{label}\", {expression});\n");
        }

        source.AppendLine("""
                uint8_t buffer[1024];
                struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
                struct spa_audio_info_raw info = { .format = SPA_AUDIO_FORMAT_F32, .rate = 48000, .channels = 2, .position = { SPA_AUDIO_CHANNEL_FL, SPA_AUDIO_CHANNEL_FR } };
                struct spa_pod *pod = spa_format_audio_raw_build(&b, SPA_PARAM_EnumFormat, &info);
                printf("pod=");
                for (uint32_t i = 0; i < SPA_POD_SIZE(pod); i++) printf("%02x", ((uint8_t *)pod)[i]);
                printf("\n");
                return 0;
            }
            """);
        var dir = Tools.TempDirectory("pipewire-layout");
        try
        {
            File.WriteAllText(Path.Combine(dir, "layout.c"), source.ToString());
            var (compileExit, compileOutput) = Tools.Run(gcc!,
                ["-std=gnu11", "-Wall", "-Werror", "-Wno-unused-function", "-I/usr/include/pipewire-0.3", "-I/usr/include/spa-0.2", "-D_REENTRANT", "-o", "layout", "layout.c"], dir);
            Assert.True(compileExit == 0, compileOutput);
            var (runExit, output) = Tools.Run(Path.Combine(dir, "layout"), [], dir);
            Assert.Equal(0, runExit);
            var fromC = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            var mismatches = checks.Where(c => long.Parse(fromC[c.Label], CultureInfo.InvariantCulture) != c.CSharp)
                .Select(c => $"{c.Label}: C {fromC[c.Label]}, C# {c.CSharp}")
                .Concat(strings.Where(s => fromC[s.Label] != s.CSharp).Select(s => $"{s.Label}: C \"{fromC[s.Label]}\", C# \"{s.CSharp}\""))
                .ToList();
            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
            Assert.Equal(fromC["pod"], Convert.ToHexStringLower(SpaFormatPod.AudioRawF32(48_000)));
            Assert.True(checks.Count > 90, $"only {checks.Count} checks");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static readonly HashSet<(Type, string)> Covered = [];

    private static unsafe List<(string Label, string C, long CSharp)> Checks()
    {
        var list = new List<(string, string, long)>();

        void Struct<T>(string c, params (string CField, string CsField)[] fields)
            where T : unmanaged
        {
            list.Add(($"sizeof {c}", $"sizeof(struct {c})", sizeof(T)));
            foreach (var (cField, csField) in fields)
            {
                Covered.Add((typeof(T), csField));
                var field = typeof(T).GetField(csField, BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"{typeof(T).Name} has no field {csField}");
                var offset = field.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
                list.Add(($"{c}.{cField} offset", $"offsetof(struct {c}, {cField})", offset));
                list.Add(($"{c}.{cField} size", $"sizeof(((struct {c}*)0)->{cField})", Marshal.SizeOf(field.FieldType == typeof(nint) || field.FieldType.IsPointer ? typeof(nint) : field.FieldType)));
            }
        }

        Struct<SpaFraction>("spa_fraction", ("num", nameof(SpaFraction.Num)), ("denom", nameof(SpaFraction.Denom)));
        Struct<PwTime>("pw_time", ("now", nameof(PwTime.Now)), ("rate", nameof(PwTime.Rate)), ("ticks", nameof(PwTime.Ticks)), ("delay", nameof(PwTime.Delay)),
            ("queued", nameof(PwTime.Queued)), ("buffered", nameof(PwTime.Buffered)), ("queued_buffers", nameof(PwTime.QueuedBuffers)),
            ("avail_buffers", nameof(PwTime.AvailBuffers)), ("size", nameof(PwTime.Size)));
        Struct<PwBuffer>("pw_buffer", ("buffer", nameof(PwBuffer.Buffer)), ("user_data", nameof(PwBuffer.UserData)), ("size", nameof(PwBuffer.Size)),
            ("requested", nameof(PwBuffer.Requested)), ("time", nameof(PwBuffer.Time)));
        Struct<SpaBuffer>("spa_buffer", ("n_metas", nameof(SpaBuffer.NMetas)), ("n_datas", nameof(SpaBuffer.NDatas)), ("metas", nameof(SpaBuffer.Metas)),
            ("datas", nameof(SpaBuffer.Datas)));
        Struct<SpaData>("spa_data", ("type", nameof(SpaData.Type)), ("flags", nameof(SpaData.Flags)), ("fd", nameof(SpaData.Fd)),
            ("mapoffset", nameof(SpaData.MapOffset)), ("maxsize", nameof(SpaData.MaxSize)), ("data", nameof(SpaData.Data)), ("chunk", nameof(SpaData.Chunk)));
        Struct<SpaChunk>("spa_chunk", ("offset", nameof(SpaChunk.Offset)), ("size", nameof(SpaChunk.Size)), ("stride", nameof(SpaChunk.Stride)),
            ("flags", nameof(SpaChunk.Flags)));
        Struct<SpaDictItem>("spa_dict_item", ("key", nameof(SpaDictItem.Key)), ("value", nameof(SpaDictItem.Value)));
        Struct<SpaDict>("spa_dict", ("flags", nameof(SpaDict.Flags)), ("n_items", nameof(SpaDict.NItems)), ("items", nameof(SpaDict.Items)));
        Struct<SpaHook>("spa_hook", ("link.next", nameof(SpaHook.LinkNext)));
        Struct<PwStreamEvents>("pw_stream_events", ("version", nameof(PwStreamEvents.Version)), ("destroy", nameof(PwStreamEvents.Destroy)),
            ("state_changed", nameof(PwStreamEvents.StateChanged)), ("control_info", nameof(PwStreamEvents.ControlInfo)),
            ("io_changed", nameof(PwStreamEvents.IoChanged)), ("param_changed", nameof(PwStreamEvents.ParamChanged)),
            ("add_buffer", nameof(PwStreamEvents.AddBuffer)), ("remove_buffer", nameof(PwStreamEvents.RemoveBuffer)),
            ("process", nameof(PwStreamEvents.Process)), ("drained", nameof(PwStreamEvents.Drained)), ("command", nameof(PwStreamEvents.Command)),
            ("trigger_done", nameof(PwStreamEvents.TriggerDone)));
        Struct<PwCoreEvents>("pw_core_events", ("version", nameof(PwCoreEvents.Version)), ("info", nameof(PwCoreEvents.Info)), ("done", nameof(PwCoreEvents.Done)),
            ("ping", nameof(PwCoreEvents.Ping)), ("error", nameof(PwCoreEvents.Error)), ("remove_id", nameof(PwCoreEvents.RemoveId)),
            ("bound_id", nameof(PwCoreEvents.BoundId)), ("add_mem", nameof(PwCoreEvents.AddMem)), ("remove_mem", nameof(PwCoreEvents.RemoveMem)),
            ("bound_props", nameof(PwCoreEvents.BoundProps)));
        Struct<PwRegistryEvents>("pw_registry_events", ("version", nameof(PwRegistryEvents.Version)), ("global", nameof(PwRegistryEvents.Global)),
            ("global_remove", nameof(PwRegistryEvents.GlobalRemove)));
        Struct<PwMetadataEvents>("pw_metadata_events", ("version", nameof(PwMetadataEvents.Version)), ("property", nameof(PwMetadataEvents.Property)));

        foreach (var f in typeof(PwDefs).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            list.Add(($"PwDefs.{f.Name}", ConstantCName(f.Name), Convert.ToInt64(f.GetRawConstantValue(), CultureInfo.InvariantCulture)));
        }

        return list;
    }

    private static List<(string Label, string C, string CSharp)> Strings()
    {
        var list = new List<(string, string, string)>();
        foreach (var f in typeof(PwKeys).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            list.Add(($"PwKeys.{f.Name}", KeyCName(f.Name), (string)f.GetRawConstantValue()!));
        }

        return list;
    }

    private static void CheckCoverage()
    {
        var structs = typeof(PwDefs).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(PwDefs).Namespace && t.IsValueType && t.StructLayoutAttribute?.Value == LayoutKind.Explicit);
        var missing = structs.SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !Covered.Contains((t, f.Name))).Select(f => $"{t.Name}.{f.Name}")).ToList();
        Assert.True(missing.Count == 0, "fields without a layout check: " + string.Join(", ", missing));
    }

    private static string ConstantCName(string name) => name switch
    {
        "DirectionInput" => "PW_DIRECTION_INPUT",
        "DirectionOutput" => "PW_DIRECTION_OUTPUT",
        "StreamFlagAutoconnect" => "PW_STREAM_FLAG_AUTOCONNECT",
        "StreamFlagMapBuffers" => "PW_STREAM_FLAG_MAP_BUFFERS",
        "StreamFlagRtProcess" => "PW_STREAM_FLAG_RT_PROCESS",
        "StreamFlagDontReconnect" => "PW_STREAM_FLAG_DONT_RECONNECT",
        "StreamStateError" => "PW_STREAM_STATE_ERROR",
        "StreamStateUnconnected" => "PW_STREAM_STATE_UNCONNECTED",
        "StreamStateConnecting" => "PW_STREAM_STATE_CONNECTING",
        "StreamStatePaused" => "PW_STREAM_STATE_PAUSED",
        "StreamStateStreaming" => "PW_STREAM_STATE_STREAMING",
        "IdAny" => "PW_ID_ANY",
        "IdCore" => "PW_ID_CORE",
        "VersionStreamEvents" => "PW_VERSION_STREAM_EVENTS",
        "VersionCoreEvents" => "PW_VERSION_CORE_EVENTS",
        "VersionRegistryEvents" => "PW_VERSION_REGISTRY_EVENTS",
        "VersionMetadataEvents" => "PW_VERSION_METADATA_EVENTS",
        "VersionRegistry" => "PW_VERSION_REGISTRY",
        "VersionMetadata" => "PW_VERSION_METADATA",
        "VersionNode" => "PW_VERSION_NODE",
        "ParamEnumFormat" => "SPA_PARAM_EnumFormat",
        "TypeId" => "SPA_TYPE_Id",
        "TypeInt" => "SPA_TYPE_Int",
        "TypeArray" => "SPA_TYPE_Array",
        "TypeObject" => "SPA_TYPE_Object",
        "TypeObjectFormat" => "SPA_TYPE_OBJECT_Format",
        "FormatMediaType" => "SPA_FORMAT_mediaType",
        "FormatMediaSubtype" => "SPA_FORMAT_mediaSubtype",
        "FormatAudioFormat" => "SPA_FORMAT_AUDIO_format",
        "FormatAudioRate" => "SPA_FORMAT_AUDIO_rate",
        "FormatAudioChannels" => "SPA_FORMAT_AUDIO_channels",
        "FormatAudioPosition" => "SPA_FORMAT_AUDIO_position",
        "MediaTypeAudio" => "SPA_MEDIA_TYPE_audio",
        "MediaSubtypeRaw" => "SPA_MEDIA_SUBTYPE_raw",
        "AudioFormatF32" => "SPA_AUDIO_FORMAT_F32",
        "AudioChannelFl" => "SPA_AUDIO_CHANNEL_FL",
        "AudioChannelFr" => "SPA_AUDIO_CHANNEL_FR",
        _ => throw new InvalidOperationException($"no C name for PwDefs.{name}: add it to the table"),
    };

    private static string KeyCName(string name) => name switch
    {
        "MediaType" => "PW_KEY_MEDIA_TYPE",
        "MediaCategory" => "PW_KEY_MEDIA_CATEGORY",
        "MediaClass" => "PW_KEY_MEDIA_CLASS",
        "MediaName" => "PW_KEY_MEDIA_NAME",
        "NodeName" => "PW_KEY_NODE_NAME",
        "NodeNick" => "PW_KEY_NODE_NICK",
        "NodeDescription" => "PW_KEY_NODE_DESCRIPTION",
        "NodeLatency" => "PW_KEY_NODE_LATENCY",
        "NodeDontReconnect" => "PW_KEY_NODE_DONT_RECONNECT",
        "NodeVirtual" => "PW_KEY_NODE_VIRTUAL",
        "TargetObject" => "PW_KEY_TARGET_OBJECT",
        "StreamCaptureSink" => "PW_KEY_STREAM_CAPTURE_SINK",
        "AppName" => "PW_KEY_APP_NAME",
        "ObjectSerial" => "PW_KEY_OBJECT_SERIAL",
        "LinkOutputNode" => "PW_KEY_LINK_OUTPUT_NODE",
        "LinkInputNode" => "PW_KEY_LINK_INPUT_NODE",
        "FactoryName" => "PW_KEY_FACTORY_NAME",
        "PrioritySession" => "PW_KEY_PRIORITY_SESSION",
        "PriorityDriver" => "PW_KEY_PRIORITY_DRIVER",
        "AudioRate" => "PW_KEY_AUDIO_RATE",
        "AudioChannels" => "PW_KEY_AUDIO_CHANNELS",
        "TypeNode" => "PW_TYPE_INTERFACE_Node",
        "TypeLink" => "PW_TYPE_INTERFACE_Link",
        "TypeMetadata" => "PW_TYPE_INTERFACE_Metadata",
        _ => throw new InvalidOperationException($"no C name for PwKeys.{name}: add it to the table"),
    };
}
