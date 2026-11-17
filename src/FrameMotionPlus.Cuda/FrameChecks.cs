using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Cuda;

/// <summary>
/// Host-side checks before any GPU work touches a frame. A kernel that writes out of bounds corrupts the context for
/// the whole process, so every frame is checked against its stated allocation first. Allocation-free when valid.
/// </summary>
internal static class FrameChecks
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ValidateCudaImage(in GpuFrame frame, string paramName)
    {
        if (!IsValidCudaImage(frame))
        {
            ThrowInvalid(frame, paramName);
        }
    }

    internal static bool IsValidCudaImage(in GpuFrame frame)
    {
        if (frame.Domain is not (MemoryDomain.CudaDevice or MemoryDomain.CudaMappedHost) || frame.Address == 0 ||
            frame.Width <= 0 || frame.Height <= 0 || frame.Format == PixelFormat.Unknown || frame.Format.BytesPerPixel() != 4)
        {
            return false;
        }

        var rowBytes = (long)frame.Width * 4;
        var start = frame.Address + frame.Offset;
        if (frame.PitchBytes < rowBytes || (frame.PitchBytes & 3) != 0 || (start & 3) != 0 || frame.SizeBytes <= 0)
        {
            return false;
        }

        // Bytes touched after Offset: (Height - 1) * Pitch + Width * 4 (at most 2^62, no overflow).
        if (frame.Offset > (ulong)frame.SizeBytes)
        {
            return false;
        }

        var touched = ((long)(frame.Height - 1) * frame.PitchBytes) + rowBytes;
        return (ulong)touched <= (ulong)frame.SizeBytes - frame.Offset;
    }

    [DoesNotReturn]
    private static void ThrowInvalid(in GpuFrame frame, string paramName) =>
        throw new ArgumentException(
            $"Not a usable CUDA image: domain {frame.Domain}, address 0x{frame.Address:x} + {frame.Offset}, {frame.Width}x{frame.Height} " +
            $"{frame.Format}, pitch {frame.PitchBytes}, allocation {frame.SizeBytes} bytes. It must be CUDA device or mapped host memory " +
            "with a 4-byte pixel format, a 4-byte aligned start, a pitch of at least width x 4 and every row inside the allocation.",
            paramName);
}
