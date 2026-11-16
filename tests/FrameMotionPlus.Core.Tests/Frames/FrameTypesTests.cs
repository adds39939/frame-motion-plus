using System.Runtime.CompilerServices;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Simulation;
using FrameMotionPlus.Core.Telemetry;

namespace FrameMotionPlus.Core.Tests.Frames;

public class FrameTypesTests
{
    [Fact]
    public void Per_frame_value_types_are_blittable()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<GpuFrame>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<GpuSignal>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ColorMetadata>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<SourceFrame>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<FrameGenConfig>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<FrameGenCapabilities>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<SlotDecision>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<FrameArrival>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<FrameTimelineRecord>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<PresentRequest>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<BridgeSlot>());
    }

    [Fact]
    public void Ten_bit_is_representable_and_families_group_formats()
    {
        Assert.Equal(10, PixelFormat.Rgb10A2.BitsPerColorChannel());
        Assert.Equal(4, PixelFormat.Rgb10A2.BytesPerPixel());
        Assert.Equal(PixelFormatFamily.TenBit, PixelFormat.Rgb10A2.Family());
        Assert.Equal(PixelFormatFamily.EightBit, PixelFormat.Bgra8.Family());
        Assert.Equal(PixelFormat.Rgba8.Family(), PixelFormat.Bgra8.Family());
        Assert.True(PixelFormatSet.All.Contains(PixelFormat.Rgb10A2));
        Assert.False((PixelFormatSet.Rgba8 | PixelFormatSet.Bgra8).Contains(PixelFormat.Rgb10A2));
        Assert.False(PixelFormatSet.All.Contains(PixelFormat.Unknown));
        Assert.Equal(2560 * 4, PixelFormat.Rgba8.MinimumPitch(2560));
    }

    [Fact]
    public void Color_metadata_describes_hdr10_and_sdr()
    {
        Assert.True(ColorMetadata.Hdr10.IsHdr);
        Assert.True(ColorMetadata.Hdr10.Mastering.IsSpecified);
        Assert.True(ColorMetadata.Hdr10.LightLevel.IsSpecified);
        Assert.Equal(ColorPrimaries.Bt2020, ColorMetadata.Hdr10.Primaries);
        Assert.False(ColorMetadata.SdrBt709.IsHdr);
        Assert.False(default(ColorMetadata).Mastering.IsSpecified);
    }

    [Fact]
    public void Shape_check_compares_size_and_format_family()
    {
        var a = new GpuFrame { Width = 1920, Height = 1080, Format = PixelFormat.Rgba8, Address = 1 };
        Assert.True(a.HasSameShape(a with { Format = PixelFormat.Bgra8 }));
        Assert.False(a.HasSameShape(a with { Format = PixelFormat.Rgb10A2 }));
        Assert.False(a.HasSameShape(a with { Width = 1280 }));
        Assert.True(default(GpuFrame).IsEmpty);
        Assert.True(GpuSignal.None.IsNone);
        Assert.Equal(GpuSignalKind.TimelineSemaphore, GpuSignal.Timeline(3, 9).Kind);
        Assert.Equal(MemoryDomain.Unknown, GpuSignal.Timeline(3, 9).Domain);
        Assert.Equal(MemoryDomain.Unknown, GpuSignal.None.Domain);
    }

    [Fact]
    public void Bridge_slot_carries_both_timelines_in_both_domains()
    {
        // As a Linux bridge would hand out slot use k of a 4-slot ring: each exported VkSemaphore has a CUDA handle
        // too, and each side gets the point with its own handle.
        const ulong WrittenCuda = 11, WrittenVulkan = 21, ReleasedCuda = 12, ReleasedVulkan = 22;
        const ulong K = 9, Slots = 4;
        var slot = new BridgeSlot
        {
            Index = (int)(K % Slots),
            ProducerWait = GpuSignal.Timeline(MemoryDomain.CudaDevice, ReleasedCuda, K - Slots),
            ProducerSignal = GpuSignal.Timeline(MemoryDomain.CudaDevice, WrittenCuda, K),
            ConsumerWait = GpuSignal.Timeline(MemoryDomain.Vulkan, WrittenVulkan, K),
            ConsumerSignal = GpuSignal.Timeline(MemoryDomain.Vulkan, ReleasedVulkan, K),
        };

        Assert.Equal(slot.ProducerSignal.Value, slot.ConsumerWait.Value);
        Assert.NotEqual(slot.ProducerSignal.Handle, slot.ConsumerWait.Handle);
        Assert.Equal(MemoryDomain.CudaDevice, slot.ProducerSignal.Domain);
        Assert.Equal(MemoryDomain.Vulkan, slot.ConsumerWait.Domain);
        Assert.Equal(slot.ConsumerSignal.Value - Slots, slot.ProducerWait.Value);
        Assert.Equal(MemoryDomain.Vulkan, slot.ConsumerSignal.Domain);
        Assert.Equal(MemoryDomain.CudaDevice, slot.ProducerWait.Domain);
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<BridgeSlot>());
    }

    [Fact]
    public void Null_generator_validates_inputs()
    {
        using var g = new NullFrameGenerator();
        var f = new GpuFrame { Width = 64, Height = 64, Format = PixelFormat.Rgb10A2, Address = 1 };
        g.Configure(new FrameGenConfig { Width = 64, Height = 64, Format = PixelFormat.Rgb10A2 });
        g.Enqueue(f, f, 0.5f, f, GpuSignal.None);
        Assert.Equal(1, g.EnqueueCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => g.Enqueue(f, f, 0f, f, GpuSignal.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => g.Enqueue(f, f, 1f, f, GpuSignal.None));
        Assert.Throws<ArgumentException>(() => g.Enqueue(f, f with { Format = PixelFormat.Rgba8 }, 0.5f, f, GpuSignal.None));
        g.NotifyDiscontinuity();
        Assert.Equal(1, g.DiscontinuityCount);
        Assert.Equal(8, g.Capabilities.MaxMultiplier);
        Assert.False(g.Capabilities.SupportsScaling);
    }
}
