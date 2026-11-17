using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FrameMotionPlus.Sources.File.Interop;

// Source-generated P/Invoke for the FFmpeg functions used, all with blittable signatures (pointers, ints, longs): no
// marshalling, no allocation per call. Strings are passed as NUL-terminated UTF-8 the caller owns. The logical library
// names resolve to the system's sonames (FFmpegLibraries); every caller makes sure they are loaded and version-checked
// first. StructLayoutTests checks each prototype against the headers with __builtin_types_compatible_p.

internal static unsafe partial class AvUtil
{
    public const string Library = "avutil";

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint avutil_version();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVFrame* av_frame_alloc();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_frame_free(AVFrame** frame);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_frame_unref(AVFrame* frame);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVFrameSideData* av_frame_get_side_data(AVFrame* frame, int type);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_buffer_unref(AVBufferRef** buffer);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_hwdevice_ctx_create(AVBufferRef** deviceCtx, int type, byte* device, void* opts, int flags);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_hwframe_get_buffer(AVBufferRef* hwFrameCtx, AVFrame* frame, int flags);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_strerror(int errnum, byte* errbuf, nuint errbufSize);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* av_get_pix_fmt_name(int pixFmt);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_log_set_level(int level);

    /// <summary><c>void av_log_set_callback(void (*callback)(void*, int, const char*, va_list))</c>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_log_set_callback(delegate* unmanaged[Cdecl]<void*, int, byte*, nint, void> callback);

    /// <summary>Formats one log line; <paramref name="vl"/> is the callback's <c>va_list</c>, passed on as received.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_log_format_line2(void* ptr, int level, byte* fmt, nint vl, byte* line, int lineSize, int* printPrefix);
}

internal static unsafe partial class AvCodec
{
    public const string Library = "avcodec";

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint avcodec_version();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void* avcodec_find_decoder_by_name(byte* name);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVCodecContext* avcodec_alloc_context3(void* codec);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void avcodec_free_context(AVCodecContext** context);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_parameters_to_context(AVCodecContext* context, AVCodecParameters* parameters);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_open2(AVCodecContext* context, void* codec, void** options);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_send_packet(AVCodecContext* context, AVPacket* packet);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_receive_frame(AVCodecContext* context, AVFrame* frame);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void avcodec_flush_buffers(AVCodecContext* context);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVCodecHWConfig* avcodec_get_hw_config(void* codec, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* avcodec_get_name(int id);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* avcodec_profile_name(int codecId, int profile);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVPacket* av_packet_alloc();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_packet_free(AVPacket** packet);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_packet_unref(AVPacket* packet);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVPacketSideData* av_packet_side_data_get(AVPacketSideData* sideData, int count, int type);
}

internal static unsafe partial class AvFormat
{
    public const string Library = "avformat";

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint avformat_version();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avformat_open_input(AVFormatContext** context, byte* url, AVInputFormat* format, void** options);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avformat_find_stream_info(AVFormatContext* context, void** options);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_find_best_stream(AVFormatContext* context, int type, int wantedStream, int relatedStream, void** decoder, int flags);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void avformat_close_input(AVFormatContext** context);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_read_frame(AVFormatContext* context, AVPacket* packet);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_seek_frame(AVFormatContext* context, int streamIndex, long timestamp, int flags);
}
