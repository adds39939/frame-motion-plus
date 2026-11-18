using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FrameMotionPlus.Cuda.Tests.Support;
using FrameMotionPlus.Sources.V4l2.Interop;

namespace FrameMotionPlus.Sources.V4l2.Tests;

/// <summary>
/// Proves the hand-written V4L2 interop against the system's kernel headers (/usr/include/linux/videodev2.h, poll.h,
/// fcntl.h, sys/mman.h, sys/eventfd.h, errno.h): a C program compiled with gcc prints the size of every structure, the
/// offset and size of every field the C# declarations name, every constant and every ioctl request number, and each must
/// equal the C# value. Reflection checks that the table covers every <see cref="FieldOffsetAttribute"/> field, every
/// constant of <see cref="V4l2Defs"/> and <see cref="LibC"/> and every ioctl number. Skips without gcc or the header
/// (CI on Windows, a machine without kernel headers).
/// </summary>
public class StructLayoutTests
{
    private const string Header = "/usr/include/linux/videodev2.h";

    [Fact]
    public void Interop_sizes_offsets_constants_and_ioctl_numbers_match_the_kernel_headers()
    {
        var gcc = Tools.Which("gcc");
        if (gcc is null || OperatingSystem.IsWindows() || !System.IO.File.Exists(Header))
        {
            Assert.Skip($"needs gcc and {Header} (gcc: {gcc ?? "missing"})");
        }

        var checks = Checks();
        CheckCoverage(checks);
        var source = new StringBuilder();
        source.AppendLine("#define _GNU_SOURCE");
        source.AppendLine("#include <stdio.h>\n#include <stddef.h>\n#include <errno.h>\n#include <fcntl.h>\n#include <poll.h>");
        source.AppendLine("#include <sys/mman.h>\n#include <sys/eventfd.h>\n#include <sys/ioctl.h>\n#include <linux/videodev2.h>");
        source.AppendLine("int main(void)\n{");
        foreach (var (label, expression, _) in checks)
        {
            source.Append(CultureInfo.InvariantCulture, $"    printf(\"%s=%lld\\n\", \"{label}\", (long long)({expression}));\n");
        }

        source.AppendLine("    return 0;\n}");
        var dir = Tools.TempDirectory("v4l2-layout");
        try
        {
            System.IO.File.WriteAllText(Path.Combine(dir, "layout.c"), source.ToString());
            var (compileExit, compileOutput) = Tools.Run(gcc!, ["-std=gnu11", "-Wall", "-Werror", "-o", "layout", "layout.c"], dir);
            Assert.True(compileExit == 0, compileOutput);
            var (runExit, output) = Tools.Run(Path.Combine(dir, "layout"), [], dir);
            Assert.Equal(0, runExit);
            var fromC = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2))
                .ToDictionary(p => p[0], p => long.Parse(p[1], CultureInfo.InvariantCulture));
            var mismatches = checks.Where(c => fromC[c.Label] != c.CSharp).Select(c => $"{c.Label}: C {fromC[c.Label]}, C# {c.CSharp}").ToList();
            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
            Assert.True(checks.Count > 150, $"only {checks.Count} checks");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Every C# struct field (by offset and size), every struct size, every constant and every ioctl number.</summary>
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
                list.Add(($"{c}.{cField} size", $"sizeof(((struct {c}*)0)->{cField})", FieldSize(field)));
            }
        }

        Struct<V4l2Capability>("v4l2_capability", ("driver", "Driver"), ("card", "Card"), ("bus_info", "BusInfo"), ("version", "Version"),
            ("capabilities", "Capabilities"), ("device_caps", "DeviceCaps"));
        Struct<V4l2FmtDesc>("v4l2_fmtdesc", ("index", "Index"), ("type", "Type"), ("flags", "Flags"), ("description", "Description"),
            ("pixelformat", "PixelFormat"), ("mbus_code", "MbusCode"));
        Struct<V4l2FrmSizeEnum>("v4l2_frmsizeenum", ("index", "Index"), ("pixel_format", "PixelFormat"), ("type", "Type"),
            ("discrete.width", "DiscreteWidth"), ("discrete.height", "DiscreteHeight"), ("stepwise.min_width", "MinWidth"),
            ("stepwise.max_width", "MaxWidth"), ("stepwise.step_width", "StepWidth"), ("stepwise.min_height", "MinHeight"),
            ("stepwise.max_height", "MaxHeight"), ("stepwise.step_height", "StepHeight"));
        Struct<V4l2FrmIvalEnum>("v4l2_frmivalenum", ("index", "Index"), ("pixel_format", "PixelFormat"), ("width", "Width"), ("height", "Height"),
            ("type", "Type"), ("discrete.numerator", "DiscreteNumerator"), ("discrete.denominator", "DiscreteDenominator"),
            ("stepwise.min.numerator", "MinNumerator"), ("stepwise.min.denominator", "MinDenominator"), ("stepwise.max.numerator", "MaxNumerator"),
            ("stepwise.max.denominator", "MaxDenominator"), ("stepwise.step.numerator", "StepNumerator"), ("stepwise.step.denominator", "StepDenominator"));
        Struct<V4l2PixFormat>("v4l2_pix_format", ("width", "Width"), ("height", "Height"), ("pixelformat", "PixelFormat"), ("field", "Field"),
            ("bytesperline", "BytesPerLine"), ("sizeimage", "SizeImage"), ("colorspace", "Colorspace"), ("priv", "Priv"), ("flags", "Flags"),
            ("ycbcr_enc", "YcbcrEnc"), ("quantization", "Quantization"), ("xfer_func", "XferFunc"));
        Struct<V4l2Format>("v4l2_format", ("type", "Type"), ("fmt.pix", "Pix"));
        Struct<V4l2StreamParm>("v4l2_streamparm", ("type", "Type"), ("parm.capture.capability", "Capability"), ("parm.capture.capturemode", "CaptureMode"),
            ("parm.capture.timeperframe.numerator", "TimePerFrameNumerator"), ("parm.capture.timeperframe.denominator", "TimePerFrameDenominator"),
            ("parm.capture.extendedmode", "ExtendedMode"), ("parm.capture.readbuffers", "ReadBuffers"));
        Struct<V4l2RequestBuffers>("v4l2_requestbuffers", ("count", "Count"), ("type", "Type"), ("memory", "Memory"),
            ("capabilities", "Capabilities"), ("flags", "Flags"));
        Struct<V4l2Buffer>("v4l2_buffer", ("index", "Index"), ("type", "Type"), ("bytesused", "BytesUsed"), ("flags", "Flags"), ("field", "Field"),
            ("timestamp.tv_sec", "TimestampSeconds"), ("timestamp.tv_usec", "TimestampMicroseconds"), ("sequence", "Sequence"), ("memory", "Memory"),
            ("m.offset", "Offset"), ("m.userptr", "UserPtr"), ("length", "Length"));
        Struct<V4l2Control>("v4l2_control", ("id", "Id"), ("value", "Value"));
        Struct<V4l2Input>("v4l2_input", ("index", "Index"), ("name", "Name"), ("type", "Type"), ("status", "Status"), ("capabilities", "Capabilities"));
        Struct<V4l2EventSubscription>("v4l2_event_subscription", ("type", "Type"), ("id", "Id"), ("flags", "Flags"));
        Struct<V4l2Event>("v4l2_event", ("type", "Type"), ("u.src_change.changes", "SourceChanges"), ("pending", "Pending"), ("sequence", "Sequence"));
        Struct<PollFd>("pollfd", ("fd", "Fd"), ("events", "Events"), ("revents", "Revents"));

        // Every constant, by the C name its C# name stands for.
        foreach (var f in typeof(V4l2Defs).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            list.Add(($"V4l2Defs.{f.Name}", V4l2CName(f.Name), Convert.ToInt64(f.GetRawConstantValue(), CultureInfo.InvariantCulture)));
        }

        foreach (var f in typeof(LibC).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            list.Add(($"LibC.{f.Name}", LibCName(f.Name), Convert.ToInt64(f.GetRawConstantValue(), CultureInfo.InvariantCulture)));
        }

        foreach (var f in typeof(V4l2Ioctl).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            list.Add(($"V4l2Ioctl.{f.Name}", "VIDIOC_" + Snake(f.Name).ToUpperInvariant() switch
            {
                "QUERY_CAP" => "QUERYCAP",
                "ENUM_FMT" => "ENUM_FMT",
                "GET_FMT" => "G_FMT",
                "SET_FMT" => "S_FMT",
                "REQ_BUFS" => "REQBUFS",
                "QUERY_BUF" => "QUERYBUF",
                "Q_BUF" => "QBUF",
                "DQ_BUF" => "DQBUF",
                "STREAM_ON" => "STREAMON",
                "STREAM_OFF" => "STREAMOFF",
                "GET_PARM" => "G_PARM",
                "SET_PARM" => "S_PARM",
                "ENUM_INPUT" => "ENUMINPUT",
                "GET_CTRL" => "G_CTRL",
                "SET_CTRL" => "S_CTRL",
                "GET_INPUT" => "G_INPUT",
                "SET_INPUT" => "S_INPUT",
                "TRY_FMT" => "TRY_FMT",
                "ENUM_FRAME_SIZES" => "ENUM_FRAMESIZES",
                "ENUM_FRAME_INTERVALS" => "ENUM_FRAMEINTERVALS",
                "DQ_EVENT" => "DQEVENT",
                "SUBSCRIBE_EVENT" => "SUBSCRIBE_EVENT",
                "UNSUBSCRIBE_EVENT" => "UNSUBSCRIBE_EVENT",
                var other => throw new InvalidOperationException($"no C name for ioctl {f.Name} ({other})"),
            }, (long)(nuint)f.GetValue(null)!));
        }

        return list;
    }

    /// <summary>Every <see cref="FieldOffsetAttribute"/> field of the interop structures has a row.</summary>
    private static void CheckCoverage(List<(string Label, string C, long CSharp)> checks)
    {
        var structs = typeof(V4l2Defs).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(V4l2Defs).Namespace && t.IsValueType && t.StructLayoutAttribute?.Value == LayoutKind.Explicit);
        var missing = new List<string>();
        foreach (var t in structs)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!Covered.Contains((t, f.Name)))
                {
                    missing.Add($"{t.Name}.{f.Name}");
                }
            }
        }

        Assert.True(missing.Count == 0, "fields without a layout check: " + string.Join(", ", missing));
        Assert.Contains(checks, c => c.Label == "V4l2Ioctl.DqBuf");
    }

    private static readonly HashSet<(Type, string)> Covered = [];

    private static string CName(Type t) => t.Name switch
    {
        nameof(V4l2Capability) => "v4l2_capability",
        nameof(V4l2FmtDesc) => "v4l2_fmtdesc",
        nameof(V4l2FrmSizeEnum) => "v4l2_frmsizeenum",
        nameof(V4l2FrmIvalEnum) => "v4l2_frmivalenum",
        nameof(V4l2PixFormat) => "v4l2_pix_format",
        nameof(V4l2Format) => "v4l2_format",
        nameof(V4l2StreamParm) => "v4l2_streamparm",
        nameof(V4l2RequestBuffers) => "v4l2_requestbuffers",
        nameof(V4l2Buffer) => "v4l2_buffer",
        nameof(V4l2Control) => "v4l2_control",
        nameof(V4l2Input) => "v4l2_input",
        nameof(V4l2EventSubscription) => "v4l2_event_subscription",
        nameof(V4l2Event) => "v4l2_event",
        nameof(PollFd) => "pollfd",
        _ => throw new InvalidOperationException($"no C name for {t.Name}"),
    };

    private static unsafe long FieldSize(FieldInfo f)
    {
        var fixedBuffer = f.GetCustomAttribute<System.Runtime.CompilerServices.FixedBufferAttribute>();
        if (fixedBuffer is not null)
        {
            return fixedBuffer.Length * Marshal.SizeOf(fixedBuffer.ElementType);
        }

        return f.FieldType == typeof(V4l2PixFormat) ? sizeof(V4l2PixFormat) : Marshal.SizeOf(f.FieldType);
    }

    /// <summary>The videodev2.h name of a <see cref="V4l2Defs"/> constant.</summary>
    private static string V4l2CName(string name)
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BufTypeVideoCapture"] = "V4L2_BUF_TYPE_VIDEO_CAPTURE",
            ["MemoryMmap"] = "V4L2_MEMORY_MMAP",
            ["MemoryUserPtr"] = "V4L2_MEMORY_USERPTR",
            ["FieldAny"] = "V4L2_FIELD_ANY",
            ["FieldNone"] = "V4L2_FIELD_NONE",
            ["CapVideoCapture"] = "V4L2_CAP_VIDEO_CAPTURE",
            ["CapVideoCaptureMplane"] = "V4L2_CAP_VIDEO_CAPTURE_MPLANE",
            ["CapMetaCapture"] = "V4L2_CAP_META_CAPTURE",
            ["CapStreaming"] = "V4L2_CAP_STREAMING",
            ["CapDeviceCaps"] = "V4L2_CAP_DEVICE_CAPS",
            ["FmtFlagCompressed"] = "V4L2_FMT_FLAG_COMPRESSED",
            ["FmtFlagEmulated"] = "V4L2_FMT_FLAG_EMULATED",
            ["FrmSizeTypeDiscrete"] = "V4L2_FRMSIZE_TYPE_DISCRETE",
            ["FrmSizeTypeContinuous"] = "V4L2_FRMSIZE_TYPE_CONTINUOUS",
            ["FrmSizeTypeStepwise"] = "V4L2_FRMSIZE_TYPE_STEPWISE",
            ["FrmIvalTypeDiscrete"] = "V4L2_FRMIVAL_TYPE_DISCRETE",
            ["FrmIvalTypeContinuous"] = "V4L2_FRMIVAL_TYPE_CONTINUOUS",
            ["FrmIvalTypeStepwise"] = "V4L2_FRMIVAL_TYPE_STEPWISE",
            ["CapTimePerFrame"] = "V4L2_CAP_TIMEPERFRAME",
            ["BufFlagMapped"] = "V4L2_BUF_FLAG_MAPPED",
            ["BufFlagQueued"] = "V4L2_BUF_FLAG_QUEUED",
            ["BufFlagDone"] = "V4L2_BUF_FLAG_DONE",
            ["BufFlagError"] = "V4L2_BUF_FLAG_ERROR",
            ["BufFlagTimestampMask"] = "V4L2_BUF_FLAG_TIMESTAMP_MASK",
            ["BufFlagTimestampUnknown"] = "V4L2_BUF_FLAG_TIMESTAMP_UNKNOWN",
            ["BufFlagTimestampMonotonic"] = "V4L2_BUF_FLAG_TIMESTAMP_MONOTONIC",
            ["BufFlagTimestampCopy"] = "V4L2_BUF_FLAG_TIMESTAMP_COPY",
            ["BufFlagTstampSrcMask"] = "V4L2_BUF_FLAG_TSTAMP_SRC_MASK",
            ["BufFlagTstampSrcEof"] = "V4L2_BUF_FLAG_TSTAMP_SRC_EOF",
            ["BufFlagTstampSrcSoe"] = "V4L2_BUF_FLAG_TSTAMP_SRC_SOE",
            ["BufCapSupportsMmap"] = "V4L2_BUF_CAP_SUPPORTS_MMAP",
            ["BufCapSupportsUserPtr"] = "V4L2_BUF_CAP_SUPPORTS_USERPTR",
            ["BufCapSupportsDmaBuf"] = "V4L2_BUF_CAP_SUPPORTS_DMABUF",
            ["InStNoPower"] = "V4L2_IN_ST_NO_POWER",
            ["InStNoSignal"] = "V4L2_IN_ST_NO_SIGNAL",
            ["InStNoSync"] = "V4L2_IN_ST_NO_SYNC",
            ["EventEos"] = "V4L2_EVENT_EOS",
            ["EventSourceChange"] = "V4L2_EVENT_SOURCE_CHANGE",
            ["EventSrcChResolution"] = "V4L2_EVENT_SRC_CH_RESOLUTION",
            ["ColorspaceDefault"] = "V4L2_COLORSPACE_DEFAULT",
            ["ColorspaceSmpte170M"] = "V4L2_COLORSPACE_SMPTE170M",
            ["ColorspaceSmpte240M"] = "V4L2_COLORSPACE_SMPTE240M",
            ["ColorspaceRec709"] = "V4L2_COLORSPACE_REC709",
            ["Colorspace470SystemM"] = "V4L2_COLORSPACE_470_SYSTEM_M",
            ["Colorspace470SystemBg"] = "V4L2_COLORSPACE_470_SYSTEM_BG",
            ["ColorspaceJpeg"] = "V4L2_COLORSPACE_JPEG",
            ["ColorspaceSrgb"] = "V4L2_COLORSPACE_SRGB",
            ["ColorspaceOpRgb"] = "V4L2_COLORSPACE_OPRGB",
            ["ColorspaceBt2020"] = "V4L2_COLORSPACE_BT2020",
            ["ColorspaceRaw"] = "V4L2_COLORSPACE_RAW",
            ["ColorspaceDciP3"] = "V4L2_COLORSPACE_DCI_P3",
            ["XferFuncDefault"] = "V4L2_XFER_FUNC_DEFAULT",
            ["XferFunc709"] = "V4L2_XFER_FUNC_709",
            ["XferFuncSrgb"] = "V4L2_XFER_FUNC_SRGB",
            ["XferFuncOpRgb"] = "V4L2_XFER_FUNC_OPRGB",
            ["XferFuncSmpte240M"] = "V4L2_XFER_FUNC_SMPTE240M",
            ["XferFuncNone"] = "V4L2_XFER_FUNC_NONE",
            ["XferFuncDciP3"] = "V4L2_XFER_FUNC_DCI_P3",
            ["XferFuncSmpte2084"] = "V4L2_XFER_FUNC_SMPTE2084",
            ["YcbcrEncDefault"] = "V4L2_YCBCR_ENC_DEFAULT",
            ["YcbcrEnc601"] = "V4L2_YCBCR_ENC_601",
            ["YcbcrEnc709"] = "V4L2_YCBCR_ENC_709",
            ["YcbcrEncXv601"] = "V4L2_YCBCR_ENC_XV601",
            ["YcbcrEncXv709"] = "V4L2_YCBCR_ENC_XV709",
            ["YcbcrEncSycc"] = "V4L2_YCBCR_ENC_SYCC",
            ["YcbcrEncBt2020"] = "V4L2_YCBCR_ENC_BT2020",
            ["YcbcrEncBt2020ConstLum"] = "V4L2_YCBCR_ENC_BT2020_CONST_LUM",
            ["YcbcrEncSmpte240M"] = "V4L2_YCBCR_ENC_SMPTE240M",
            ["QuantizationDefault"] = "V4L2_QUANTIZATION_DEFAULT",
            ["QuantizationFullRange"] = "V4L2_QUANTIZATION_FULL_RANGE",
            ["QuantizationLimRange"] = "V4L2_QUANTIZATION_LIM_RANGE",
            ["PixFmtNv12"] = "V4L2_PIX_FMT_NV12",
            ["PixFmtP010"] = "V4L2_PIX_FMT_P010",
            ["PixFmtYuyv"] = "V4L2_PIX_FMT_YUYV",
            ["PixFmtMjpeg"] = "V4L2_PIX_FMT_MJPEG",
            ["PixFmtJpeg"] = "V4L2_PIX_FMT_JPEG",
            ["CidBase"] = "V4L2_CID_BASE",
        };
        return known.TryGetValue(name, out var c) ? c : throw new InvalidOperationException($"no C name for V4l2Defs.{name}: add it to the table");
    }

    private static string LibCName(string name) => name switch
    {
        "ORdWr" => "O_RDWR",
        "ONonBlock" => "O_NONBLOCK",
        "OCloExec" => "O_CLOEXEC",
        "PollIn" => "POLLIN",
        "PollPri" => "POLLPRI",
        "PollErr" => "POLLERR",
        "PollHup" => "POLLHUP",
        "PollNval" => "POLLNVAL",
        "ProtRead" => "PROT_READ",
        "ProtWrite" => "PROT_WRITE",
        "MapShared" => "MAP_SHARED",
        "MapPrivate" => "MAP_PRIVATE",
        "MapAnonymous" => "MAP_ANONYMOUS",
        "EfdCloExec" => "EFD_CLOEXEC",
        "EfdNonBlock" => "EFD_NONBLOCK",
        _ when name.StartsWith('E') => name.ToUpperInvariant(),
        _ => throw new InvalidOperationException($"no C name for LibC.{name}: add it to the table"),
    };

    private static string Snake(string pascal)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]))
            {
                sb.Append('_');
            }

            sb.Append(pascal[i]);
        }

        return sb.ToString();
    }
}
