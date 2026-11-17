using System.Runtime.InteropServices;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

[Trait("Category", "Gpu")]
public class MemoryTests
{
    [Fact]
    public void Device_memory_round_trip()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var a = CudaDeviceBuffer.Allocate(env.Context, 1 << 20);
        using var b = CudaDeviceBuffer.Allocate(env.Context, 1 << 20);
        var data = Reference.Noise(1 << 18, 1);
        stream.CopyFromHost<uint>(data, a.Address);
        stream.CopyAsync(b.Address, a.Address, a.SizeBytes);
        var back = new uint[data.Length];
        stream.CopyToHost<uint>(b.Address, back);
        Assert.Equal(data, back);

        stream.MemsetAsync(b.Address + 16, 0x7F, 8);
        stream.CopyToHost<uint>(b.Address, back.AsSpan(0, 8));
        Assert.Equal([data[0], data[1], data[2], data[3], 0x7F7F7F7Fu, 0x7F7F7F7Fu, data[6], data[7]], back[..8]);
    }

    [Fact]
    public unsafe void Pinned_memory_round_trip_is_asynchronous_and_mapped()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var pinned = CudaPinnedBuffer.Allocate(env.Context, 1 << 20);
        using var device = CudaDeviceBuffer.Allocate(env.Context, 1 << 20);
        Assert.NotEqual(0ul, pinned.DeviceAddress);

        var span = pinned.AsSpan<uint>();
        Reference.Noise(span.Length, 2).CopyTo(span);
        var expected = span.ToArray();
        stream.CopyFromHostAsync(device.Address, pinned.HostPointer, pinned.SizeBytes);
        stream.MemsetAsync(pinned.DeviceAddress, 0, pinned.SizeBytes); // through the mapping, ordered after the upload
        stream.CopyToHostAsync(pinned.HostPointer, device.Address, pinned.SizeBytes);
        stream.Synchronize();
        Assert.Equal(expected, pinned.AsSpan<uint>().ToArray());

        using var writeCombined = CudaPinnedBuffer.Allocate(env.Context, 4096, CudaHostAllocOptions.WriteCombined);
        Assert.Equal(0ul, writeCombined.DeviceAddress);
        Assert.Throws<InvalidOperationException>(() => writeCombined.AsFrame(16, 16, PixelFormat.Rgba8, 64));
    }

    [Fact]
    public unsafe void Pinned_memory_is_page_aligned_on_request()
    {
        // The driver sub-allocates pinned memory, so plain allocations are often not page aligned once small ones are
        // interleaved. With an alignment the pointer always is, and the mapping and copies use the aligned bytes.
        var env = Gpu.Require();
        var page = Environment.SystemPageSize;
        using var stream = CudaStream.Create(env.Context);
        var small = new List<CudaPinnedBuffer>();
        try
        {
            for (var i = 0; i < 64; i++)
            {
                small.Add(CudaPinnedBuffer.Allocate(env.Context, 100 + (i * 37)));
                using var aligned = CudaPinnedBuffer.Allocate(env.Context, (1 << 16) + i, alignment: page);
                Assert.Equal(0, (long)aligned.HostPointer % page);
                Assert.Equal((ulong)aligned.HostPointer, aligned.DeviceAddress); // unified addressing
                aligned.AsSpan<byte>().Fill((byte)i);
                stream.MemsetAsync(aligned.DeviceAddress + 1, 0xEE, 1);
                stream.Synchronize();
                Assert.Equal((byte)i, aligned.AsSpan<byte>()[0]);
                Assert.Equal(0xEE, aligned.AsSpan<byte>()[1]);
                Assert.Equal((byte)i, aligned.AsSpan<byte>()[^1]);
            }
        }
        finally
        {
            small.ForEach(b => b.Dispose());
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => CudaPinnedBuffer.Allocate(env.Context, 4096, alignment: 3000));
    }

    [Fact]
    public unsafe void Registered_host_memory_round_trip()
    {
        var env = Gpu.Require();
        const int Bytes = 1 << 20;
        var host = NativeMemory.AlignedAlloc(Bytes, 4096);
        try
        {
            var span = new Span<uint>(host, Bytes / 4);
            Reference.Noise(span.Length, 3).CopyTo(span);
            var expected = span.ToArray();
            using (var registration = CudaHostRegistration.Register(env.Context, host, Bytes))
            using (var stream = CudaStream.Create(env.Context))
            using (var device = CudaDeviceBuffer.Allocate(env.Context, Bytes))
            {
                Assert.NotEqual(0ul, registration.DeviceAddress);
                stream.CopyAsync(device.Address, registration.DeviceAddress, Bytes);
                stream.MemsetAsync(registration.DeviceAddress, 0, Bytes);
                stream.CopyAsync(registration.DeviceAddress, device.Address, Bytes);
                stream.Synchronize();
            }

            Assert.Equal(expected, span.ToArray());
            var again = Assert.Throws<CudaException>(() =>
            {
                using var first = CudaHostRegistration.Register(env.Context, host, Bytes);
                using var second = CudaHostRegistration.Register(env.Context, host, Bytes);
            });
            Assert.Equal(CudaResult.ErrorHostMemoryAlreadyRegistered, again.Result);
        }
        finally
        {
            NativeMemory.AlignedFree(host);
        }
    }

    [Fact]
    public unsafe void Pitched_2d_copies_respect_pitch_offsets_and_padding()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        const int Width = 333, Height = 77;
        using var image = CudaDeviceBuffer.AllocateImage(env.Context, Width, Height, PixelFormat.Rgba8);
        Assert.True(image.PitchBytes >= Width * 4);
        Assert.Equal(0, image.PitchBytes % 4);
        var frame = image.AsFrame();

        // Upload from a padded host layout, read back packed: the padding must not leak through.
        const int HostPitch = (Width * 4) + 52;
        var host = new byte[HostPitch * Height];
        var expected = Reference.Noise(Width * Height, 4);
        for (var y = 0; y < Height; y++)
        {
            MemoryMarshal.AsBytes(expected.AsSpan(y * Width, Width)).CopyTo(host.AsSpan(y * HostPitch));
            host.AsSpan((y * HostPitch) + (Width * 4), 52).Fill(0xEE);
        }

        fixed (byte* p = host)
        {
            stream.Copy2DFromHostAsync(image.Address, image.PitchBytes, p, HostPitch, Width * 4, Height);
        }

        Assert.Equal(expected, env.Read(stream, frame));

        // Device to device into a sub-rectangle of a larger padded image.
        using var big = env.PaddedImage(400, 100, 36, out var bigFrame, PixelFormat.Rgba8);
        var target = bigFrame with { Offset = bigFrame.Offset + (ulong)((10 * bigFrame.PitchBytes) + (20 * 4)), Width = Width, Height = Height };
        stream.CopyFrameAsync(frame, target);
        var whole = env.Read(stream, bigFrame);
        env.AssertGuardsIntact(stream, big, bigFrame);
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 400; x++)
            {
                var inside = x >= 20 && x < 20 + Width && y >= 10 && y < 10 + Height;
                var want = inside ? expected[((y - 10) * Width) + (x - 20)] : 0xA5A5A5A5u;
                Assert.True(want == whole[(y * 400) + x], $"({x}, {y}): expected 0x{want:x8}, got 0x{whole[(y * 400) + x]:x8}");
            }
        }

        // Fill and the frame-level helpers.
        stream.FillFrameAsync(frame, 0x01020304);
        Assert.All(env.Read(stream, frame), p => Assert.Equal(0x01020304u, p));
        stream.CopyFrameFromHost(expected, frame);
        Assert.Equal(expected, env.Read(stream, frame));
    }

    [Fact]
    public void Kernels_write_directly_into_mapped_pinned_memory()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        const int Width = 96, Height = 60;
        using var pinned = CudaPinnedBuffer.Allocate(env.Context, Width * Height * 4);
        var frame = pinned.AsFrame(Width, Height, PixelFormat.Rgba8, Width * 4);
        Assert.Equal(MemoryDomain.CudaMappedHost, frame.Domain);
        env.Kernels.EnqueueTestPattern(stream, frame, 5);
        stream.Synchronize();
        Assert.Equal(Reference.Pattern(Width, Height, 5, PixelFormat.Rgba8), pinned.AsSpan<uint>().ToArray());
    }
}
