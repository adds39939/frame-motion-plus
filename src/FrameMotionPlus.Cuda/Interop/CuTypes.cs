using System.Runtime.InteropServices;

namespace FrameMotionPlus.Cuda.Interop;

// Driver API types, values and layouts as in cuda.h (CUDA 12.8). Handles are nint, CUdeviceptr is ulong (64-bit
// only), size_t is nuint. StructLayoutTests compiles a C program against cuda.h and compares every size and offset.

internal enum CuMemoryType
{
    Host = 0x01,
    Device = 0x02,
    Array = 0x03,
    Unified = 0x04,
}

internal enum CuJitOption
{
    MaxRegisters = 0,
    WallTime = 2,
    InfoLogBuffer = 3,
    InfoLogBufferSizeBytes = 4,
    ErrorLogBuffer = 5,
    ErrorLogBufferSizeBytes = 6,
    OptimizationLevel = 7,
    TargetFromCucontext = 8,
    LogVerbose = 12,
}

internal enum CuFunctionAttribute
{
    MaxThreadsPerBlock = 0,
    SharedSizeBytes = 1,
    NumRegs = 4,
    PtxVersion = 5,
    BinaryVersion = 6,
}

internal enum CuExternalMemoryHandleType
{
    OpaqueFd = 1,
    OpaqueWin32 = 2,
    OpaqueWin32Kmt = 3,
}

internal enum CuExternalSemaphoreHandleType
{
    OpaqueFd = 1,
    OpaqueWin32 = 2,
    OpaqueWin32Kmt = 3,
    TimelineSemaphoreFd = 9,
    TimelineSemaphoreWin32 = 10,
}

internal static class CuConstants
{
    internal const uint StreamDefault = 0x0;
    internal const uint StreamNonBlocking = 0x1;

    internal const uint EventDefault = 0x0;
    internal const uint EventBlockingSync = 0x1;
    internal const uint EventDisableTiming = 0x2;

    internal const uint MemHostAllocPortable = 0x01;
    internal const uint MemHostAllocDeviceMap = 0x02;
    internal const uint MemHostAllocWriteCombined = 0x04;

    internal const uint MemHostRegisterPortable = 0x01;
    internal const uint MemHostRegisterDeviceMap = 0x02;
    internal const uint MemHostRegisterIoMemory = 0x04;
    internal const uint MemHostRegisterReadOnly = 0x08;

    internal const uint ExternalMemoryDedicated = 0x1;

    internal const uint CtxSchedAuto = 0x00;
    internal const uint CtxSchedSpin = 0x01;
    internal const uint CtxSchedYield = 0x02;
    internal const uint CtxSchedBlockingSync = 0x04;
}

/// <summary>CUuuid.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CuUuid
{
    public fixed byte Bytes[16];
}

/// <summary>CUDA_MEMCPY2D (CUDA_MEMCPY2D_v2).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CuMemcpy2D
{
    public nuint SrcXInBytes;
    public nuint SrcY;
    public CuMemoryType SrcMemoryType;
    public void* SrcHost;
    public ulong SrcDevice;
    public nint SrcArray;
    public nuint SrcPitch;

    public nuint DstXInBytes;
    public nuint DstY;
    public CuMemoryType DstMemoryType;
    public void* DstHost;
    public ulong DstDevice;
    public nint DstArray;
    public nuint DstPitch;

    public nuint WidthInBytes;
    public nuint Height;
}

/// <summary>The handle union shared by the external memory and external semaphore handle descriptors.</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct CuExternalHandle
{
    [FieldOffset(0)]
    public int Fd;

    [FieldOffset(0)]
    public nint Win32Handle;

    [FieldOffset(8)]
    public nint Win32Name;

    [FieldOffset(0)]
    public nint NvSciObject;
}

/// <summary>CUDA_EXTERNAL_MEMORY_HANDLE_DESC (_v1).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CuExternalMemoryHandleDesc
{
    public CuExternalMemoryHandleType Type;
    public CuExternalHandle Handle;
    public ulong Size;
    public uint Flags;
    public fixed uint Reserved[16];
}

/// <summary>CUDA_EXTERNAL_MEMORY_BUFFER_DESC (_v1).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CuExternalMemoryBufferDesc
{
    public ulong Offset;
    public ulong Size;
    public uint Flags;
    public fixed uint Reserved[16];
}

/// <summary>CUDA_EXTERNAL_SEMAPHORE_HANDLE_DESC (_v1).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CuExternalSemaphoreHandleDesc
{
    public CuExternalSemaphoreHandleType Type;
    public CuExternalHandle Handle;
    public uint Flags;
    public fixed uint Reserved[16];
}

/// <summary>
/// CUDA_EXTERNAL_SEMAPHORE_SIGNAL_PARAMS (_v1), with the nested <c>params</c> struct flattened: fence.value,
/// the nvSciSync union, keyedMutex.key, then params.reserved[12].
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CuExternalSemaphoreSignalParams
{
    public ulong FenceValue;
    public ulong NvSciSyncFence;
    public ulong KeyedMutexKey;
    public fixed uint ParamsReserved[12];
    public uint Flags;
    public fixed uint Reserved[16];
}

/// <summary>
/// CUDA_EXTERNAL_SEMAPHORE_WAIT_PARAMS (_v1), flattened like the signal parameters. The nested keyedMutex struct
/// (key, timeoutMs) is padded to 16 bytes in C, hence <see cref="KeyedMutexPadding"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CuExternalSemaphoreWaitParams
{
    public ulong FenceValue;
    public ulong NvSciSyncFence;
    public ulong KeyedMutexKey;
    public uint KeyedMutexTimeoutMs;
    public uint KeyedMutexPadding;
    public fixed uint ParamsReserved[10];
    public uint Flags;
    public fixed uint Reserved[16];
}
