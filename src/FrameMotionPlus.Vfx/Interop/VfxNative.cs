using System.Runtime.InteropServices;

namespace FrameMotionPlus.Vfx.Interop;

/// <summary>
/// The VFX SDK entry points used here (nvVideoEffects.h, nvCVImage.h, nvCVStatus.h). The library names are logical:
/// <see cref="VfxLibraries"/> maps them to the libraries it loaded by absolute path from the SDK directory, so nothing
/// is ever looked up on the system's search path. Every argument is blittable (selectors are pre-encoded UTF-8
/// pointers, see <see cref="VfxSelectors"/>), so the generated stubs marshal nothing and allocate nothing.
/// Callers must have loaded the SDK (<see cref="VfxSdk.EnsureLoaded"/>) first; until then the names do not resolve.
/// <para>
/// The five calls a frame makes go through <see cref="VfxFrameCalls"/> instead: function pointers taken once at load.
/// </para>
/// </summary>
internal static unsafe partial class VfxNative
{
    /// <summary>libVideoFX.so (Linux) / NVVideoEffects.dll (Windows): the NvVFX dispatcher.</summary>
    internal const string Effects = "fmp-nvvfx";

    /// <summary>libNVCVImage.so / NVCVImage.dll: the image descriptor library.</summary>
    internal const string Image = "fmp-nvcvimage";

    /// <summary>
    /// libVideoFXLocal.so (Linux): the local backend behind the dispatcher. Only the logger is configured through it: the
    /// dispatcher's <c>NvVFX_ConfigureLogger</c> also tries to load the Triton backend (libVideoFXTriton.so, which needs a
    /// gRPC client library the SDK does not ship) and prints "Cannot load libVideoFXTriton.so" to stderr when it cannot.
    /// </summary>
    internal const string Local = "fmp-nvvfxlocal";

    static VfxNative() => VfxLibraries.EnsureResolver();

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_GetVersion(uint* version);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_CreateEffect(byte* code, nint* effect);

    [LibraryImport(Effects)]
    internal static partial void NvVFX_DestroyEffect(nint effect);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_SetU32(nint effect, byte* paramName, uint value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_SetS32(nint effect, byte* paramName, int value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_SetF32(nint effect, byte* paramName, float value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_SetF64(nint effect, byte* paramName, double value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_SetU64(nint effect, byte* paramName, ulong value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_SetObject(nint effect, byte* paramName, void* value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_SetString(nint effect, byte* paramName, byte* value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_SetImage(nint effect, byte* paramName, NvCVImage* image);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_SetCudaStream(nint effect, byte* paramName, nint stream);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_GetU32(nint effect, byte* paramName, uint* value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_GetS32(nint effect, byte* paramName, int* value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_GetF32(nint effect, byte* paramName, float* value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_GetF64(nint effect, byte* paramName, double* value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_GetU64(nint effect, byte* paramName, ulong* value);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_GetImage(nint effect, byte* paramName, NvCVImage* image);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_GetCudaStream(nint effect, byte* paramName, nint* stream);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_Load(nint effect);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_Run(nint effect, int async);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_CudaStreamSynchronize(nint stream);

    [LibraryImport(Effects)]
    internal static partial NvCVStatus NvVFX_ConfigureLogger(int verbosity, byte* file, delegate* unmanaged<void*, byte*, void> callback, void* callbackData);

    [LibraryImport(Local, EntryPoint = "NvVFX_ConfigureLogger")]
    internal static partial NvCVStatus NvVFX_ConfigureLoggerLocal(int verbosity, byte* file, delegate* unmanaged<void*, byte*, void> callback, void* callbackData);

    [LibraryImport(Image)]
    internal static partial NvCVStatus NvCVImage_Init(NvCVImage* image, uint width, uint height, int pitch, void* pixels,
        NvCVImagePixelFormat format, NvCVImageComponentType type, uint layout, uint memSpace);

    [LibraryImport(Image)]
    internal static partial byte* NvCV_GetErrorStringFromCode(NvCVStatus code);
}

/// <summary>
/// The SDK calls made per frame (<c>NvVFX_SetU32</c>, <c>NvVFX_SetF32</c>, <c>NvVFX_SetImage</c>, <c>NvVFX_Run</c>,
/// <c>NvCVImage_Init</c>) as unmanaged function pointers resolved once, when the SDK is loaded. Measured in the JIT test
/// host: the runtime binds a <see cref="LibraryImportAttribute"/> P/Invoke again when its stub is re-compiled at a
/// higher tier, about a second into playback, and each binding hands the library resolver a freshly allocated library
/// name on the calling thread (40 + 48 B; the frame loop's zero-allocation check caught it). A call through a function
/// pointer never involves the resolver. NativeAOT binds once and has no tiers; the declarations in
/// <see cref="VfxNative"/> stay the bindings for everything else and for the tests.
/// </summary>
internal static unsafe class VfxFrameCalls
{
    internal static delegate* unmanaged<nint, byte*, uint, NvCVStatus> SetU32;
    internal static delegate* unmanaged<nint, byte*, float, NvCVStatus> SetF32;
    internal static delegate* unmanaged<nint, byte*, NvCVImage*, NvCVStatus> SetImage;
    internal static delegate* unmanaged<nint, int, NvCVStatus> Run;
    internal static delegate* unmanaged<NvCVImage*, uint, uint, int, void*, NvCVImagePixelFormat, NvCVImageComponentType, uint, uint, NvCVStatus> ImageInit;

    /// <summary>Takes the pointers from the loaded libraries; throws <see cref="EntryPointNotFoundException"/> if one is missing.</summary>
    internal static void Resolve(nint effects, nint image)
    {
        SetU32 = (delegate* unmanaged<nint, byte*, uint, NvCVStatus>)NativeLibrary.GetExport(effects, "NvVFX_SetU32");
        SetF32 = (delegate* unmanaged<nint, byte*, float, NvCVStatus>)NativeLibrary.GetExport(effects, "NvVFX_SetF32");
        SetImage = (delegate* unmanaged<nint, byte*, NvCVImage*, NvCVStatus>)NativeLibrary.GetExport(effects, "NvVFX_SetImage");
        Run = (delegate* unmanaged<nint, int, NvCVStatus>)NativeLibrary.GetExport(effects, "NvVFX_Run");
        ImageInit = (delegate* unmanaged<NvCVImage*, uint, uint, int, void*, NvCVImagePixelFormat, NvCVImageComponentType, uint, uint, NvCVStatus>)
            NativeLibrary.GetExport(image, "NvCVImage_Init");
    }
}
