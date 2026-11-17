using FrameMotionPlus.Cuda.Interop;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

[Collection(SerialGpuDefinition.Name)]
[Trait("Category", "Gpu")]
public class DriverTests
{
    [Fact]
    public void Devices_are_enumerated_with_name_memory_and_compute_capability()
    {
        Gpu.Require();
        Assert.True(CudaDriver.DriverVersion >= 12000, $"driver CUDA version {CudaDriver.DriverVersion}");
        Assert.True(CudaDriver.DeviceCount >= 1);
        var device = CudaDriver.GetDevice(0);
        Assert.Contains("NVIDIA", device.Name, StringComparison.Ordinal);
        Assert.True(device.TotalMemoryBytes > 1L << 30);
        var (major, minor) = device.ComputeCapability;
        Assert.True(major >= 7, $"compute capability {major}.{minor}");
        Assert.Matches("^[0-9A-Fa-f]{4,8}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}\\.[0-9]$", device.PciBusId);
        Assert.Equal(1, device.GetAttribute(CudaDeviceAttribute.UnifiedAddressing));
        Assert.Equal(1, device.GetAttribute(CudaDeviceAttribute.StreamPrioritiesSupported));
        Assert.Throws<CudaException>(() => CudaDriver.GetDevice(CudaDriver.DeviceCount));
    }

    [Fact]
    public void Device_uuid_matches_nvidia_smi_and_finds_the_device()
    {
        Gpu.Require();
        var device = CudaDriver.GetDevice(0);
        var uuid = device.Uuid;
        Assert.NotEqual(Guid.Empty, uuid);

        Span<byte> raw = stackalloc byte[16];
        device.CopyUuid(raw);
        Span<byte> fromGuid = stackalloc byte[16];
        Assert.True(uuid.TryWriteBytes(fromGuid, bigEndian: true, out _));
        Assert.True(raw.SequenceEqual(fromGuid), "Guid must preserve the driver's byte order (as Vulkan's deviceUUID)");

        Assert.True(CudaDriver.TryFindDevice(uuid, out var found));
        Assert.Equal(device, found);
        Assert.False(CudaDriver.TryFindDevice(Guid.NewGuid(), out _));

        var smi = Tools.Which("nvidia-smi");
        if (smi is not null)
        {
            var (exit, output) = Tools.Run(smi, ["--query-gpu=uuid", "--format=csv,noheader", "-i", "0"]);
            Assert.Equal(0, exit);
            Assert.Equal($"GPU-{uuid}", output.Trim());
        }
    }

    [Fact]
    public unsafe void Primary_context_is_shared_and_current_after_retain()
    {
        var env = Gpu.Require();
        using var second = CudaContext.RetainPrimary(0);
        Assert.Equal(env.Context.Handle, second.Handle);
        Assert.True(second.IsCurrent);

        uint flags;
        int active;
        Assert.Equal(CudaResult.Success, CuNative.cuDevicePrimaryCtxGetState(second.Device.Handle, &flags, &active));
        Assert.Equal(1, active);

        // Another thread starts with no context; any wrapper call binds it there.
        var otherThreadWasCurrent = true;
        var otherThreadAfterUse = false;
        var thread = new Thread(() =>
        {
            otherThreadWasCurrent = second.IsCurrent;
            using var stream = CudaStream.Create(second);
            stream.Synchronize();
            otherThreadAfterUse = second.IsCurrent;
        });
        thread.Start();
        thread.Join();
        Assert.False(otherThreadWasCurrent);
        Assert.True(otherThreadAfterUse);

        var (free, total) = second.MemoryInfo;
        Assert.InRange(free, 1, total);
    }

    [Fact]
    public void Stream_priorities_cover_the_context_range_and_clamp()
    {
        var env = Gpu.Require();
        var (least, greatest) = env.Context.StreamPriorityRange;
        Assert.True(greatest < least, $"range least {least}, greatest {greatest}");

        using var normal = CudaStream.Create(env.Context);
        using var high = CudaStream.Create(env.Context, CudaStreamPriority.High);
        using var clamped = CudaStream.CreateWithPriority(env.Context, greatest - 100);
        using var blocking = CudaStream.Create(env.Context, CudaStreamPriority.Normal, nonBlocking: false);
        Assert.Equal(least, normal.Priority);
        Assert.Equal(greatest, high.Priority);
        Assert.Equal(greatest, clamped.Priority);
        Assert.True(high.IsIdle);
        Assert.NotEqual(0, blocking.Handle);
    }

    [Fact]
    public void Driver_reports_its_library()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "nvcuda.dll" : "libcuda.so.1", CudaDriver.LibraryName);
        var available = CudaDriver.TryInitialize(out var reason);
        Assert.Equal(available, reason is null);
    }
}
