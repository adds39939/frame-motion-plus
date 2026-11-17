using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Generators;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>
/// <c>done</c> must be signalled only after the output is written. A consumer stream waits on it and immediately
/// copies the output's last rows, which the generator writes last. The generation is made long (an 8K image, about
/// half a millisecond against a few microseconds for the consumer's copy), so a <c>done</c> signalled before the work
/// lets the consumer read the rows before they are written and see the marker the output was filled with.
/// </summary>
[Collection(SerialGpuDefinition.Name)]
[Trait("Category", "Gpu")]
public class DoneOrderingTests
{
    private const int Size = 8192;
    private const int TailRows = 8;
    private const uint Prev = 0x10101010;
    private const uint Curr = 0x30303030;
    private const uint Marker = 0xA5A5A5A5;

    public static TheoryData<string, bool> Cases => new()
    {
        { "blend", false },
        { "blend", true },
        { "repeat", false },
        { "repeat", true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Done_is_signalled_only_after_the_output_is_written(string kind, bool discontinuity)
    {
        var env = Gpu.Require();
        using var setup = CudaStream.Create(env.Context);
        using var consumer = CudaStream.Create(env.Context, CudaStreamPriority.High);
        using var a = CudaDeviceBuffer.AllocateImage(env.Context, Size, Size, PixelFormat.Rgba8);
        using var b = CudaDeviceBuffer.AllocateImage(env.Context, Size, Size, PixelFormat.Rgba8);
        using var o = CudaDeviceBuffer.AllocateImage(env.Context, Size, Size, PixelFormat.Rgba8);
        using var tail = CudaDeviceBuffer.Allocate(env.Context, (long)TailRows * Size * 4);
        using var done = CudaEvent.Create(env.Context);
        using IFrameGenerator generator = kind == "blend" ? new BlendGenerator(env.Context, env.Kernels) : new RepeatGenerator(env.Context);
        generator.Configure(new FrameGenConfig { Width = Size, Height = Size, Format = PixelFormat.Rgba8 });
        setup.FillFrameAsync(a.AsFrame(), Prev);
        setup.FillFrameAsync(b.AsFrame(), Curr);
        setup.FillFrameAsync(o.AsFrame(), Marker);
        setup.Synchronize();
        if (discontinuity)
        {
            generator.NotifyDiscontinuity();
        }

        generator.Enqueue(a.AsFrame(), b.AsFrame(), 0.25f, o.AsFrame(), done.Signal);
        consumer.Wait(done.Signal);
        consumer.Copy2DAsync(tail.Address, Size * 4L, o.Address + ((ulong)(Size - TailRows) * (ulong)o.PitchBytes), o.PitchBytes, Size * 4L, TailRows);
        var seen = new uint[TailRows * Size];
        consumer.CopyToHost<uint>(tail.Address, seen);

        // t = 0.25: repeat shows prev, blend mixes per channel; a discontinuity shows curr either way.
        var expected = discontinuity ? Curr
            : kind == "repeat" ? Prev
            : 0x01010101u * Reference.Mix(0x10, 0x30, Reference.BlendWeight(0.25f));
        var wrong = seen.Count(p => p != expected);
        if (wrong != 0)
        {
            Assert.Fail($"{wrong} of {seen.Length} pixels the consumer read after done were not yet written (first: 0x{seen.First(p => p != expected):x8})");
        }
    }
}
