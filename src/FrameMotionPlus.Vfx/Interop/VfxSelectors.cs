using System.Runtime.InteropServices;
using System.Text;

namespace FrameMotionPlus.Vfx.Interop;

/// <summary>
/// The effect and parameter selector strings (nvVideoEffects.h, nvVFXVideoFrameGeneration.h), encoded once as
/// null-terminated UTF-8 in native memory that lives for the process, so a per-frame call passes a pointer and marshals
/// nothing. StructLayoutTests compares each with the header's definition.
/// </summary>
internal static unsafe class VfxSelectors
{
    // nvVFXVideoFrameGeneration.h
    internal static readonly byte* VideoFrameGeneration = Encode("VideoFrameGeneration");
    internal static readonly byte* FrameMultiplier = Encode("FrameMultiplier");
    internal static readonly byte* FrameIndex = Encode("FrameIndex");
    internal static readonly byte* Timestep = Encode("Timestep");
    internal static readonly byte* ShotChange = Encode("ShotChange");
    internal static readonly byte* AutomaticShotChangeDetectionEnabled = Encode("AutomaticShotChangeDetectionEnabled");
    internal static readonly byte* Mode = Encode("Mode");

    // nvVideoEffects.h
    internal static readonly byte* InputImage0 = Encode("SrcImage0");
    internal static readonly byte* InputImage1 = Encode("SrcImage1");
    internal static readonly byte* OutputImage0 = Encode("DstImage0");
    internal static readonly byte* CudaStream = Encode("CudaStream");
    internal static readonly byte* InputWidth = Encode("InputWidth");
    internal static readonly byte* InputHeight = Encode("InputHeight");
    internal static readonly byte* ModelCacheDirectory = Encode("ModelCacheDir");
    internal static readonly byte* ModelCacheMode = Encode("ModelCacheMode");

    /// <summary>Selector name for messages (the pointer's text).</summary>
    internal static string Name(byte* selector) => Marshal.PtrToStringUTF8((nint)selector) ?? string.Empty;

    private static byte* Encode(string text)
    {
        var count = Encoding.UTF8.GetByteCount(text);
        var memory = (byte*)NativeMemory.Alloc((nuint)count + 1);
        Encoding.UTF8.GetBytes(text, new Span<byte>(memory, count));
        memory[count] = 0;
        return memory;
    }
}
