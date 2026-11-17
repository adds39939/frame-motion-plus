namespace FrameMotionPlus.Core.Frames;

/// <summary>
/// A GPU-resident image. Plain blittable value: copying it copies the description, never pixels.
/// Nothing here owns memory; the source, history or presentation ring that allocated it does.
/// </summary>
public readonly record struct GpuFrame
{
    public MemoryDomain Domain { get; init; }

    /// <summary>Device pointer, mapped host pointer or API buffer handle, as given by <see cref="Domain"/>.</summary>
    public ulong Address { get; init; }

    /// <summary>Byte offset of the first pixel from <see cref="Address"/> (non-zero for sub-allocated buffers).</summary>
    public ulong Offset { get; init; }

    /// <summary>Size in bytes of the addressable allocation backing this image.</summary>
    public long SizeBytes { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Row pitch in bytes (pitch-linear layout).</summary>
    public int PitchBytes { get; init; }

    public PixelFormat Format { get; init; }

    public ColorMetadata Color { get; init; }

    /// <summary>Content timestamp: when this picture first appeared at the source (ns, CLOCK_MONOTONIC).</summary>
    public long ContentTimeNs { get; init; }

    /// <summary>
    /// GPU-side point after which this memory may be used, in the domain of whoever will use it. Any GPU work that
    /// touches the frame waits on it first, GPU-side; the CPU never does. For a frame that will be read it is "the
    /// content is written" (a source's upload and convert, a generator's output); for a frame that will be written
    /// (a ring slot handed to a producer) it is "the previous reader has released it". <see cref="GpuSignal.None"/>
    /// when nothing is pending or when the work is ordered on the same stream anyway.
    /// <para>
    /// A CUDA consumer waits on a <see cref="GpuSignalKind.CudaEvent"/> with <c>cuStreamWaitEvent</c> and on a CUDA
    /// <see cref="GpuSignalKind.TimelineSemaphore"/> with <c>cuWaitExternalSemaphoresAsync</c>.
    /// </para>
    /// <para>
    /// The object behind the signal must outlive this frame and every copy of it (see <see cref="GpuSignal"/>,
    /// "Lifetime"): a frame kept in history must not name an event that has since been disposed.
    /// </para>
    /// </summary>
    public GpuSignal Ready { get; init; }

    public bool IsEmpty => Address == 0;

    public bool HasSameShape(in GpuFrame other) =>
        Width == other.Width && Height == other.Height && Format.Family() == other.Format.Family();
}
