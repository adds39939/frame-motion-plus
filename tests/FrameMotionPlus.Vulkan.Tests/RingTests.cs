using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Tests.Support;

namespace FrameMotionPlus.Vulkan.Tests;

public class RingTests
{
    public static TheoryData<PixelFormat, int, int> Shapes => new()
    {
        { PixelFormat.Rgba8, 1920, 1080 },
        { PixelFormat.Rgb10A2, 2560, 1440 },
        { PixelFormat.Rgba8, 3840, 2160 },
        { PixelFormat.Rgb10A2, 1277, 721 },
        { PixelFormat.Rgba8, 33, 17 },
        { PixelFormat.Rgb10A2, 1, 1 },
    };

    private static bool FdIsOpen(int fd) => new FileInfo($"/proc/self/fd/{fd}").LinkTarget is not null;

    [Fact]
    public void Protocol_maps_writes_to_slots_in_ring_order()
    {
        Assert.Equal(0, RingProtocol.SlotOf(1, 4));
        Assert.Equal(3, RingProtocol.SlotOf(4, 4));
        Assert.Equal(0, RingProtocol.SlotOf(5, 4));
        Assert.Equal(1, RingProtocol.SlotOf(12, 5));
        Assert.Equal(0UL, RingProtocol.ReleasedValueBeforeWrite(4, 4));
        Assert.Equal(1UL, RingProtocol.ReleasedValueBeforeWrite(5, 4));
        Assert.Equal(94UL, RingProtocol.ReleasedValueBeforeWrite(100, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => RingProtocol.SlotOf(0, 4));
    }

    [Fact]
    public void Content_rect_fits_with_bars_and_rounds_to_whole_pixels()
    {
        Assert.Equal(new ContentRect(0, 0, 3840, 2160), ContentRect.Fit(2560, 1440, 3840, 2160));
        Assert.Equal(new ContentRect(0, 218, 1000, 563), ContentRect.Fit(1280, 720, 1000, 1000)); // letterbox
        Assert.Equal(new ContentRect(331, 0, 338, 600), ContentRect.Fit(720, 1280, 1000, 600)); // pillarbox
        Assert.Equal(new ContentRect(440, 0, 2560, 1080), ContentRect.Fit(2560, 1080, 3440, 1080));
        Assert.Equal(new ContentRect(0, 0, 1, 1), ContentRect.Fit(1, 1, 1, 1));
    }

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [MemberData(nameof(Shapes))]
    public void Ring_reports_shape_pitch_sizes_and_valid_export_fds(PixelFormat format, int width, int height) => Gpu.Run(ctx =>
    {
        using var ring = PresentationRing.Create(ctx.Device, new PresentationRingConfig { Width = width, Height = height, Format = format });
        var pitch = (width * 4 + 255) / 256 * 256;
        Assert.Equal(4, ring.SlotCount);
        Assert.Equal(pitch, ring.PitchBytes);
        Assert.Equal((long)pitch * height, ring.ContentSizeBytes);
        Assert.True(ring.AllocationSizeBytes >= ring.ContentSizeBytes);
        Assert.True(ring.DedicatedAllocation);
        Assert.Equal(ctx.Nvidia.Uuid, ring.DeviceUuid);
        Assert.NotEqual(ring.WrittenSemaphoreHandle, ring.ReleasedSemaphoreHandle);

        var handles = new HashSet<ulong>();
        for (var i = 0; i < ring.SlotCount; i++)
        {
            var slot = ring.Slot(i);
            Assert.Equal(i, slot.Index);
            Assert.Equal(width, slot.Width);
            Assert.Equal(height, slot.Height);
            Assert.Equal(pitch, slot.PitchBytes);
            Assert.Equal(format, slot.Format);
            Assert.Equal(ring.ContentSizeBytes, slot.ContentSizeBytes);
            Assert.Equal(ring.AllocationSizeBytes, slot.AllocationSizeBytes);
            Assert.True(handles.Add(slot.BufferHandle));
        }

        var frame = ring.ConsumerFrame(6);
        Assert.Equal(MemoryDomain.Vulkan, frame.Domain);
        Assert.Equal(ring.Slot(1).BufferHandle, frame.Address);
        Assert.Equal(pitch, frame.PitchBytes);
        Assert.Equal(ring.WrittenSignal(6), frame.Ready);
        Assert.Equal(ring.WrittenSemaphoreHandle, ring.WrittenSignal(6).Handle);
        Assert.Equal(6UL, ring.WrittenSignal(6).Value);

        using var export = ring.Export();
        Assert.Equal(4, export.SlotMemory.Count);
        Assert.Equal(pitch, export.PitchBytes);
        Assert.Equal(ring.AllocationSizeBytes, export.AllocationSizeBytes);
        var fds = export.SlotMemory.Select(static f => f.Fd).Append(export.WrittenSemaphore.Fd).Append(export.ReleasedSemaphore.Fd).ToArray();
        Assert.Equal(fds.Length, fds.Distinct().Count());
        Assert.All(fds, static fd => Assert.True(fd >= 0 && FdIsOpen(fd), $"fd {fd} is not open"));
        export.Dispose();
        Assert.All(fds, static fd => Assert.False(FdIsOpen(fd), $"fd {fd} was not closed"));
        Assert.Equal(0UL, ring.ReadWrittenValue());
        Assert.Equal(0UL, ring.ReadReleasedValue());
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Each_export_is_a_new_fd_and_ownership_is_explicit() => Gpu.Run(ctx =>
    {
        using var ring = PresentationRing.Create(ctx.Device, new PresentationRingConfig { Width = 64, Height = 64, Format = PixelFormat.Rgba8, SlotCount = 6 });
        Assert.Equal(6, ring.SlotCount);
        var a = ring.ExportSlotMemory(0);
        var b = ring.ExportSlotMemory(0);
        Assert.NotEqual(a.Fd, b.Fd);
        var aFd = a.Fd;
        a.Dispose();
        Assert.False(FdIsOpen(aFd));
        Assert.Equal(-1, a.Fd);

        // Taken: the caller owns it now and Dispose leaves it alone.
        var bFd = b.Take();
        b.Dispose();
        Assert.True(FdIsOpen(bFd));
        using (new ExportedFd(bFd))
        {
        }

        Assert.False(FdIsOpen(bFd));

        // Transferred (as after a successful import): never closed here.
        using var c = ring.ExportReleasedSemaphore();
        var cFd = c.Fd;
        c.MarkTransferred();
        c.Dispose();
        Assert.True(FdIsOpen(cFd));
        using (new ExportedFd(cFd))
        {
        }
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Invalid_ring_configurations_are_refused() => Gpu.Run(ctx =>
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PresentationRing.Create(ctx.Device, new PresentationRingConfig { SlotCount = 3, Width = 8, Height = 8, Format = PixelFormat.Rgba8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresentationRing.Create(ctx.Device, new PresentationRingConfig { SlotCount = 7, Width = 8, Height = 8, Format = PixelFormat.Rgba8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresentationRing.Create(ctx.Device, new PresentationRingConfig { Width = 0, Height = 8, Format = PixelFormat.Rgba8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresentationRing.Create(ctx.Device, new PresentationRingConfig { Width = 8, Height = 8, Format = PixelFormat.Rgba8, PitchAlignment = 6 }));
        Assert.Throws<NotSupportedException>(() => PresentationRing.Create(ctx.Device, new PresentationRingConfig { Width = 8, Height = 8, Format = PixelFormat.Bgra8 }));
        using var tight = PresentationRing.Create(ctx.Device, new PresentationRingConfig { Width = 7, Height = 3, Format = PixelFormat.Rgba8, PitchAlignment = 4 });
        Assert.Equal(28, tight.PitchBytes);
    });
}
