using System.Runtime.CompilerServices;
using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>The Core additions this phase made for input dependencies.</summary>
public class CoreContractTests
{
    [Fact]
    public void Frames_carry_their_ready_signal_and_stay_blittable()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<GpuFrame>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<GpuSignal>());
        var ready = GpuSignal.FromCudaEvent(0x1234);
        Assert.Equal(GpuSignalKind.CudaEvent, ready.Kind);
        Assert.Equal(MemoryDomain.CudaDevice, ready.Domain);
        Assert.Equal(0x1234ul, ready.Handle);
        var frame = new GpuFrame { Width = 4, Height = 4, Format = PixelFormat.Rgba8, Ready = ready };
        Assert.Equal(ready, frame.Ready);
        Assert.True(default(GpuFrame).Ready.IsNone);
        Assert.True(frame.HasSameShape(frame with { Ready = GpuSignal.None }));
    }
}
