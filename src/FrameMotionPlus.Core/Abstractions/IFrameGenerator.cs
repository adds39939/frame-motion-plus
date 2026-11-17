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
    /// <b>Content times.</b> Every frame carries a distinct <see cref="GpuFrame.ContentTimeNs"/>, and a pair's increase:
    /// <c>prev.ContentTimeNs &lt; curr.ContentTimeNs</c>. A call whose pair does not is refused
    /// (<see cref="ArgumentException"/>, nothing enqueued): discontinuities are matched by content time
    /// (<see cref="NotifyDiscontinuity"/>), so frames without real times would be misread.
    /// </para>
    /// <para>
    /// <b>Failure.</b> A call that throws does not signal <paramref name="done"/>, and the caller must not wait on it. What
    /// it left behind depends on where it failed. Arguments are checked first, so a call refused for its arguments (and an
    /// <see cref="InvalidOperationException"/> for a generator that is unconfigured, disposed or faulted) enqueued nothing
    /// at all. A call that fails later (a CUDA or SDK error while enqueueing) may already have enqueued its GPU-side waits
    /// on the three <c>Ready</c> signals, and work that partly wrote <paramref name="output"/>: the output's content is
    /// undefined. The pipeline then treats that output as never produced and never shows it as a generated frame. If
    /// <paramref name="done"/> is a write others wait on (a presentation-ring slot), that write must still be completed,
    /// in order (a ring write that is never signalled stalls the ring): the pipeline copies the real frame the slot would
    /// otherwise show (<paramref name="curr"/> for t ≥ 0.5, else <paramref name="prev"/>) into
    /// <paramref name="output"/> and signals <paramref name="done"/> after it, both on the generator's own stream (the
    /// CUDA-domain generators expose it), so the copy is ordered behind anything the failed call enqueued there, the
    /// waits included. A CUDA error that leaves the context unusable (a sticky error) fails that copy too; the pipeline
    /// then stops.
    /// </para>
    /// </summary>
    void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done);

    /// <summary>
    /// Source switch, seek or detected cut: the frame whose content time is <paramref name="contentTimeNs"/> starts a new
    /// shot, so no pair that spans it may be treated as continuous motion. A pair spans it when
    /// <c>prev.ContentTimeNs &lt; contentTimeNs ≤ curr.ContentTimeNs</c>; every call with such a pair outputs a real frame
    /// instead of an interpolation, by one rule for all generators (<see cref="DiscontinuityLatch"/>): <c>prev</c> at
    /// t = 0, <c>curr</c> for every t &gt; 0, so the picture switches to the new shot once and never steps back.
    /// <para>
    /// Because the notification names its frame, it can be given from any thread at any time before the pair across the
    /// cut is enqueued, typically as soon as the source delivers the frame (a <see cref="SourceFrameMarks.Discontinuity"/>
    /// mark): pairs before the cut are not affected, however many are still to be generated. The first pair that starts
    /// at or after the cut (<c>prev.ContentTimeNs ≥ contentTimeNs</c>) ends it, whether a pair across the cut was enqueued
    /// or not (the scheduler never interpolates across a discontinuity it is told about on a <c>FrameArrival</c>; it shows
    /// real frames there instead, and this then leaves nothing armed). A notification given after the pair across the cut
    /// was enqueued applies only to later calls with that pair. Allocation-free; never blocks.
    /// </para>
    /// </summary>
    void NotifyDiscontinuity(long contentTimeNs);
}
