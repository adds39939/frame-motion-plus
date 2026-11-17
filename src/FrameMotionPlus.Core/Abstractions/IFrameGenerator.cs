using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.Core.Abstractions;

/// <summary>
/// Makes the frame at timestep t between two real frames. The scheduler decides when and at which t;
/// a generator only answers "make the frame at t between these two".
/// </summary>
public interface IFrameGenerator : IDisposable
{
    FrameGenCapabilities Capabilities { get; }

    /// <summary>Sets size, format and quality. May rebuild engines off-thread; never called per frame.</summary>
    void Configure(in FrameGenConfig config);

    /// <summary>
    /// GPU-async: enqueues generation of the frame at <paramref name="t"/> (0 = prev, 1 = curr) into
    /// <paramref name="output"/> and never waits. Signals <paramref name="done"/> when the output is written.
    /// <para>
    /// Input dependencies travel with the frames: the generator's GPU work first waits, GPU-side, on
    /// <paramref name="prev"/>.<see cref="GpuFrame.Ready"/> and <paramref name="curr"/>.<see cref="GpuFrame.Ready"/>
    /// (content written) and on <paramref name="output"/>.<see cref="GpuFrame.Ready"/> (target released).
    /// </para>
    /// </summary>
    void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done);

    /// <summary>Source switch, seek or detected cut: the next pair must not be treated as continuous motion.</summary>
    void NotifyDiscontinuity();
}
