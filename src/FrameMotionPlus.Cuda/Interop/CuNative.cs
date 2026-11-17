using System.Runtime.InteropServices;

namespace FrameMotionPlus.Cuda.Interop;

/// <summary>
/// CUDA driver API entry points, named exactly as exported: the versioned symbol cuda.h maps each call to (for
/// example <c>cuMemAlloc</c> is <c>cuMemAlloc_v2</c>). Legacy default-stream semantics, as FFmpeg and the VFX SDK
/// use (no <c>_ptsz</c>/<c>_ptds</c> variants). Only blittable arguments, so the generated stubs marshal nothing.
/// BindingTests checks every name against cuda.h and the installed library.
/// </summary>
internal static unsafe partial class CuNative
{
    // Runs before the first call of any binding below, so the logical library name always resolves.
    static CuNative() => CudaLibrary.EnsureResolver();

    // Initialisation, version, errors
    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuInit(uint flags);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDriverGetVersion(int* driverVersion);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuGetErrorName(CudaResult error, byte** name);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuGetErrorString(CudaResult error, byte** text);

    // Devices
    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDeviceGetCount(int* count);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDeviceGet(int* device, int ordinal);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDeviceGetName(byte* name, int length, int device);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDeviceGetUuid_v2(CuUuid* uuid, int device);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDeviceTotalMem_v2(nuint* bytes, int device);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDeviceGetAttribute(int* value, CudaDeviceAttribute attribute, int device);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDeviceGetPCIBusId(byte* pciBusId, int length, int device);

    // Primary context
    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDevicePrimaryCtxRetain(nint* context, int device);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDevicePrimaryCtxRelease_v2(int device);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDevicePrimaryCtxGetState(int device, uint* flags, int* active);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDevicePrimaryCtxSetFlags_v2(int device, uint flags);

    // Context on the calling thread
    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuCtxSetCurrent(nint context);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuCtxGetCurrent(nint* context);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuCtxPushCurrent_v2(nint context);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuCtxPopCurrent_v2(nint* context);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuCtxGetDevice(int* device);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuCtxSynchronize();

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuCtxGetStreamPriorityRange(int* leastPriority, int* greatestPriority);

    // Streams
    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuStreamCreate(nint* stream, uint flags);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuStreamCreateWithPriority(nint* stream, uint flags, int priority);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuStreamDestroy_v2(nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuStreamSynchronize(nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuStreamQuery(nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuStreamWaitEvent(nint stream, nint cudaEvent, uint flags);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuStreamGetPriority(nint stream, int* priority);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuStreamGetFlags(nint stream, uint* flags);

    // Device and pinned host memory
    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemGetInfo_v2(nuint* free, nuint* total);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemAlloc_v2(ulong* pointer, nuint bytes);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemAllocPitch_v2(ulong* pointer, nuint* pitch, nuint widthInBytes, nuint height, uint elementSizeBytes);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemFree_v2(ulong pointer);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemHostAlloc(void** pointer, nuint bytes, uint flags);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemFreeHost(void* pointer);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemHostRegister_v2(void* pointer, nuint bytes, uint flags);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemHostUnregister(void* pointer);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemHostGetDevicePointer_v2(ulong* devicePointer, void* hostPointer, uint flags);

    // Asynchronous copies and fills
    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemcpyAsync(ulong destination, ulong source, nuint bytes, nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemcpyHtoDAsync_v2(ulong destination, void* source, nuint bytes, nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemcpyDtoHAsync_v2(void* destination, ulong source, nuint bytes, nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemcpyDtoDAsync_v2(ulong destination, ulong source, nuint bytes, nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemcpy2DAsync_v2(CuMemcpy2D* copy, nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemsetD8Async(ulong destination, byte value, nuint count, nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemsetD32Async(ulong destination, uint value, nuint count, nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuMemsetD2D32Async(ulong destination, nuint pitch, uint value, nuint width, nuint height, nint stream);

    // Modules and kernels
    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuModuleLoadData(nint* module, void* image);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuModuleLoadDataEx(nint* module, void* image, uint optionCount, CuJitOption* options, void** optionValues);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuModuleUnload(nint module);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuModuleGetFunction(nint* function, nint module, byte* name);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuFuncGetAttribute(int* value, CuFunctionAttribute attribute, nint function);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuLaunchKernel(
        nint function,
        uint gridDimX,
        uint gridDimY,
        uint gridDimZ,
        uint blockDimX,
        uint blockDimY,
        uint blockDimZ,
        uint sharedMemBytes,
        nint stream,
        void** kernelParams,
        void** extra);

    // Events
    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuEventCreate(nint* cudaEvent, uint flags);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuEventDestroy_v2(nint cudaEvent);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuEventRecord(nint cudaEvent, nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuEventQuery(nint cudaEvent);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuEventSynchronize(nint cudaEvent);

    // cuda.h 12.8 maps cuEventElapsedTime to itself; the _v2 that CUDA 13 headers map to needs an R570+ driver.
    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuEventElapsedTime(float* milliseconds, nint start, nint end);

    // External memory and semaphores
    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuImportExternalMemory(nint* externalMemory, CuExternalMemoryHandleDesc* description);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuExternalMemoryGetMappedBuffer(ulong* pointer, nint externalMemory, CuExternalMemoryBufferDesc* description);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDestroyExternalMemory(nint externalMemory);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuImportExternalSemaphore(nint* externalSemaphore, CuExternalSemaphoreHandleDesc* description);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuSignalExternalSemaphoresAsync(nint* semaphores, CuExternalSemaphoreSignalParams* parameters, uint count, nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuWaitExternalSemaphoresAsync(nint* semaphores, CuExternalSemaphoreWaitParams* parameters, uint count, nint stream);

    [LibraryImport(CudaLibrary.Name)]
    internal static partial CudaResult cuDestroyExternalSemaphore(nint externalSemaphore);
}
