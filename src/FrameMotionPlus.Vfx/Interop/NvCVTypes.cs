using System.Runtime.InteropServices;

namespace FrameMotionPlus.Vfx.Interop;

// Types, values and layouts of the VFX SDK 1.3 headers (nvCVStatus.h, nvCVImage.h). StructLayoutTests compiles a C
// program against the headers, when they are present, and compares every size, offset and value with these.

/// <summary><c>NvCV_Status</c> (nvCVStatus.h).</summary>
public enum NvCVStatus
{
    Success = 0,
    ErrGeneral = -1,
    ErrUnimplemented = -2,
    ErrMemory = -3,
    ErrEffect = -4,
    ErrSelector = -5,
    ErrBuffer = -6,
    ErrParameter = -7,
    ErrMismatch = -8,
    ErrPixelformat = -9,
    ErrModel = -10,
    ErrLibrary = -11,
    ErrInitialization = -12,
    ErrFile = -13,
    ErrFeaturenotfound = -14,
    ErrMissinginput = -15,
    ErrResolution = -16,
    ErrUnsupportedgpu = -17,
    ErrWronggpu = -18,
    ErrUnsupporteddriver = -19,
    ErrModeldependencies = -20,
    ErrParse = -21,
    ErrModelsubstitution = -22,
    ErrRead = -23,
    ErrWrite = -24,
    ErrParamreadonly = -25,
    ErrTrtEnqueue = -26,
    ErrTrtBindings = -27,
    ErrTrtContext = -28,
    ErrTrtInfer = -29,
    ErrTrtEngine = -30,
    ErrNpp = -31,
    ErrConfig = -32,
    ErrToosmall = -33,
    ErrToobig = -34,
    ErrWrongsize = -35,
    ErrObjectnotfound = -36,
    ErrSingular = -37,
    ErrNothingrendered = -38,
    ErrConvergence = -39,
    ErrEof = -40,
    ErrVersionMismatch = -41,
    ErrCudaValue = -1001,
    ErrCudaMemory = -1002,
    ErrCudaInit = -1003,
    ErrCudaPitch = -1012,
    ErrCudaDriver = -1035,
    ErrCudaKernel = -1209,
    ErrCudaIllegalAddress = -1700,
    ErrCudaLaunch = -1719,
    ErrCudaUnsupported = -1801,
    ErrCuda = -1999,
}

/// <summary><c>NvCVImage_PixelFormat</c> (nvCVImage.h, built without RTX_CAMERA_IMAGE).</summary>
internal enum NvCVImagePixelFormat
{
    FormatUnknown = 0,
    Y = 1,
    A = 2,
    Ya = 3,
    Rgb = 4,
    Bgr = 5,
    Rgba = 6,
    Bgra = 7,
    Argb = 8,
    Abgr = 9,
    Yuv420 = 10,
    Yuv422 = 11,
    Yuv444 = 12,
    Rgb10A2 = 13,
}

/// <summary><c>NvCVImage_ComponentType</c> (nvCVImage.h).</summary>
internal enum NvCVImageComponentType
{
    TypeUnknown = 0,
    U8 = 1,
    U16 = 2,
    S16 = 3,
    F16 = 4,
    U32 = 5,
    S32 = 6,
    F32 = 7,
    U64 = 8,
    S64 = 9,
    F64 = 10,
    P32 = 11,
}

internal static class NvCVConstants
{
    /// <summary><c>NVCV_CHUNKY</c>: all components of a pixel adjacent (the only layout used here).</summary>
    internal const uint Chunky = 0;

    /// <summary><c>NVCV_CPU</c>.</summary>
    internal const uint Cpu = 0;

    /// <summary><c>NVCV_GPU</c> / <c>NVCV_CUDA</c>: the buffer is CUDA memory.</summary>
    internal const uint Gpu = 1;

    /// <summary><c>NVCV_CPU_PINNED</c>.</summary>
    internal const uint CpuPinned = 2;

    /// <summary><c>NVCV_LOG_ERROR</c>.</summary>
    internal const int LogError = 1;

    /// <summary><c>NVCV_LOG_WARNING</c>.</summary>
    internal const int LogWarning = 2;
}

/// <summary>
/// <c>NvCVImage</c>, the SDK's image descriptor (64 bytes on 64-bit Linux and Windows). Here it only ever wraps memory
/// someone else owns (<c>deletePtr</c> stays null), so handing an image to the SDK copies nothing and frees nothing.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NvCVImage
{
    public uint Width;
    public uint Height;
    public int Pitch;
    public NvCVImagePixelFormat PixelFormat;
    public NvCVImageComponentType ComponentType;
    public byte PixelBytes;
    public byte ComponentBytes;
    public byte NumComponents;
    public byte Planar;
    public byte GpuMem;
    public byte Colorspace;
    public fixed byte Reserved[2];
    public void* Pixels;
    public void* DeletePtr;
    public delegate* unmanaged<void*, void> DeleteProc;
    public ulong BufferBytes;
}
