using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Gpu.Tests.Support;
using FrameMotionPlus.Vulkan;
using Reference = FrameMotionPlus.Cuda.Tests.Support.Reference;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>
/// The Phase 2 interop round trip: CUDA draws the test pattern straight into imported ring slots, Vulkan presents them
/// 1:1 to the offscreen target, and the read-back image is compared bit for bit with the CPU reference.
/// </summary>
public class RoundTripTests
{
    public static TheoryData<PixelFormat, int, int, int> Shapes => new()
    {
        // format, width, height, pitch alignment (0 = the presenter's default, 256)
        { PixelFormat.Rgba8, 640, 360, 0 },
        { PixelFormat.Rgb10A2, 640, 360, 0 },
        { PixelFormat.Rgba8, 1277, 719, 0 },     // pitch 5120 for 5108 bytes of pixels
        { PixelFormat.Rgb10A2, 1277, 719, 0 },
        { PixelFormat.Rgba8, 1277, 719, 4 },     // tight pitch 5108: aligned to nothing but the pixel size
        { PixelFormat.Rgb10A2, 1277, 719, 4 },
        { PixelFormat.Rgba8, 333, 97, 12 },      // pitch 1332: a multiple of 12, not of any power of two above 4
        { PixelFormat.Rgb10A2, 2560, 1440, 0 },
        { PixelFormat.Rgba8, 3840, 2160, 0 },
    };

    [Theory]
    [Trait(Category.Name, Category.Gpu)]
    [MemberData(nameof(Shapes))]
    public void Cuda_pattern_in_ring_slots_is_presented_bit_exact(PixelFormat format, int width, int height, int pitchAlignment) =>
        Interop.Run(width, height, format, rig =>
        {
            var ring = rig.Ring;
            Interop.WriteLine($"{width}x{height} {format}: pitch {ring.PitchBytes}, allocation {ring.AllocationSizeBytes} B, {ring.SlotCount} slots");
            if (pitchAlignment == 4)
            {
                Assert.Equal(width * 4, ring.PitchBytes);
            }

            Assert.Equal(format, rig.Target.PixelFormat);
            Assert.Equal(OutputTransform.Passthrough, rig.Presenter.ActiveTransform);

            // Two laps, so every slot is written twice and the second lap waits on 'released' of the first.
            var frames = (2 * ring.SlotCount) + 1;
            for (var i = 0; i < frames; i++)
            {
                var frame = (uint)(1000 + (i * 37));
                var slot = rig.WritePattern(frame);
                Assert.Equal(i % ring.SlotCount, slot.Index);
                Assert.Equal((ulong)(i + 1), slot.ProducerSignal.Value);
                Assert.Equal(i >= ring.SlotCount, !slot.ProducerWait.IsNone);
                rig.Present(slot);
                var pixels = rig.ReadPixels();
                Interop.AssertPixels(Reference.Pattern(width, height, frame, format), pixels, width, $"write {i + 1} (slot {slot.Index})");
                Assert.Equal(frame, Reference.DecodeCounter(pixels, width, height, format));
            }

            Assert.True(rig.Presenter.WaitIdle(2_000_000_000));
            Assert.Equal((ulong)frames, ring.ReadWrittenValue());
            Assert.Equal((ulong)frames, ring.ReadReleasedValue());
            Assert.Equal(0, rig.Presenter.Stats.Dropped);
        }, new RigOptions { Presenter = new VulkanPresenterOptions { PitchAlignment = pitchAlignment == 0 ? 256 : pitchAlignment } });
}
