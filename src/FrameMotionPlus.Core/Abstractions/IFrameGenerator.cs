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
    /// <paramref name="output"/>. Signals <paramref name="done"/> when the output is written.
    /// <para>
    /// Input dependencies travel with the frames: the generator's GPU work first waits, GPU-side, on
    /// <paramref name="prev"/>.<see cref="GpuFrame.Ready"/> and <paramref name="curr"/>.<see cref="GpuFrame.Ready"/>
    /// (content written) and on <paramref name="output"/>.<see cref="GpuFrame.Ready"/> (target released).
    /// </para>
    /// <para>
    /// <b>Blocking.</b> Unless <see cref="FrameGenCapabilities.EnqueueMayBlock"/> is set, the call only enqueues and never
    /// waits for the GPU. When it is set, the call may block until the frame has been generated, and that includes
    /// waiting for every <c>Ready</c> above. A pipeline must then call it from a dedicated generator thread: never from
    /// the present or pacing thread, and never from a thread whose own later action is what writes
    /// <paramref name="prev"/> or <paramref name="curr"/> or releases <paramref name="output"/> (it would wait for itself,
    /// for ever). It must also hand the work over early enough for the block to end before the output's display time.
    /// </para>
    /// <para>
    /// <b>Failure.</b> A call that throws produced nothing: the output was not written and <paramref name="done"/> will not
    /// be signalled by the generator. The caller must not wait on it, and must signal it itself if others do (a ring
    /// write that is never signalled stalls the ring).
    /// </para>
    /// </summary>
    void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done);

    /// <summary>
    /// Source switch, seek or detected cut: the next pair must not be treated as continuous motion. "The next pair" is
    /// the (prev, curr) of the next <see cref="Enqueue"/>, identified by its frames (<see cref="DiscontinuityLatch"/>):
    /// every call with that same pair outputs a real frame instead of an interpolation, by one rule for all
    /// generators: <c>prev</c> at t = 0, <c>curr</c> for every t &gt; 0, so the picture switches to the new shot once and
    /// never steps back. The first call with a different pair ends it. Call it right before enqueueing the pair across
    /// the cut. (The scheduler never interpolates across a discontinuity it is told about on a
    /// <c>FrameArrival</c>; it shows real frames there instead. This is for a pair enqueued across a cut regardless.)
    /// </summary>
    void NotifyDiscontinuity();
}
