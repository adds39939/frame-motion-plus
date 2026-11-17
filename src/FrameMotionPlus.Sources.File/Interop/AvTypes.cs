using System.Runtime.InteropServices;

namespace FrameMotionPlus.Sources.File.Interop;

// The FFmpeg 9.0 (libavcodec 63, libavformat 63, libavutil 61) structures this project touches, as views: each declares
// only the fields read or written, at their offsets on x86-64 (LP64). FFmpeg allocates every one of them; C# never
// creates, copies or measures one, so a view's own size means nothing (AVPacketSideData, the one array walked, declares
// its stride). FFmpeg only appends public fields within a major version, so these offsets hold for any 63/63/61 minor.
// StructLayoutTests compiles a C program against /usr/include/libav* and checks every offset, constant and function
// prototype here.

/// <summary>Values of FFmpeg enums and macros used here (FFmpeg 9.0).</summary>
internal static class Av
{
    public const int MediaTypeVideo = 0;

    public const int CodecIdH264 = 27;
    public const int CodecIdVp9 = 166;
    public const int CodecIdHevc = 172;
    public const int CodecIdAv1 = 222;

    public const int PixFmtNone = -1;
    public const int PixFmtYuv420P = 0;
    public const int PixFmtYuvJ420P = 12;
    public const int PixFmtNv12 = 23;
    public const int PixFmtYuv420P10Le = 62;
    public const int PixFmtCuda = 117;
    public const int PixFmtP010Le = 158;

    public const int HwDeviceTypeCuda = 2;
    public const int CodecHwConfigMethodHwDeviceCtx = 1;

    /// <summary>AV_CUDA_USE_CURRENT_CONTEXT: the device context takes the calling thread's current CUDA context and never destroys it.</summary>
    public const int CudaUseCurrentContext = 2;

    public const long NoPtsValue = long.MinValue;
    public const int ErrorEof = -541478725;
    public const int ErrorEagain = -11;
    public const int ErrorInvalidData = -1094995529;
    public const int ErrorDecoderNotFound = -1128613112;

    public const int SeekFlagBackward = 1;
    public const int SeekFlagByte = 2;
    public const int AvioSeekableNormal = 1;
    public const int DiscardAll = 48;

    public const int FrameDataMasteringDisplayMetadata = 11;
    public const int FrameDataContentLightLevel = 14;
    public const int PacketDataMasteringDisplayMetadata = 20;
    public const int PacketDataContentLightLevel = 22;

    public const int FrameFlagCorrupt = 1;
    public const int PacketFlagKey = 1;

    /// <summary>AVInputFormat.flags: the format's timestamps may jump (MPEG-TS, MPEG-PS).</summary>
    public const int FormatTsDiscont = 0x200;

    /// <summary>AVInputFormat.flags: the format has no timestamps (elementary streams).</summary>
    public const int FormatNoTimestamps = 0x80;

    /// <summary>AVInputFormat.flags: the format cannot seek by bytes (MP4/MOV).</summary>
    public const int FormatNoByteSeek = 0x8000;

    public const int LogError = 16;

    public const int ColorPrimariesBt709 = 1;
    public const int ColorPrimariesUnspecified = 2;
    public const int ColorPrimariesBt470Bg = 5;
    public const int ColorPrimariesSmpte170M = 6;
    public const int ColorPrimariesSmpte240M = 7;
    public const int ColorPrimariesBt2020 = 9;
    public const int ColorPrimariesSmpte431 = 11;
    public const int ColorPrimariesSmpte432 = 12;

    public const int TransferLinear = 8;
    public const int TransferIec61966_2_1 = 13;
    public const int TransferSmpte2084 = 16;
    public const int TransferAribStdB67 = 18;

    public const int ColorSpaceBt709 = 1;
    public const int ColorSpaceUnspecified = 2;
    public const int ColorSpaceReserved = 3;
    public const int ColorSpaceBt470Bg = 5;
    public const int ColorSpaceSmpte170M = 6;
    public const int ColorSpaceBt2020Ncl = 9;

    public const int ColorRangeJpeg = 2;

    public const int ChromaLocationCenter = 2;
    public const int ChromaLocationTopLeft = 3;
    public const int ChromaLocationTop = 4;
    public const int ChromaLocationBottomLeft = 5;
    public const int ChromaLocationBottom = 6;

    public const int AvCodecMajor = 63;
    public const int AvFormatMajor = 63;
    public const int AvUtilMajor = 61;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVRational
{
    public int Num;
    public int Den;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVFormatContext
{
    [FieldOffset(8)] public AVInputFormat* IFormat;
    [FieldOffset(32)] public AVIOContext* Pb;
    [FieldOffset(44)] public uint NbStreams;
    [FieldOffset(48)] public AVStream** Streams;
    [FieldOffset(96)] public long StartTime;
    [FieldOffset(104)] public long Duration;
    [FieldOffset(112)] public long BitRate;
}

/// <summary>The demuxer's I/O context: whether it can seek (<c>seekable</c>, AVIO_SEEKABLE_* bits; 0 for a pipe).</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct AVIOContext
{
    [FieldOffset(144)] public int Seekable;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVInputFormat
{
    [FieldOffset(0)] public byte* Name;
    [FieldOffset(8)] public byte* LongName;
    [FieldOffset(16)] public int Flags;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVStream
{
    [FieldOffset(8)] public int Index;
    [FieldOffset(16)] public AVCodecParameters* CodecPar;
    [FieldOffset(32)] public AVRational TimeBase;
    [FieldOffset(40)] public long StartTime;
    [FieldOffset(48)] public long Duration;
    [FieldOffset(56)] public long NbFrames;
    [FieldOffset(68)] public int Discard;
    [FieldOffset(88)] public AVRational AvgFrameRate;
    [FieldOffset(204)] public AVRational RFrameRate;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVCodecParameters
{
    [FieldOffset(0)] public int CodecType;
    [FieldOffset(4)] public int CodecId;
    [FieldOffset(32)] public AVPacketSideData* CodedSideData;
    [FieldOffset(40)] public int NbCodedSideData;
    [FieldOffset(44)] public int Format;
    [FieldOffset(60)] public int BitsPerRawSample;
    [FieldOffset(64)] public int Profile;
    [FieldOffset(68)] public int Level;
    [FieldOffset(72)] public int Width;
    [FieldOffset(76)] public int Height;
    [FieldOffset(88)] public AVRational FrameRate;
    [FieldOffset(100)] public int ColorRange;
    [FieldOffset(104)] public int ColorPrimaries;
    [FieldOffset(108)] public int ColorTrc;
    [FieldOffset(112)] public int ColorSpace;
    [FieldOffset(116)] public int ChromaLocation;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVCodecContext
{
    [FieldOffset(24)] public int CodecId;
    [FieldOffset(48)] public void* Opaque;
    [FieldOffset(92)] public AVRational PktTimebase;
    [FieldOffset(112)] public int Width;
    [FieldOffset(116)] public int Height;
    [FieldOffset(120)] public int CodedWidth;
    [FieldOffset(124)] public int CodedHeight;
    [FieldOffset(136)] public int PixFmt;
    [FieldOffset(140)] public int SwPixFmt;

    /// <summary><c>enum AVPixelFormat (*get_format)(struct AVCodecContext *s, const enum AVPixelFormat *fmt)</c>.</summary>
    [FieldOffset(192)] public delegate* unmanaged[Cdecl]<AVCodecContext*, int*, int> GetFormat;
    [FieldOffset(552)] public AVBufferRef* HwFramesCtx;
    [FieldOffset(560)] public AVBufferRef* HwDeviceCtx;
    [FieldOffset(656)] public int ThreadCount;
    [FieldOffset(660)] public int ThreadType;
    [FieldOffset(688)] public int Profile;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVPacket
{
    [FieldOffset(8)] public long Pts;
    [FieldOffset(16)] public long Dts;
    [FieldOffset(32)] public int Size;
    [FieldOffset(36)] public int StreamIndex;
    [FieldOffset(40)] public int Flags;
    [FieldOffset(64)] public long Duration;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVFrame
{
    /// <summary><c>uint8_t *data[8]</c>: for an <c>AV_PIX_FMT_CUDA</c> frame, CUDA device pointers to the planes.</summary>
    [FieldOffset(0)] public fixed ulong Data[8];
    [FieldOffset(64)] public fixed int LineSize[8];
    [FieldOffset(104)] public int Width;
    [FieldOffset(108)] public int Height;
    [FieldOffset(116)] public int Format;
    [FieldOffset(136)] public long Pts;
    [FieldOffset(264)] public AVFrameSideData** SideData;
    [FieldOffset(272)] public int NbSideData;
    [FieldOffset(276)] public int Flags;
    [FieldOffset(280)] public int ColorRange;
    [FieldOffset(284)] public int ColorPrimaries;
    [FieldOffset(288)] public int ColorTrc;
    [FieldOffset(292)] public int ColorSpace;
    [FieldOffset(296)] public int ChromaLocation;
    [FieldOffset(304)] public long BestEffortTimestamp;
    [FieldOffset(320)] public int DecodeErrorFlags;
    [FieldOffset(328)] public AVBufferRef* HwFramesCtx;
    [FieldOffset(344)] public nuint CropTop;
    [FieldOffset(352)] public nuint CropBottom;
    [FieldOffset(360)] public nuint CropLeft;
    [FieldOffset(368)] public nuint CropRight;
    [FieldOffset(408)] public long Duration;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVFrameSideData
{
    [FieldOffset(0)] public int Type;
    [FieldOffset(8)] public byte* Data;
    [FieldOffset(16)] public nuint Size;
}

/// <summary>An element of the <c>coded_side_data</c> array: walked by index, so it declares its C size (24).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal unsafe struct AVPacketSideData
{
    [FieldOffset(0)] public byte* Data;
    [FieldOffset(8)] public nuint Size;
    [FieldOffset(16)] public int Type;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVBufferRef
{
    [FieldOffset(8)] public byte* Data;
    [FieldOffset(16)] public nuint Size;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVHWDeviceContext
{
    [FieldOffset(8)] public int Type;
    [FieldOffset(16)] public void* HwCtx;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVHWFramesContext
{
    [FieldOffset(16)] public AVHWDeviceContext* DeviceCtx;
    [FieldOffset(56)] public int InitialPoolSize;
    [FieldOffset(60)] public int Format;
    [FieldOffset(64)] public int SwFormat;
    [FieldOffset(68)] public int Width;
    [FieldOffset(72)] public int Height;
}

/// <summary><c>AVCUDADeviceContext</c> (libavutil/hwcontext_cuda.h): the hwctx of a CUDA <see cref="AVHWDeviceContext"/>.</summary>
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVCUDADeviceContext
{
    /// <summary><c>CUcontext</c>.</summary>
    [FieldOffset(0)] public nint CudaCtx;

    /// <summary><c>CUstream</c> every CUDA call of the device context and of NVDEC's frame retrieval is made on.</summary>
    [FieldOffset(8)] public nint Stream;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVMasteringDisplayMetadata
{
    /// <summary><c>AVRational display_primaries[3][2]</c>: R, G, B, each (x, y).</summary>
    [FieldOffset(0)] public fixed int DisplayPrimaries[12];

    /// <summary><c>AVRational white_point[2]</c>.</summary>
    [FieldOffset(48)] public fixed int WhitePoint[4];
    [FieldOffset(64)] public AVRational MinLuminance;
    [FieldOffset(72)] public AVRational MaxLuminance;
    [FieldOffset(80)] public int HasPrimaries;
    [FieldOffset(84)] public int HasLuminance;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AVContentLightMetadata
{
    [FieldOffset(0)] public uint MaxCll;
    [FieldOffset(4)] public uint MaxFall;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AVCodecHWConfig
{
    [FieldOffset(0)] public int PixFmt;
    [FieldOffset(4)] public int Methods;
    [FieldOffset(8)] public int DeviceType;
}
