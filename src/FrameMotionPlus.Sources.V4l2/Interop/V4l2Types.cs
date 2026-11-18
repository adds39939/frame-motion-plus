using System.Runtime.InteropServices;

namespace FrameMotionPlus.Sources.V4l2.Interop;

// The V4L2 structures this source uses, as /usr/include/linux/videodev2.h lays them out on 64-bit Linux (x86-64 and
// AArch64 have the same layout: LP64, natural alignment). Every size, field offset, constant and ioctl number below is
// checked against the header by StructLayoutTests (a C program compiled against the system's header prints them).
// Only the fields the source reads or writes are declared; Size fixes the rest.

/// <summary><c>struct v4l2_capability</c> (VIDIOC_QUERYCAP).</summary>
[StructLayout(LayoutKind.Explicit, Size = 104)]
internal unsafe struct V4l2Capability
{
    [FieldOffset(0)] public fixed byte Driver[16];
    [FieldOffset(16)] public fixed byte Card[32];
    [FieldOffset(48)] public fixed byte BusInfo[32];
    [FieldOffset(80)] public uint Version;
    [FieldOffset(84)] public uint Capabilities;
    [FieldOffset(88)] public uint DeviceCaps;
}

/// <summary><c>struct v4l2_fmtdesc</c> (VIDIOC_ENUM_FMT).</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal unsafe struct V4l2FmtDesc
{
    [FieldOffset(0)] public uint Index;
    [FieldOffset(4)] public uint Type;
    [FieldOffset(8)] public uint Flags;
    [FieldOffset(12)] public fixed byte Description[32];
    [FieldOffset(44)] public uint PixelFormat;
    [FieldOffset(48)] public uint MbusCode;
}

/// <summary><c>struct v4l2_frmsizeenum</c> (VIDIOC_ENUM_FRAMESIZES); the union holds the discrete or the stepwise sizes.</summary>
[StructLayout(LayoutKind.Explicit, Size = 44)]
internal struct V4l2FrmSizeEnum
{
    [FieldOffset(0)] public uint Index;
    [FieldOffset(4)] public uint PixelFormat;
    [FieldOffset(8)] public uint Type;

    // union { struct v4l2_frmsize_discrete discrete; struct v4l2_frmsize_stepwise stepwise; }
    [FieldOffset(12)] public uint DiscreteWidth;
    [FieldOffset(16)] public uint DiscreteHeight;
    [FieldOffset(12)] public uint MinWidth;
    [FieldOffset(16)] public uint MaxWidth;
    [FieldOffset(20)] public uint StepWidth;
    [FieldOffset(24)] public uint MinHeight;
    [FieldOffset(28)] public uint MaxHeight;
    [FieldOffset(32)] public uint StepHeight;
}

/// <summary><c>struct v4l2_frmivalenum</c> (VIDIOC_ENUM_FRAMEINTERVALS); intervals are fractions of a second.</summary>
[StructLayout(LayoutKind.Explicit, Size = 52)]
internal struct V4l2FrmIvalEnum
{
    [FieldOffset(0)] public uint Index;
    [FieldOffset(4)] public uint PixelFormat;
    [FieldOffset(8)] public uint Width;
    [FieldOffset(12)] public uint Height;
    [FieldOffset(16)] public uint Type;

    // union { struct v4l2_fract discrete; struct v4l2_frmival_stepwise stepwise; }
    [FieldOffset(20)] public uint DiscreteNumerator;
    [FieldOffset(24)] public uint DiscreteDenominator;
    [FieldOffset(20)] public uint MinNumerator;
    [FieldOffset(24)] public uint MinDenominator;
    [FieldOffset(28)] public uint MaxNumerator;
    [FieldOffset(32)] public uint MaxDenominator;
    [FieldOffset(36)] public uint StepNumerator;
    [FieldOffset(40)] public uint StepDenominator;
}

/// <summary><c>struct v4l2_pix_format</c> (the single-planar member of <c>v4l2_format.fmt</c>).</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct V4l2PixFormat
{
    [FieldOffset(0)] public uint Width;
    [FieldOffset(4)] public uint Height;
    [FieldOffset(8)] public uint PixelFormat;
    [FieldOffset(12)] public uint Field;
    [FieldOffset(16)] public uint BytesPerLine;
    [FieldOffset(20)] public uint SizeImage;
    [FieldOffset(24)] public uint Colorspace;
    [FieldOffset(28)] public uint Priv;
    [FieldOffset(32)] public uint Flags;
    [FieldOffset(36)] public uint YcbcrEnc;
    [FieldOffset(40)] public uint Quantization;
    [FieldOffset(44)] public uint XferFunc;
}

/// <summary><c>struct v4l2_format</c> (VIDIOC_G_FMT, S_FMT, TRY_FMT): the type, then a 200-byte union aligned to 8.</summary>
[StructLayout(LayoutKind.Explicit, Size = 208)]
internal struct V4l2Format
{
    [FieldOffset(0)] public uint Type;
    [FieldOffset(8)] public V4l2PixFormat Pix;
}

/// <summary><c>struct v4l2_streamparm</c> with its <c>v4l2_captureparm</c> member (VIDIOC_G_PARM, S_PARM).</summary>
[StructLayout(LayoutKind.Explicit, Size = 204)]
internal struct V4l2StreamParm
{
    [FieldOffset(0)] public uint Type;
    [FieldOffset(4)] public uint Capability;
    [FieldOffset(8)] public uint CaptureMode;
    [FieldOffset(12)] public uint TimePerFrameNumerator;
    [FieldOffset(16)] public uint TimePerFrameDenominator;
    [FieldOffset(20)] public uint ExtendedMode;
    [FieldOffset(24)] public uint ReadBuffers;
}

/// <summary><c>struct v4l2_requestbuffers</c> (VIDIOC_REQBUFS).</summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
internal struct V4l2RequestBuffers
{
    [FieldOffset(0)] public uint Count;
    [FieldOffset(4)] public uint Type;
    [FieldOffset(8)] public uint Memory;
    [FieldOffset(12)] public uint Capabilities;
    [FieldOffset(16)] public byte Flags;
}

/// <summary>
/// <c>struct v4l2_buffer</c> (VIDIOC_QUERYBUF, QBUF, DQBUF). The timestamp is a <c>struct timeval</c> (64-bit seconds and
/// microseconds); <c>m</c> is a union of the MMAP offset and the USERPTR address.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 88)]
internal struct V4l2Buffer
{
    [FieldOffset(0)] public uint Index;
    [FieldOffset(4)] public uint Type;
    [FieldOffset(8)] public uint BytesUsed;
    [FieldOffset(12)] public uint Flags;
    [FieldOffset(16)] public uint Field;
    [FieldOffset(24)] public long TimestampSeconds;
    [FieldOffset(32)] public long TimestampMicroseconds;
    [FieldOffset(56)] public uint Sequence;
    [FieldOffset(60)] public uint Memory;
    [FieldOffset(64)] public uint Offset;
    [FieldOffset(64)] public ulong UserPtr;
    [FieldOffset(72)] public uint Length;
}

/// <summary><c>struct v4l2_control</c> (VIDIOC_G_CTRL, S_CTRL).</summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct V4l2Control
{
    [FieldOffset(0)] public uint Id;
    [FieldOffset(4)] public int Value;
}

/// <summary><c>struct v4l2_input</c> (VIDIOC_ENUMINPUT): its status carries the input's signal state where the driver reports it.</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal unsafe struct V4l2Input
{
    [FieldOffset(0)] public uint Index;
    [FieldOffset(4)] public fixed byte Name[32];
    [FieldOffset(36)] public uint Type;
    [FieldOffset(56)] public uint Status;
    [FieldOffset(60)] public uint Capabilities;
}

/// <summary><c>struct v4l2_event_subscription</c> (VIDIOC_SUBSCRIBE_EVENT).</summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct V4l2EventSubscription
{
    [FieldOffset(0)] public uint Type;
    [FieldOffset(4)] public uint Id;
    [FieldOffset(8)] public uint Flags;
}

/// <summary><c>struct v4l2_event</c> (VIDIOC_DQEVENT); for a source change the union starts with its <c>changes</c> mask.</summary>
[StructLayout(LayoutKind.Explicit, Size = 136)]
internal struct V4l2Event
{
    [FieldOffset(0)] public uint Type;
    [FieldOffset(8)] public uint SourceChanges;
    [FieldOffset(72)] public uint Pending;
    [FieldOffset(76)] public uint Sequence;
}

/// <summary><c>struct pollfd</c>.</summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct PollFd
{
    [FieldOffset(0)] public int Fd;
    [FieldOffset(4)] public short Events;
    [FieldOffset(6)] public short Revents;
}

/// <summary>The ioctl request numbers: <c>_IOR</c>/<c>_IOW</c>/<c>_IOWR('V', nr, type)</c> from the structure sizes above.</summary>
internal static unsafe class V4l2Ioctl
{
    private const uint Write = 1;
    private const uint Read = 2;

    public static readonly nuint QueryCap = Ioc(Read, 0, sizeof(V4l2Capability));
    public static readonly nuint EnumFmt = Ioc(Read | Write, 2, sizeof(V4l2FmtDesc));
    public static readonly nuint GetFmt = Ioc(Read | Write, 4, sizeof(V4l2Format));
    public static readonly nuint SetFmt = Ioc(Read | Write, 5, sizeof(V4l2Format));
    public static readonly nuint ReqBufs = Ioc(Read | Write, 8, sizeof(V4l2RequestBuffers));
    public static readonly nuint QueryBuf = Ioc(Read | Write, 9, sizeof(V4l2Buffer));
    public static readonly nuint QBuf = Ioc(Read | Write, 15, sizeof(V4l2Buffer));
    public static readonly nuint DqBuf = Ioc(Read | Write, 17, sizeof(V4l2Buffer));
    public static readonly nuint StreamOn = Ioc(Write, 18, sizeof(int));
    public static readonly nuint StreamOff = Ioc(Write, 19, sizeof(int));
    public static readonly nuint GetParm = Ioc(Read | Write, 21, sizeof(V4l2StreamParm));
    public static readonly nuint SetParm = Ioc(Read | Write, 22, sizeof(V4l2StreamParm));
    public static readonly nuint EnumInput = Ioc(Read | Write, 26, sizeof(V4l2Input));
    public static readonly nuint GetCtrl = Ioc(Read | Write, 27, sizeof(V4l2Control));
    public static readonly nuint SetCtrl = Ioc(Read | Write, 28, sizeof(V4l2Control));
    public static readonly nuint GetInput = Ioc(Read, 38, sizeof(int));
    public static readonly nuint SetInput = Ioc(Read | Write, 39, sizeof(int));
    public static readonly nuint TryFmt = Ioc(Read | Write, 64, sizeof(V4l2Format));
    public static readonly nuint EnumFrameSizes = Ioc(Read | Write, 74, sizeof(V4l2FrmSizeEnum));
    public static readonly nuint EnumFrameIntervals = Ioc(Read | Write, 75, sizeof(V4l2FrmIvalEnum));
    public static readonly nuint DqEvent = Ioc(Read, 89, sizeof(V4l2Event));
    public static readonly nuint SubscribeEvent = Ioc(Write, 90, sizeof(V4l2EventSubscription));
    public static readonly nuint UnsubscribeEvent = Ioc(Write, 91, sizeof(V4l2EventSubscription));

    private static nuint Ioc(uint direction, uint number, int size) =>
        (nuint)((direction << 30) | ((uint)size << 16) | ((uint)'V' << 8) | number);
}

/// <summary>videodev2.h constants (the subset used).</summary>
internal static class V4l2Defs
{
    public const uint BufTypeVideoCapture = 1;

    public const uint MemoryMmap = 1;
    public const uint MemoryUserPtr = 2;

    public const uint FieldAny = 0;
    public const uint FieldNone = 1;

    public const uint CapVideoCapture = 0x00000001;
    public const uint CapVideoCaptureMplane = 0x00001000;
    public const uint CapMetaCapture = 0x00800000;
    public const uint CapStreaming = 0x04000000;
    public const uint CapDeviceCaps = 0x80000000;

    public const uint FmtFlagCompressed = 0x0001;
    public const uint FmtFlagEmulated = 0x0002;

    public const uint FrmSizeTypeDiscrete = 1;
    public const uint FrmSizeTypeContinuous = 2;
    public const uint FrmSizeTypeStepwise = 3;
    public const uint FrmIvalTypeDiscrete = 1;
    public const uint FrmIvalTypeContinuous = 2;
    public const uint FrmIvalTypeStepwise = 3;

    public const uint CapTimePerFrame = 0x1000;

    public const uint BufFlagMapped = 0x00000001;
    public const uint BufFlagQueued = 0x00000002;
    public const uint BufFlagDone = 0x00000004;
    public const uint BufFlagError = 0x00000040;
    public const uint BufFlagTimestampMask = 0x0000e000;
    public const uint BufFlagTimestampUnknown = 0x00000000;
    public const uint BufFlagTimestampMonotonic = 0x00002000;
    public const uint BufFlagTimestampCopy = 0x00004000;
    public const uint BufFlagTstampSrcMask = 0x00070000;
    public const uint BufFlagTstampSrcEof = 0x00000000;
    public const uint BufFlagTstampSrcSoe = 0x00010000;

    public const uint BufCapSupportsMmap = 0x00000001;
    public const uint BufCapSupportsUserPtr = 0x00000002;
    public const uint BufCapSupportsDmaBuf = 0x00000004;

    public const uint InStNoPower = 0x00000001;
    public const uint InStNoSignal = 0x00000002;
    public const uint InStNoSync = 0x00010000;

    public const uint EventEos = 2;
    public const uint EventSourceChange = 5;
    public const uint EventSrcChResolution = 0x0001;

    // enum v4l2_colorspace
    public const uint ColorspaceDefault = 0;
    public const uint ColorspaceSmpte170M = 1;
    public const uint ColorspaceSmpte240M = 2;
    public const uint ColorspaceRec709 = 3;
    public const uint Colorspace470SystemM = 5;
    public const uint Colorspace470SystemBg = 6;
    public const uint ColorspaceJpeg = 7;
    public const uint ColorspaceSrgb = 8;
    public const uint ColorspaceOpRgb = 9;
    public const uint ColorspaceBt2020 = 10;
    public const uint ColorspaceRaw = 11;
    public const uint ColorspaceDciP3 = 12;

    // enum v4l2_xfer_func
    public const uint XferFuncDefault = 0;
    public const uint XferFunc709 = 1;
    public const uint XferFuncSrgb = 2;
    public const uint XferFuncOpRgb = 3;
    public const uint XferFuncSmpte240M = 4;
    public const uint XferFuncNone = 5;
    public const uint XferFuncDciP3 = 6;
    public const uint XferFuncSmpte2084 = 7;

    // enum v4l2_ycbcr_encoding
    public const uint YcbcrEncDefault = 0;
    public const uint YcbcrEnc601 = 1;
    public const uint YcbcrEnc709 = 2;
    public const uint YcbcrEncXv601 = 3;
    public const uint YcbcrEncXv709 = 4;
    public const uint YcbcrEncSycc = 5;
    public const uint YcbcrEncBt2020 = 6;
    public const uint YcbcrEncBt2020ConstLum = 7;
    public const uint YcbcrEncSmpte240M = 8;

    // enum v4l2_quantization
    public const uint QuantizationDefault = 0;
    public const uint QuantizationFullRange = 1;
    public const uint QuantizationLimRange = 2;

    // Pixel formats (v4l2_fourcc).
    public const uint PixFmtNv12 = 'N' | ('V' << 8) | ('1' << 16) | ('2' << 24);
    public const uint PixFmtP010 = 'P' | ('0' << 8) | ('1' << 16) | ('0' << 24);
    public const uint PixFmtYuyv = 'Y' | ('U' << 8) | ('Y' << 16) | ('V' << 24);
    public const uint PixFmtMjpeg = 'M' | ('J' << 8) | ('P' << 16) | ('G' << 24);
    public const uint PixFmtJpeg = 'J' | ('P' << 8) | ('E' << 16) | ('G' << 24);

    // Controls.
    public const uint CidBase = 0x00980900;
}
