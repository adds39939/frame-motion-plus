using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FrameMotionPlus.Cuda.Tests.Support;
using FrameMotionPlus.Sources.File.Interop;

namespace FrameMotionPlus.Sources.File.Tests;

/// <summary>
/// Proves the FFmpeg interop declarations against the system's FFmpeg headers (/usr/include/libav*), when those are the
/// headers of the major versions the bindings are for (63/63/61; others skip). A C program compiled with gcc prints the
/// offset and size of every structure field the C# views declare, the size of every structure walked as an array, and
/// every enum value and macro the C# side names, and each must equal the C# declaration. Every imported function has a C
/// prototype in a table: the same program checks each against the headers with <c>__builtin_types_compatible_p</c> (a
/// mismatch does not compile), and each C prototype, put through a fixed map of C types to blittable C# types, must
/// equal the import's managed signature, parameter by parameter and the return type (so an <c>int64_t</c> declared
/// <c>int</c>, a <c>size_t</c> declared <c>int</c> or a pointer declared <c>int</c> fails). Reflection checks that the
/// tables cover every <c>[FieldOffset]</c> field, every <see cref="Av"/> constant and every import.
/// libavutil/hwcontext_cuda.h includes &lt;cuda.h&gt;, which only a CUDA toolkit has: a two-line stand-in declaring the
/// two opaque handle types it uses is put first on the include path. Skips without gcc or the headers (CI, Windows).
/// </summary>
public class StructLayoutTests
{
    private const string Include = "/usr/include";

    [Fact]
    public void Interop_offsets_values_and_prototypes_match_the_ffmpeg_headers()
    {
        var gcc = Tools.Which("gcc");
        if (gcc is null || OperatingSystem.IsWindows() || !System.IO.File.Exists(Path.Combine(Include, "libavcodec", "avcodec.h")) ||
            !System.IO.File.Exists(Path.Combine(Include, "libavutil", "hwcontext_cuda.h")))
        {
            Assert.Skip($"needs gcc and the FFmpeg headers (gcc: {gcc ?? "missing"}, headers: {Include}/libav*)");
        }

        var dir = Tools.TempDirectory("ffmpeg-layout");
        try
        {
            System.IO.File.WriteAllText(Path.Combine(dir, "cuda.h"), "typedef struct CUctx_st *CUcontext;\ntypedef struct CUstream_st *CUstream;\n");
            var headers = HeaderMajors(gcc, dir);
            if (headers != (Av.AvCodecMajor, Av.AvFormatMajor, Av.AvUtilMajor))
            {
                Assert.Skip($"the headers are libavcodec {headers.Codec}, libavformat {headers.Format}, libavutil {headers.Util}; the bindings are for " +
                            $"{Av.AvCodecMajor}/{Av.AvFormatMajor}/{Av.AvUtilMajor} (FFmpeg 9)");
            }

            var (numbers, prototypes, fields, constants) = Checks();
            CheckCoverage(prototypes, fields, constants);
            CheckManagedSignatures(prototypes);

            var source = new StringBuilder();
            source.AppendLine("#include <stdio.h>\n#include <stddef.h>\n#include <errno.h>\n#include <stdarg.h>");
            source.AppendLine("#include <libavcodec/avcodec.h>\n#include <libavformat/avformat.h>\n#include <libavutil/hwcontext.h>");
            source.AppendLine("#include <libavutil/hwcontext_cuda.h>\n#include <libavutil/mastering_display_metadata.h>\n#include <libavutil/pixdesc.h>");
            foreach (var (name, type) in prototypes)
            {
                source.Append(CultureInfo.InvariantCulture, $"_Static_assert(__builtin_types_compatible_p(__typeof__({name}), {type}), \"{name}: prototype differs from the table\");\n");
            }

            source.AppendLine("int main(void)\n{");
            foreach (var (label, expression, _) in numbers)
            {
                source.Append(CultureInfo.InvariantCulture, $"    printf(\"%s=%lld\\n\", \"{label}\", (long long)({expression}));\n");
            }

            source.AppendLine("    return 0;\n}");
            System.IO.File.WriteAllText(Path.Combine(dir, "layout.c"), source.ToString());
            var (compileExit, compileOutput) = Tools.Run(gcc, ["-std=gnu11", "-Wall", "-Werror", "-I", dir, "-I", Include, "-o", "layout", "layout.c"], dir);
            Assert.True(compileExit == 0, compileOutput);
            var (runExit, output) = Tools.Run(Path.Combine(dir, "layout"), [], dir);
            Assert.Equal(0, runExit);
            var fromC = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => long.Parse(p[1], CultureInfo.InvariantCulture));
            var mismatches = numbers.Where(c => fromC[c.Label] != c.CSharp).Select(c => $"{c.Label}: C {fromC[c.Label]}, C# {c.CSharp}").ToList();
            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
            Assert.True(numbers.Count > 200, $"only {numbers.Count} numeric checks");
            Assert.True(prototypes.Count >= 35, $"only {prototypes.Count} prototype checks");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The C type map on its own: what each C parameter or return type must be declared as in C#, including the mutations
    /// a review tried (each must not map to what it replaced).
    /// </summary>
    [Theory]
    [InlineData("int64_t", "long")]
    [InlineData("int", "int")]
    [InlineData("unsigned", "uint")]
    [InlineData("size_t", "nuint")]
    [InlineData("enum AVFrameSideDataType", "int")]
    [InlineData("const char *", "byte*")]
    [InlineData("const AVFrame *", "AVFrame*")]
    [InlineData("AVFrameSideData *", "AVFrameSideData*")]
    [InlineData("AVFrame **", "AVFrame**")]
    [InlineData("const AVCodec *", "void*")]
    [InlineData("const AVCodec **", "void**")]
    [InlineData("AVDictionary **", "void**")]
    [InlineData("const struct AVCodecParameters *", "AVCodecParameters*")]
    [InlineData("va_list", "nint")]
    [InlineData("void (*)(void *, int, const char *, va_list)", "delegate* unmanaged[Cdecl]<void*, int, byte*, nint, void>")]
    public void C_types_map_to_one_managed_type(string c, string managed) => Assert.Equal(managed, Managed(c));

    /// <summary>The mutations a review made to the imports (an int64_t taken as int, a size_t as int, a pointer returned as int) are caught.</summary>
    [Fact]
    public void Signature_mutations_are_caught()
    {
        Assert.NotEqual(Managed("int64_t"), Describe(typeof(int)));
        Assert.NotEqual(Managed("size_t"), Describe(typeof(int)));
        Assert.NotEqual(Managed("AVFrameSideData *"), Describe(typeof(int)));
        Assert.NotEqual(Managed("enum AVFrameSideDataType"), Describe(typeof(long)));
        Assert.Equal(Managed("int64_t"), Describe(typeof(long)));
        Assert.Equal(Managed("AVFrameSideData *"), Describe(typeof(AVFrameSideData*)));
    }

    /// <summary>The library major versions the system headers declare.</summary>
    private static (long Codec, long Format, long Util) HeaderMajors(string gcc, string dir)
    {
        System.IO.File.WriteAllText(Path.Combine(dir, "version.c"),
            "#include <stdio.h>\n#include <libavcodec/version_major.h>\n#include <libavformat/version_major.h>\n#include <libavutil/version.h>\n" +
            "int main(void) { printf(\"%d %d %d\\n\", LIBAVCODEC_VERSION_MAJOR, LIBAVFORMAT_VERSION_MAJOR, LIBAVUTIL_VERSION_MAJOR); return 0; }\n");
        var (exit, output) = Tools.Run(gcc, ["-std=gnu11", "-I", dir, "-I", Include, "-o", "version", "version.c"], dir);
        Assert.True(exit == 0, output);
        var (runExit, text) = Tools.Run(Path.Combine(dir, "version"), [], dir);
        Assert.Equal(0, runExit);
        var v = text.Trim().Split(' ').Select(t => long.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        return (v[0], v[1], v[2]);
    }

    /// <summary>Every imported function, every field of every view and every <see cref="Av"/> constant is in the tables.</summary>
    private static void CheckCoverage(List<(string Name, string Type)> prototypes, HashSet<(string Type, string Field)> fields, HashSet<string> constants)
    {
        Assert.Equal(Imports().Keys.Order(StringComparer.Ordinal), prototypes.Select(p => p.Name).Order(StringComparer.Ordinal));

        var views = typeof(Av).Assembly.GetTypes()
            .Where(t => t.IsValueType && t.Namespace == typeof(Av).Namespace && t.Name.StartsWith("AV", StringComparison.Ordinal));
        var missing = views
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => (Type: t.Name, Field: f.Name)))
            .Where(f => !fields.Contains(f))
            .Select(f => $"{f.Type}.{f.Field}")
            .ToList();
        Assert.True(missing.Count == 0, "fields with no offset check: " + string.Join(", ", missing));

        var unchecked_ = typeof(Av).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !constants.Contains(f.Name))
            .Select(f => f.Name)
            .ToList();
        Assert.True(unchecked_.Count == 0, "constants with no value check: " + string.Join(", ", unchecked_));
    }

    /// <summary>Each import's managed signature is what the type map makes of its C prototype.</summary>
    private static void CheckManagedSignatures(List<(string Name, string Type)> prototypes)
    {
        var imports = Imports();
        var errors = new List<string>();
        foreach (var (name, type) in prototypes)
        {
            var (ret, parameters) = SplitPrototype(type);
            var method = imports[name];
            var expected = $"{Managed(ret)} ({string.Join(", ", parameters.Select(Managed))})";
            var actual = $"{Describe(method.ReturnType)} ({string.Join(", ", method.GetParameters().Select(p => Describe(p.ParameterType, p.GetModifiedParameterType())))})";
            if (expected != actual)
            {
                errors.Add($"{name}: C {type} is {expected} in C#, the import is {actual}");
            }
        }

        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    private static Dictionary<string, MethodInfo> Imports() =>
        new[] { typeof(AvUtil), typeof(AvCodec), typeof(AvFormat) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.IsDefined(typeof(LibraryImportAttribute), false))
            .ToDictionary(m => m.Name);

    /// <summary>"R (P1, P2)" into R and the parameters (none for "(void)"), splitting at commas outside parentheses.</summary>
    private static (string Return, List<string> Parameters) SplitPrototype(string type)
    {
        var open = TopLevelOpen(type);
        var ret = type[..open].Trim();
        var inner = type[(open + 1)..type.LastIndexOf(')')];
        var parameters = new List<string>();
        int depth = 0, from = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            depth += inner[i] == '(' ? 1 : inner[i] == ')' ? -1 : 0;
            if (inner[i] == ',' && depth == 0)
            {
                parameters.Add(inner[from..i].Trim());
                from = i + 1;
            }
        }

        var last = inner[from..].Trim();
        if (last.Length > 0 && last != "void")
        {
            parameters.Add(last);
        }

        return (ret, parameters);
    }

    /// <summary>The '(' that opens a function type's parameter list: the last one at depth 0 (a function-pointer return would precede it).</summary>
    private static int TopLevelOpen(string type)
    {
        int depth = 0, open = -1;
        for (var i = 0; i < type.Length; i++)
        {
            if (type[i] == '(')
            {
                if (depth == 0)
                {
                    open = i;
                }

                depth++;
            }
            else if (type[i] == ')')
            {
                depth--;
            }
        }

        return open;
    }

    /// <summary>
    /// The fixed map of C types to blittable C# types: integers by width and signedness, enums as int, va_list as nint,
    /// char as byte, structures the views declare as themselves, other structures (opaque to C#: AVCodec, AVDictionary)
    /// as void, pointers and function pointers built from those.
    /// </summary>
    private static string Managed(string c)
    {
        c = Regex.Replace(c, @"\b(const|struct)\b", " ");
        c = Regex.Replace(c, @"\s+", " ").Trim();
        var function = Regex.Match(c, @"^(.*)\(\s*\*\s*\)\s*\((.*)\)$");
        if (function.Success)
        {
            var (ret, parameters) = SplitPrototype(function.Groups[1].Value + " (" + function.Groups[2].Value + ")");
            return $"delegate* unmanaged[Cdecl]<{string.Join(", ", parameters.Select(Managed).Append(Managed(ret)))}>";
        }

        var stars = c.Count(ch => ch == '*');
        var name = c.Replace("*", string.Empty, StringComparison.Ordinal).Trim();
        var baseType = name switch
        {
            "void" => "void",
            "int" => "int",
            "unsigned" or "unsigned int" => "uint",
            "int64_t" => "long",
            "uint64_t" => "ulong",
            "size_t" => "nuint",
            "char" => "byte",
            "va_list" => "nint",
            _ when name.StartsWith("enum ", StringComparison.Ordinal) => "int",
            _ when typeof(Av).Assembly.GetType(typeof(Av).Namespace + "." + name) is { IsValueType: true } => name,
            _ when name.StartsWith("AV", StringComparison.Ordinal) => "void",
            _ => throw new InvalidOperationException($"no C# type for C type '{name}'"),
        };
        return baseType + new string('*', stars);
    }

    /// <summary>A managed parameter or return type, spelled as <see cref="Managed"/> spells it.</summary>
    private static string Describe(Type type, Type? modified = null)
    {
        if (type.IsPointer)
        {
            return Describe(type.GetElementType()!) + "*";
        }

        if (type.IsFunctionPointer)
        {
            var callConvs = (modified ?? type).GetFunctionPointerCallingConventions();
            var cdecl = type.IsUnmanagedFunctionPointer && callConvs.Length == 1 && callConvs[0] == typeof(CallConvCdecl);
            var types = type.GetFunctionPointerParameterTypes().Append(type.GetFunctionPointerReturnType()).Select(t => Describe(t));
            return $"delegate* {(cdecl ? "unmanaged[Cdecl]" : "?")}<{string.Join(", ", types)}>";
        }

        return type == typeof(void) ? "void"
            : type == typeof(int) ? "int"
            : type == typeof(uint) ? "uint"
            : type == typeof(long) ? "long"
            : type == typeof(ulong) ? "ulong"
            : type == typeof(nint) ? "nint"
            : type == typeof(nuint) ? "nuint"
            : type == typeof(byte) ? "byte"
            : type.Name;
    }

    /// <summary>A field's size as C# lays it out (a fixed buffer: all its elements).</summary>
    private static long ManagedSize(Type view, string field)
    {
        var f = view.GetField(field)!;
        if (f.GetCustomAttribute<FixedBufferAttribute>() is { } fixedBuffer)
        {
            return fixedBuffer.Length * Marshal.SizeOf(fixedBuffer.ElementType);
        }

        var t = f.FieldType;
        return t.IsPointer || t.IsFunctionPointer || t == typeof(nint) || t == typeof(nuint) ? IntPtr.Size : Marshal.SizeOf(t);
    }

    private static unsafe (List<(string Label, string Expression, long CSharp)> Numbers, List<(string Name, string Type)> Prototypes,
        HashSet<(string Type, string Field)> Fields, HashSet<string> Constants) Checks()
    {
        var list = new List<(string, string, long)>();
        var fields = new HashSet<(string, string)>();
        var constants = new HashSet<string>(StringComparer.Ordinal);

        // A field's offset (the C# side computed from the view's layout), and for a whole member also its size; the C#
        // field named in the expression counts as covered.
        void Offset(string cType, string member, long csharp, [CallerArgumentExpression(nameof(csharp))] string expression = "")
        {
            list.Add(($"offsetof({cType},{member})", $"offsetof({cType}, {member})", csharp));
            var field = Regex.Match(expression, @"\b[a-z]{1,3}\.([A-Z]\w*)").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(field), $"no C# field in '{expression}'");
            fields.Add((cType, field));
            if (!member.Contains('[', StringComparison.Ordinal) && !member.Contains('.', StringComparison.Ordinal))
            {
                var view = typeof(Av).Assembly.GetType(typeof(Av).Namespace + "." + cType)!;
                list.Add(($"sizeof({cType}.{member})", $"sizeof((({cType}*)0)->{member})", ManagedSize(view, field)));
            }
        }

        void Size(string cType, long csharp) => list.Add(($"sizeof({cType})", $"sizeof({cType})", csharp));

        // A value; an Av constant named in the expression counts as covered.
        void Value(string cName, long csharp, [CallerArgumentExpression(nameof(csharp))] string expression = "")
        {
            list.Add((cName, cName, csharp));
            if (expression.StartsWith("Av.", StringComparison.Ordinal))
            {
                constants.Add(expression[3..]);
            }
        }

        AVFormatContext fc;
        var b = (byte*)&fc;
        Offset("AVFormatContext", "iformat", (byte*)&fc.IFormat - b);
        Offset("AVFormatContext", "pb", (byte*)&fc.Pb - b);
        Offset("AVFormatContext", "nb_streams", (byte*)&fc.NbStreams - b);
        Offset("AVFormatContext", "streams", (byte*)&fc.Streams - b);
        Offset("AVFormatContext", "start_time", (byte*)&fc.StartTime - b);
        Offset("AVFormatContext", "duration", (byte*)&fc.Duration - b);
        Offset("AVFormatContext", "bit_rate", (byte*)&fc.BitRate - b);

        AVIOContext io;
        b = (byte*)&io;
        Offset("AVIOContext", "seekable", (byte*)&io.Seekable - b);

        AVInputFormat inf;
        b = (byte*)&inf;
        Offset("AVInputFormat", "name", (byte*)&inf.Name - b);
        Offset("AVInputFormat", "long_name", (byte*)&inf.LongName - b);
        Offset("AVInputFormat", "flags", (byte*)&inf.Flags - b);

        AVStream st;
        b = (byte*)&st;
        Offset("AVStream", "index", (byte*)&st.Index - b);
        Offset("AVStream", "codecpar", (byte*)&st.CodecPar - b);
        Offset("AVStream", "time_base", (byte*)&st.TimeBase - b);
        Offset("AVStream", "start_time", (byte*)&st.StartTime - b);
        Offset("AVStream", "duration", (byte*)&st.Duration - b);
        Offset("AVStream", "nb_frames", (byte*)&st.NbFrames - b);
        Offset("AVStream", "discard", (byte*)&st.Discard - b);
        Offset("AVStream", "avg_frame_rate", (byte*)&st.AvgFrameRate - b);
        Offset("AVStream", "r_frame_rate", (byte*)&st.RFrameRate - b);

        AVCodecParameters cp;
        b = (byte*)&cp;
        Offset("AVCodecParameters", "codec_type", (byte*)&cp.CodecType - b);
        Offset("AVCodecParameters", "codec_id", (byte*)&cp.CodecId - b);
        Offset("AVCodecParameters", "coded_side_data", (byte*)&cp.CodedSideData - b);
        Offset("AVCodecParameters", "nb_coded_side_data", (byte*)&cp.NbCodedSideData - b);
        Offset("AVCodecParameters", "format", (byte*)&cp.Format - b);
        Offset("AVCodecParameters", "bits_per_raw_sample", (byte*)&cp.BitsPerRawSample - b);
        Offset("AVCodecParameters", "profile", (byte*)&cp.Profile - b);
        Offset("AVCodecParameters", "level", (byte*)&cp.Level - b);
        Offset("AVCodecParameters", "width", (byte*)&cp.Width - b);
        Offset("AVCodecParameters", "height", (byte*)&cp.Height - b);
        Offset("AVCodecParameters", "framerate", (byte*)&cp.FrameRate - b);
        Offset("AVCodecParameters", "color_range", (byte*)&cp.ColorRange - b);
        Offset("AVCodecParameters", "color_primaries", (byte*)&cp.ColorPrimaries - b);
        Offset("AVCodecParameters", "color_trc", (byte*)&cp.ColorTrc - b);
        Offset("AVCodecParameters", "color_space", (byte*)&cp.ColorSpace - b);
        Offset("AVCodecParameters", "chroma_location", (byte*)&cp.ChromaLocation - b);

        AVCodecContext cc;
        b = (byte*)&cc;
        Offset("AVCodecContext", "codec_id", (byte*)&cc.CodecId - b);
        Offset("AVCodecContext", "opaque", (byte*)&cc.Opaque - b);
        Offset("AVCodecContext", "pkt_timebase", (byte*)&cc.PktTimebase - b);
        Offset("AVCodecContext", "width", (byte*)&cc.Width - b);
        Offset("AVCodecContext", "height", (byte*)&cc.Height - b);
        Offset("AVCodecContext", "coded_width", (byte*)&cc.CodedWidth - b);
        Offset("AVCodecContext", "coded_height", (byte*)&cc.CodedHeight - b);
        Offset("AVCodecContext", "pix_fmt", (byte*)&cc.PixFmt - b);
        Offset("AVCodecContext", "sw_pix_fmt", (byte*)&cc.SwPixFmt - b);
        Offset("AVCodecContext", "get_format", (byte*)&cc.GetFormat - b);
        Offset("AVCodecContext", "hw_frames_ctx", (byte*)&cc.HwFramesCtx - b);
        Offset("AVCodecContext", "hw_device_ctx", (byte*)&cc.HwDeviceCtx - b);
        Offset("AVCodecContext", "thread_count", (byte*)&cc.ThreadCount - b);
        Offset("AVCodecContext", "thread_type", (byte*)&cc.ThreadType - b);
        Offset("AVCodecContext", "profile", (byte*)&cc.Profile - b);

        AVPacket pk;
        b = (byte*)&pk;
        Offset("AVPacket", "pts", (byte*)&pk.Pts - b);
        Offset("AVPacket", "dts", (byte*)&pk.Dts - b);
        Offset("AVPacket", "size", (byte*)&pk.Size - b);
        Offset("AVPacket", "stream_index", (byte*)&pk.StreamIndex - b);
        Offset("AVPacket", "flags", (byte*)&pk.Flags - b);
        Offset("AVPacket", "duration", (byte*)&pk.Duration - b);

        AVFrame fr;
        b = (byte*)&fr;
        Offset("AVFrame", "data", (byte*)fr.Data - b);
        Offset("AVFrame", "data[1]", (byte*)&fr.Data[1] - b);
        Offset("AVFrame", "linesize", (byte*)fr.LineSize - b);
        Offset("AVFrame", "linesize[1]", (byte*)&fr.LineSize[1] - b);
        Offset("AVFrame", "width", (byte*)&fr.Width - b);
        Offset("AVFrame", "height", (byte*)&fr.Height - b);
        Offset("AVFrame", "format", (byte*)&fr.Format - b);
        Offset("AVFrame", "pts", (byte*)&fr.Pts - b);
        Offset("AVFrame", "side_data", (byte*)&fr.SideData - b);
        Offset("AVFrame", "nb_side_data", (byte*)&fr.NbSideData - b);
        Offset("AVFrame", "flags", (byte*)&fr.Flags - b);
        Offset("AVFrame", "color_range", (byte*)&fr.ColorRange - b);
        Offset("AVFrame", "color_primaries", (byte*)&fr.ColorPrimaries - b);
        Offset("AVFrame", "color_trc", (byte*)&fr.ColorTrc - b);
        Offset("AVFrame", "colorspace", (byte*)&fr.ColorSpace - b);
        Offset("AVFrame", "chroma_location", (byte*)&fr.ChromaLocation - b);
        Offset("AVFrame", "best_effort_timestamp", (byte*)&fr.BestEffortTimestamp - b);
        Offset("AVFrame", "decode_error_flags", (byte*)&fr.DecodeErrorFlags - b);
        Offset("AVFrame", "hw_frames_ctx", (byte*)&fr.HwFramesCtx - b);
        Offset("AVFrame", "crop_top", (byte*)&fr.CropTop - b);
        Offset("AVFrame", "crop_bottom", (byte*)&fr.CropBottom - b);
        Offset("AVFrame", "crop_left", (byte*)&fr.CropLeft - b);
        Offset("AVFrame", "crop_right", (byte*)&fr.CropRight - b);
        Offset("AVFrame", "duration", (byte*)&fr.Duration - b);
        Value("sizeof(((AVFrame*)0)->data[0])", sizeof(ulong));
        Value("sizeof(((AVFrame*)0)->crop_top)", sizeof(nuint));

        AVFrameSideData fsd;
        b = (byte*)&fsd;
        Offset("AVFrameSideData", "type", (byte*)&fsd.Type - b);
        Offset("AVFrameSideData", "data", (byte*)&fsd.Data - b);
        Offset("AVFrameSideData", "size", (byte*)&fsd.Size - b);

        AVPacketSideData psd;
        b = (byte*)&psd;
        Offset("AVPacketSideData", "data", (byte*)&psd.Data - b);
        Offset("AVPacketSideData", "size", (byte*)&psd.Size - b);
        Offset("AVPacketSideData", "type", (byte*)&psd.Type - b);
        Size("AVPacketSideData", sizeof(AVPacketSideData));

        AVBufferRef br;
        b = (byte*)&br;
        Offset("AVBufferRef", "data", (byte*)&br.Data - b);
        Offset("AVBufferRef", "size", (byte*)&br.Size - b);

        AVHWDeviceContext dc;
        b = (byte*)&dc;
        Offset("AVHWDeviceContext", "type", (byte*)&dc.Type - b);
        Offset("AVHWDeviceContext", "hwctx", (byte*)&dc.HwCtx - b);

        AVHWFramesContext hf;
        b = (byte*)&hf;
        Offset("AVHWFramesContext", "device_ctx", (byte*)&hf.DeviceCtx - b);
        Offset("AVHWFramesContext", "initial_pool_size", (byte*)&hf.InitialPoolSize - b);
        Offset("AVHWFramesContext", "format", (byte*)&hf.Format - b);
        Offset("AVHWFramesContext", "sw_format", (byte*)&hf.SwFormat - b);
        Offset("AVHWFramesContext", "width", (byte*)&hf.Width - b);
        Offset("AVHWFramesContext", "height", (byte*)&hf.Height - b);

        AVCUDADeviceContext cu;
        b = (byte*)&cu;
        Offset("AVCUDADeviceContext", "cuda_ctx", (byte*)&cu.CudaCtx - b);
        Offset("AVCUDADeviceContext", "stream", (byte*)&cu.Stream - b);

        AVMasteringDisplayMetadata md;
        b = (byte*)&md;
        Offset("AVMasteringDisplayMetadata", "display_primaries", (byte*)md.DisplayPrimaries - b);
        Offset("AVMasteringDisplayMetadata", "display_primaries[1][1]", (byte*)&md.DisplayPrimaries[6] - b);
        Offset("AVMasteringDisplayMetadata", "display_primaries[2][1].den", (byte*)&md.DisplayPrimaries[11] - b);
        Offset("AVMasteringDisplayMetadata", "white_point", (byte*)md.WhitePoint - b);
        Offset("AVMasteringDisplayMetadata", "white_point[1].den", (byte*)&md.WhitePoint[3] - b);
        Offset("AVMasteringDisplayMetadata", "min_luminance", (byte*)&md.MinLuminance - b);
        Offset("AVMasteringDisplayMetadata", "max_luminance", (byte*)&md.MaxLuminance - b);
        Offset("AVMasteringDisplayMetadata", "has_primaries", (byte*)&md.HasPrimaries - b);
        Offset("AVMasteringDisplayMetadata", "has_luminance", (byte*)&md.HasLuminance - b);

        AVContentLightMetadata cl;
        b = (byte*)&cl;
        Offset("AVContentLightMetadata", "MaxCLL", (byte*)&cl.MaxCll - b);
        Offset("AVContentLightMetadata", "MaxFALL", (byte*)&cl.MaxFall - b);

        AVRational r;
        b = (byte*)&r;
        Offset("AVRational", "num", (byte*)&r.Num - b);
        Offset("AVRational", "den", (byte*)&r.Den - b);
        Size("AVRational", sizeof(AVRational));

        AVCodecHWConfig hc;
        b = (byte*)&hc;
        Offset("AVCodecHWConfig", "pix_fmt", (byte*)&hc.PixFmt - b);
        Offset("AVCodecHWConfig", "methods", (byte*)&hc.Methods - b);
        Offset("AVCodecHWConfig", "device_type", (byte*)&hc.DeviceType - b);

        // Enum sizes: every enum field above is declared int.
        Size("enum AVPixelFormat", 4);
        Size("enum AVCodecID", 4);
        Size("enum AVColorPrimaries", 4);
        Size("enum AVDiscard", 4);

        Value("AVMEDIA_TYPE_VIDEO", Av.MediaTypeVideo);
        Value("AV_CODEC_ID_H264", Av.CodecIdH264);
        Value("AV_CODEC_ID_VP9", Av.CodecIdVp9);
        Value("AV_CODEC_ID_HEVC", Av.CodecIdHevc);
        Value("AV_CODEC_ID_AV1", Av.CodecIdAv1);
        Value("AV_PIX_FMT_NONE", Av.PixFmtNone);
        Value("AV_PIX_FMT_YUV420P", Av.PixFmtYuv420P);
        Value("AV_PIX_FMT_YUVJ420P", Av.PixFmtYuvJ420P);
        Value("AV_PIX_FMT_NV12", Av.PixFmtNv12);
        Value("AV_PIX_FMT_YUV420P10LE", Av.PixFmtYuv420P10Le);
        Value("AV_PIX_FMT_CUDA", Av.PixFmtCuda);
        Value("AV_PIX_FMT_P010LE", Av.PixFmtP010Le);
        Value("AV_HWDEVICE_TYPE_CUDA", Av.HwDeviceTypeCuda);
        Value("AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX", Av.CodecHwConfigMethodHwDeviceCtx);
        Value("AV_CUDA_USE_CURRENT_CONTEXT", Av.CudaUseCurrentContext);
        Value("AV_NOPTS_VALUE", Av.NoPtsValue);
        Value("AVERROR_EOF", Av.ErrorEof);
        Value("AVERROR(EAGAIN)", Av.ErrorEagain);
        Value("AVERROR_INVALIDDATA", Av.ErrorInvalidData);
        Value("AVERROR_DECODER_NOT_FOUND", Av.ErrorDecoderNotFound);
        Value("AVSEEK_FLAG_BACKWARD", Av.SeekFlagBackward);
        Value("AVSEEK_FLAG_BYTE", Av.SeekFlagByte);
        Value("AVIO_SEEKABLE_NORMAL", Av.AvioSeekableNormal);
        Value("AVDISCARD_ALL", Av.DiscardAll);
        Value("AV_FRAME_DATA_MASTERING_DISPLAY_METADATA", Av.FrameDataMasteringDisplayMetadata);
        Value("AV_FRAME_DATA_CONTENT_LIGHT_LEVEL", Av.FrameDataContentLightLevel);
        Value("AV_PKT_DATA_MASTERING_DISPLAY_METADATA", Av.PacketDataMasteringDisplayMetadata);
        Value("AV_PKT_DATA_CONTENT_LIGHT_LEVEL", Av.PacketDataContentLightLevel);
        Value("AV_FRAME_FLAG_CORRUPT", Av.FrameFlagCorrupt);
        Value("AV_PKT_FLAG_KEY", Av.PacketFlagKey);
        Value("AVFMT_TS_DISCONT", Av.FormatTsDiscont);
        Value("AVFMT_NOTIMESTAMPS", Av.FormatNoTimestamps);
        Value("AVFMT_NO_BYTE_SEEK", Av.FormatNoByteSeek);
        Value("AV_LOG_ERROR", Av.LogError);
        Value("AVCOL_PRI_BT709", Av.ColorPrimariesBt709);
        Value("AVCOL_PRI_BT470BG", Av.ColorPrimariesBt470Bg);
        Value("AVCOL_PRI_SMPTE170M", Av.ColorPrimariesSmpte170M);
        Value("AVCOL_PRI_SMPTE240M", Av.ColorPrimariesSmpte240M);
        Value("AVCOL_PRI_BT2020", Av.ColorPrimariesBt2020);
        Value("AVCOL_PRI_SMPTE431", Av.ColorPrimariesSmpte431);
        Value("AVCOL_PRI_SMPTE432", Av.ColorPrimariesSmpte432);
        Value("AVCOL_PRI_UNSPECIFIED", Av.ColorPrimariesUnspecified);
        Value("AVCOL_TRC_LINEAR", Av.TransferLinear);
        Value("AVCOL_TRC_IEC61966_2_1", Av.TransferIec61966_2_1);
        Value("AVCOL_TRC_SMPTE2084", Av.TransferSmpte2084);
        Value("AVCOL_TRC_ARIB_STD_B67", Av.TransferAribStdB67);
        Value("AVCOL_SPC_BT709", Av.ColorSpaceBt709);
        Value("AVCOL_SPC_UNSPECIFIED", Av.ColorSpaceUnspecified);
        Value("AVCOL_SPC_RESERVED", Av.ColorSpaceReserved);
        Value("AVCOL_SPC_BT470BG", Av.ColorSpaceBt470Bg);
        Value("AVCOL_SPC_SMPTE170M", Av.ColorSpaceSmpte170M);
        Value("AVCOL_SPC_BT2020_NCL", Av.ColorSpaceBt2020Ncl);
        Value("AVCOL_RANGE_JPEG", Av.ColorRangeJpeg);
        Value("AVCHROMA_LOC_CENTER", Av.ChromaLocationCenter);
        Value("AVCHROMA_LOC_TOPLEFT", Av.ChromaLocationTopLeft);
        Value("AVCHROMA_LOC_TOP", Av.ChromaLocationTop);
        Value("AVCHROMA_LOC_BOTTOMLEFT", Av.ChromaLocationBottomLeft);
        Value("AVCHROMA_LOC_BOTTOM", Av.ChromaLocationBottom);
        Value("LIBAVCODEC_VERSION_MAJOR", Av.AvCodecMajor);
        Value("LIBAVFORMAT_VERSION_MAJOR", Av.AvFormatMajor);
        Value("LIBAVUTIL_VERSION_MAJOR", Av.AvUtilMajor);

        // C prototypes of every function imported (AvNative.cs), in the order declared there.
        var prototypes = new List<(string, string)>
        {
            ("avutil_version", "unsigned (void)"),
            ("av_frame_alloc", "AVFrame *(void)"),
            ("av_frame_free", "void (AVFrame **)"),
            ("av_frame_unref", "void (AVFrame *)"),
            ("av_frame_get_side_data", "AVFrameSideData *(const AVFrame *, enum AVFrameSideDataType)"),
            ("av_buffer_unref", "void (AVBufferRef **)"),
            ("av_hwdevice_ctx_create", "int (AVBufferRef **, enum AVHWDeviceType, const char *, AVDictionary *, int)"),
            ("av_hwframe_get_buffer", "int (AVBufferRef *, AVFrame *, int)"),
            ("av_strerror", "int (int, char *, size_t)"),
            ("av_get_pix_fmt_name", "const char *(enum AVPixelFormat)"),
            ("av_log_set_level", "void (int)"),
            ("av_log_set_callback", "void (void (*)(void *, int, const char *, va_list))"),
            ("av_log_format_line2", "int (void *, int, const char *, va_list, char *, int, int *)"),
            ("avcodec_version", "unsigned (void)"),
            ("avcodec_find_decoder_by_name", "const AVCodec *(const char *)"),
            ("avcodec_alloc_context3", "AVCodecContext *(const AVCodec *)"),
            ("avcodec_free_context", "void (AVCodecContext **)"),
            ("avcodec_parameters_to_context", "int (AVCodecContext *, const struct AVCodecParameters *)"),
            ("avcodec_open2", "int (AVCodecContext *, const AVCodec *, AVDictionary **)"),
            ("avcodec_send_packet", "int (AVCodecContext *, const AVPacket *)"),
            ("avcodec_receive_frame", "int (AVCodecContext *, AVFrame *)"),
            ("avcodec_flush_buffers", "void (AVCodecContext *)"),
            ("avcodec_get_hw_config", "const AVCodecHWConfig *(const AVCodec *, int)"),
            ("avcodec_get_name", "const char *(enum AVCodecID)"),
            ("avcodec_profile_name", "const char *(enum AVCodecID, int)"),
            ("av_packet_alloc", "AVPacket *(void)"),
            ("av_packet_free", "void (AVPacket **)"),
            ("av_packet_unref", "void (AVPacket *)"),
            ("av_packet_side_data_get", "const AVPacketSideData *(const AVPacketSideData *, int, enum AVPacketSideDataType)"),
            ("avformat_version", "unsigned (void)"),
            ("avformat_open_input", "int (AVFormatContext **, const char *, const AVInputFormat *, AVDictionary **)"),
            ("avformat_find_stream_info", "int (AVFormatContext *, AVDictionary **)"),
            ("av_find_best_stream", "int (AVFormatContext *, enum AVMediaType, int, int, const AVCodec **, int)"),
            ("avformat_close_input", "void (AVFormatContext **)"),
            ("av_read_frame", "int (AVFormatContext *, AVPacket *)"),
            ("av_seek_frame", "int (AVFormatContext *, int, int64_t, int)"),
        };

        // The get_format callback the decoder calls (AVCodecContext.get_format), as the C# function pointer is typed.
        list.Add(("get_format is int (*)(AVCodecContext *, const int *)",
            "__builtin_types_compatible_p(__typeof__(((AVCodecContext*)0)->get_format), enum AVPixelFormat (*)(struct AVCodecContext *, const enum AVPixelFormat *))", 1));
        return (list, prototypes, fields, constants);
    }
}
