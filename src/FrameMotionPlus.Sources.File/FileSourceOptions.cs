using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Platform.Threading;

namespace FrameMotionPlus.Sources.File;

/// <summary>Options of a <see cref="FileFrameSource"/>.</summary>
public sealed record FileSourceOptions
{
    /// <summary>Frames decoded and converted ahead of the consumer (the bounded queue): 4.</summary>
    public int DecodeAhead { get; init; } = 4;

    /// <summary>Frames the consumer may hold at once (prev, curr and the next one): 3.</summary>
    public int HistoryDepth { get; init; } = 3;

    /// <summary>At the end of the file, start again (the first frame of each new lap is a discontinuity).</summary>
    public bool Loop { get; init; }

    /// <summary>
    /// Added to every content time: the <c>CLOCK_MONOTONIC</c> time at which the file's first frame counts as having
    /// appeared (the pipeline maps media time onto its clock). 0: content times are media time from the first frame.
    /// </summary>
    public long ContentTimeOriginNs { get; init; }

    /// <summary>Keep a copy of each frame's decoded NV12/P010 planes with it (<see cref="FileFrameSource.TryGetPlanes"/>), for verification.</summary>
    public bool KeepDecodedPlanes { get; init; }

    /// <summary>
    /// The stream FFmpeg and NVDEC work on (the design's copy stream). Null: the source creates one. A stream given
    /// here must be the source's alone while it runs (FFmpeg's internal calls synchronise it).
    /// </summary>
    public CudaStream? CopyStream { get; init; }

    /// <summary>The stream the conversion runs on (the design's compute stream). Null: the source creates one.</summary>
    public CudaStream? ComputeStream { get; init; }

    /// <summary>The kernels to convert with (loaded in the same context). Null: the source loads its own.</summary>
    public ImageKernels? Kernels { get; init; }

    /// <summary>How the decode thread is scheduled.</summary>
    public ThreadSchedulingRequest DecodeThread { get; init; } = ThreadSchedulingRequest.Default;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(DecodeAhead, 1, nameof(DecodeAhead));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DecodeAhead, 32, nameof(DecodeAhead));
        ArgumentOutOfRangeException.ThrowIfLessThan(HistoryDepth, 2, nameof(HistoryDepth));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HistoryDepth, 16, nameof(HistoryDepth));
        ArgumentNullException.ThrowIfNull(DecodeThread, nameof(DecodeThread));
    }
}

/// <summary>What one delivered frame cost (the decode thread's measurements; read with <see cref="FileFrameSource.TryReadTiming"/>).</summary>
public readonly record struct DecodeTiming
{
    public long FrameId { get; init; }

    public long ContentTimeNs { get; init; }

    /// <summary>Media time: ns after the stream's start in this lap.</summary>
    public long MediaTimeNs { get; init; }

    /// <summary>Host time in FFmpeg for this frame on the decode thread (demux, NVDEC submission, map, copy enqueue).</summary>
    public long HostDecodeNs { get; init; }

    /// <summary>
    /// GPU span on the copy stream from just before the decode call to just after it: NVDEC's output for the frame being
    /// mapped and copied into the frame pool, including any time the stream sat idle while the host was in FFmpeg.
    /// </summary>
    public long DecodeCopyGpuNs { get; init; }

    /// <summary>GPU time of the YCbCr-to-RGB conversion.</summary>
    public long ConvertGpuNs { get; init; }

    /// <summary>Frames waiting for the consumer when this one was published (including it).</summary>
    public int QueueDepth { get; init; }

    public SourceFrameMarks Marks { get; init; }

    /// <summary>Why the frame is marked (a loop, a seek, a jump in the stream's timestamps), and a repaired timestamp.</summary>
    public FileFrameEvents Events { get; init; }
}

/// <summary>What a frame of a file begins or had repaired (the reasons behind <see cref="SourceFrameMarks.Discontinuity"/>).</summary>
[Flags]
public enum FileFrameEvents
{
    None = 0,

    /// <summary>The first frame of a new lap: the file restarted at its beginning.</summary>
    Loop = 1,

    /// <summary>The first frame after a seek.</summary>
    Seek = 2,

    /// <summary>
    /// The stream's own timestamps jump here (concatenated recordings, a reset or wrapped clock, a gap of more than
    /// 10 s): nothing may interpolate across it. A jump back continues one interval after the previous frame; a gap
    /// forward keeps its time in a container with a continuous timeline and is collapsed to one interval in MPEG-TS/PS
    /// (<see cref="ContentClock"/>, <see cref="FileStreamInfo.DiscontinuousTimestamps"/>).
    /// </summary>
    TimestampJump = 4,

    /// <summary>Its timestamp did not advance (an isolated collision) and was moved 1 ns after the previous frame's.</summary>
    TimestampRepaired = 8,

    /// <summary>A seek was asked for and could not be made; the frames queued before it were dropped and decoding went on.</summary>
    SeekFailed = 16,
}

/// <summary>A held frame's place in the file and how it was converted (<see cref="FileFrameSource.TryGetFrameInfo"/>).</summary>
public readonly record struct FileFrameInfo
{
    /// <summary>ns after the stream's start in this lap, from its timestamp.</summary>
    public long MediaTimeNs { get; init; }

    /// <summary>Laps completed before it (0 in the first pass through the file).</summary>
    public long Lap { get; init; }

    public FileFrameEvents Events { get; init; }

    /// <summary>The YCbCr-to-RGB conversion it was converted with (the source's matrix, range and siting at that frame).</summary>
    public YuvConversion Conversion { get; init; }
}

/// <summary>Counters of a <see cref="FileFrameSource"/> (a snapshot; any thread).</summary>
public readonly record struct FileSourceStatistics
{
    public long FramesDecoded { get; init; }

    public long FramesDelivered { get; init; }

    /// <summary>Frames decoded after a seek and dropped before its target.</summary>
    public long FramesSkippedBySeek { get; init; }

    public long Loops { get; init; }

    public long Seeks { get; init; }

    /// <summary>Isolated timestamp collisions, each moved 1 ns after the previous frame (never two in a row).</summary>
    public long PtsRepairs { get; init; }

    public long PtsMissing { get; init; }

    /// <summary>Jumps in the stream's own timestamps (back, or forward by more than 10 s), each marked (see <see cref="FileFrameEvents.TimestampJump"/>).</summary>
    public long TimestampDiscontinuities { get; init; }

    /// <summary>Frames from before a seek or loop that the decoder still returned after it, and were dropped.</summary>
    public long StaleFramesDropped { get; init; }

    /// <summary>
    /// Seeks and loops made from the first byte of the file: an elementary stream (no time index), and MPEG-TS/PS when
    /// the target is at or before the start or a keyframe search reaches it (their time seeks do not land on keyframes).
    /// </summary>
    public long SeeksFromStart { get; init; }

    /// <summary>Seeks and loops the demuxer could not make at all (<see cref="FileFrameSource.SeekError"/>).</summary>
    public long FailedSeeks { get; init; }

    /// <summary>FFmpeg frame-pool buffers that appeared after start-up (CUDA allocations during playback).</summary>
    public long PoolGrowth { get; init; }

    public long CorruptFrames { get; init; }

    public long CorruptPackets { get; init; }

    /// <summary>Times the decode thread found no free history buffer and waited for the consumer.</summary>
    public long BackPressureWaits { get; init; }

    public int MaxQueueDepth { get; init; }

    /// <summary>Managed bytes the decode thread allocated per frame after its first frames (should be 0).</summary>
    public long DecodeThreadAllocatedBytes { get; init; }

    public long DroppedTimings { get; init; }
}
