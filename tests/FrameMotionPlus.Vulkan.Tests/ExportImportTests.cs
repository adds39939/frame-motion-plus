using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Diagnostics;
using FrameMotionPlus.Vulkan.Tests.Support;

namespace FrameMotionPlus.Vulkan.Tests;

/// <summary>
/// Proves the export path before CUDA exists: a second logical device stands in for the other API and imports the
/// ring's memory and semaphores purely from the exported FDs.
/// </summary>
public class ExportImportTests
{
    private const long Second = 1_000_000_000;

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [InlineData(PixelFormat.Rgba8, 1277, 719)]
    [InlineData(PixelFormat.Rgb10A2, 640, 360)]
    public void Exported_memory_imported_on_another_device_reads_what_was_written(PixelFormat format, int width, int height) => Gpu.Run(ctx =>
    {
        using var ring = PresentationRing.Create(ctx.Device, new PresentationRingConfig { Width = width, Height = height, Format = format });
        var other = ctx.CreateSecondDevice();
        using var export = ring.Export();
        var size = (int)ring.ContentSizeBytes;

        var imported = new ImportedBuffer[ring.SlotCount];
        for (var i = 0; i < ring.SlotCount; i++)
        {
            imported[i] = ctx.Own(BufferIo.ImportBuffer(other, export.SlotMemory[i], ring.ContentSizeBytes, ring.AllocationSizeBytes, ring.MemoryTypeIndex));
            Assert.False(export.SlotMemory[i].IsOwned);
        }

        for (var i = 0; i < ring.SlotCount; i++)
        {
            // Exporter writes, importer reads.
            var written = new byte[size];
            TestPatterns.Fill(written, TestPattern.Ramp, format, width, height, ring.PitchBytes, frame: i);
            BufferIo.Upload(ctx.Device, ring.Buffer(i), written, external: true);
            var read = new byte[size];
            BufferIo.Download(other, imported[i].Buffer, read, external: true);
            Assert.True(written.AsSpan().SequenceEqual(read), $"slot {i}: importer read different bytes");

            // Importer writes, exporter reads.
            TestPatterns.Fill(written, TestPattern.Smooth, format, width, height, ring.PitchBytes, frame: 0);
            written[0] ^= 0x5A;
            BufferIo.Upload(other, imported[i].Buffer, written, external: true);
            BufferIo.Download(ctx.Device, ring.Buffer(i), read, external: true);
            Assert.True(written.AsSpan().SequenceEqual(read), $"slot {i}: exporter read different bytes");
        }
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Timeline_semaphores_round_trip_in_both_directions() => Gpu.Run(ctx =>
    {
        using var ring = PresentationRing.Create(ctx.Device, new PresentationRingConfig { Width = 16, Height = 16, Format = PixelFormat.Rgba8 });
        var other = ctx.CreateSecondDevice();
        using var export = ring.Export();
        var written = ctx.Own(BufferIo.ImportTimeline(other, export.WrittenSemaphore)).Semaphore;
        var released = ctx.Own(BufferIo.ImportTimeline(other, export.ReleasedSemaphore)).Semaphore;
        Assert.False(export.WrittenSemaphore.IsOwned);

        // Host signals cross over both ways.
        BufferIo.HostSignal(other, written, 1);
        Assert.True(ring.WaitWritten(1, Second));
        Assert.Equal(1UL, ring.ReadWrittenValue());
        BufferIo.HostSignal(ctx.Device, ring.Released, 1);
        Assert.True(BufferIo.WaitCounter(other, released, 1, Second));

        // Queue signals and waits cross over both ways: the importer (as producer) waits GPU-side for released ≥ 2,
        // then signals written = 2; the exporter (as presenter) waits for written ≥ 2, then signals released = 3.
        BufferIo.SubmitWaitSignal(other, released, 2, written, 2);
        BufferIo.SubmitWaitSignal(ctx.Device, ring.Written, 2, ring.Released, 3);
        Thread.Sleep(50);
        Assert.Equal(1UL, ring.ReadWrittenValue()); // nothing moves until released reaches 2
        BufferIo.HostSignal(ctx.Device, ring.Released, 2);
        Assert.True(ring.WaitReleased(3, Second), "the exporter's queue never saw written = 2");
        Assert.Equal(2UL, BufferIo.Counter(other, written));
        Assert.True(BufferIo.WaitCounter(other, released, 3, Second));
        ctx.Device.WaitIdle();
        other.WaitIdle();
    });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Producer_honours_released_before_reusing_a_slot() => Gpu.Run(ctx =>
    {
        using var rig = OffscreenRig.Create(ctx, 320, 180, PixelFormat.Rgba8);
        var producer = rig.Producer;
        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal((ulong)i, producer.WritePattern(TestPattern.Ramp, i));
        }

        Assert.True(producer.WaitWritten(4, Second));

        // Write 5 reuses slot 0, so it must wait (GPU-side) for released ≥ 1, which only the presenter gives.
        Assert.Equal(5UL, producer.WritePattern(TestPattern.Ramp, 5));
        Thread.Sleep(100);
        Assert.Equal(4UL, producer.ReadWrittenValue());
        Assert.Equal(0UL, producer.ReadReleasedValue());

        Assert.True(rig.Presenter.TryPresent(rig.Request(1)));
        Assert.True(producer.WaitReleased(1, Second), "release did not reach the producer's device");
        Assert.True(producer.WaitWritten(5, Second), "write 5 did not proceed after the release");

        // The presenter shows write 5 (writes 2 to 4 are dropped by skipping, and released with it).
        rig.WaitReady();
        Assert.True(rig.Presenter.TryPresent(rig.Request(5)));
        Assert.True(producer.WaitReleased(5, Second));
        var pixels = rig.ReadPixels();
        Assert.Equal(TestPatterns.Pixel(TestPattern.Ramp, PixelFormat.Rgba8, 17, 9, 320, 180, 5), pixels[(9 * 320) + 17]);
    });
}
