using FrameMotionPlus.Cuda.Interop;

namespace FrameMotionPlus.Cuda;

/// <summary>Entry point: loads the driver library, initialises the driver API and enumerates devices.</summary>
public static unsafe class CudaDriver
{
    private static readonly Lock _gate = new();
    private static CudaResult? _initResult;

    /// <summary>The driver library this platform loads: <c>libcuda.so.1</c> or <c>nvcuda.dll</c>.</summary>
    public static string LibraryName => CudaLibrary.FileName;

    /// <summary>True when the driver library loads, <c>cuInit</c> succeeds and at least one device exists.</summary>
    public static bool IsAvailable => TryInitialize(out _) && DeviceCount > 0;

    /// <summary>Loads the driver and calls <c>cuInit</c> once. On failure <paramref name="reason"/> says why.</summary>
    public static bool TryInitialize(out string? reason)
    {
        if (!CudaLibrary.TryLoad(out _))
        {
            reason = $"CUDA driver library '{CudaLibrary.FileName}' not found";
            return false;
        }

        CudaResult result;
        lock (_gate)
        {
            _initResult ??= CuNative.cuInit(0);
            result = _initResult.Value;
        }

        reason = result == CudaResult.Success ? null : CudaErrors.Format(result, nameof(CuNative.cuInit), null);
        return result == CudaResult.Success;
    }

    /// <summary>As <see cref="TryInitialize"/>, but throws a <see cref="CudaException"/> on failure.</summary>
    public static void Initialize()
    {
        if (!TryInitialize(out var reason))
        {
            throw _initResult is { } result && result != CudaResult.Success
                ? new CudaException(result, nameof(CuNative.cuInit))
                : new CudaException(reason ?? "CUDA driver unavailable");
        }
    }

    /// <summary>The driver's CUDA version as 1000 × major + 10 × minor (e.g. 13020 for CUDA 13.2).</summary>
    public static int DriverVersion
    {
        get
        {
            Initialize();
            int version;
            Cu.Check(CuNative.cuDriverGetVersion(&version), nameof(CuNative.cuDriverGetVersion));
            return version;
        }
    }

    public static int DeviceCount
    {
        get
        {
            Initialize();
            int count;
            Cu.Check(CuNative.cuDeviceGetCount(&count), nameof(CuNative.cuDeviceGetCount));
            return count;
        }
    }

    public static CudaDevice GetDevice(int ordinal)
    {
        Initialize();
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        int device;
        Cu.Check(CuNative.cuDeviceGet(&device, ordinal), nameof(CuNative.cuDeviceGet));
        return new CudaDevice(ordinal, device);
    }

    /// <summary>The device whose UUID is <paramref name="uuid"/> (as Vulkan's <c>deviceUUID</c> reports it), if any.</summary>
    public static bool TryFindDevice(Guid uuid, out CudaDevice device)
    {
        var count = DeviceCount;
        for (var i = 0; i < count; i++)
        {
            var candidate = GetDevice(i);
            if (candidate.Uuid == uuid)
            {
                device = candidate;
                return true;
            }
        }

        device = default;
        return false;
    }
}
