using System.Runtime.InteropServices;
using FrameMotionPlus.Cuda.Interop;

namespace FrameMotionPlus.Cuda;

/// <summary>A CUDA device (<c>CUdevice</c>). Queries go to the driver each time; none is meant for the hot path.</summary>
public readonly unsafe record struct CudaDevice
{
    internal CudaDevice(int ordinal, int handle)
    {
        Ordinal = ordinal;
        Handle = handle;
    }

    public int Ordinal { get; }

    /// <summary>The <c>CUdevice</c> value.</summary>
    public int Handle { get; }

    public string Name
    {
        get
        {
            const int Length = 256;
            var buffer = stackalloc byte[Length];
            Cu.Check(CuNative.cuDeviceGetName(buffer, Length, Handle), nameof(CuNative.cuDeviceGetName));
            return Marshal.PtrToStringUTF8((nint)buffer) ?? string.Empty;
        }
    }

    /// <summary>
    /// The 16-byte device UUID (<c>cuDeviceGetUuid_v2</c>), byte order preserved: <see cref="Guid.ToString()"/> prints
    /// the same digits as <c>nvidia-smi</c> (without its "GPU-" prefix), and Vulkan's
    /// <c>VkPhysicalDeviceIDProperties.deviceUUID</c> compares equal via <c>new Guid(bytes, bigEndian: true)</c>.
    /// </summary>
    public Guid Uuid
    {
        get
        {
            Span<byte> bytes = stackalloc byte[16];
            CopyUuid(bytes);
            return new Guid(bytes, bigEndian: true);
        }
    }

    /// <summary>Writes the raw 16 UUID bytes, in the order the driver (and Vulkan) report them.</summary>
    public void CopyUuid(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, 16, nameof(destination));
        CuUuid uuid;
        Cu.Check(CuNative.cuDeviceGetUuid_v2(&uuid, Handle), nameof(CuNative.cuDeviceGetUuid_v2));
        new ReadOnlySpan<byte>(uuid.Bytes, 16).CopyTo(destination);
    }

    public long TotalMemoryBytes
    {
        get
        {
            nuint bytes;
            Cu.Check(CuNative.cuDeviceTotalMem_v2(&bytes, Handle), nameof(CuNative.cuDeviceTotalMem_v2));
            return (long)bytes;
        }
    }

    /// <summary>Compute capability, e.g. (12, 0) for an RTX 5090.</summary>
    public (int Major, int Minor) ComputeCapability =>
        (GetAttribute(CudaDeviceAttribute.ComputeCapabilityMajor), GetAttribute(CudaDeviceAttribute.ComputeCapabilityMinor));

    /// <summary>PCI bus id in the form <c>0000:01:00.0</c>.</summary>
    public string PciBusId
    {
        get
        {
            const int Length = 64;
            var buffer = stackalloc byte[Length];
            Cu.Check(CuNative.cuDeviceGetPCIBusId(buffer, Length, Handle), nameof(CuNative.cuDeviceGetPCIBusId));
            return Marshal.PtrToStringUTF8((nint)buffer) ?? string.Empty;
        }
    }

    public int GetAttribute(CudaDeviceAttribute attribute)
    {
        int value;
        Cu.Check(CuNative.cuDeviceGetAttribute(&value, attribute, Handle), nameof(CuNative.cuDeviceGetAttribute));
        return value;
    }

    public override string ToString() => $"CUDA device {Ordinal}";
}
