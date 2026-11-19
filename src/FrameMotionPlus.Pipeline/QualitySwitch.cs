using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>One quality switch the pacing thread committed (the generator thread switches between two writes after it).</summary>
public readonly record struct QualityCommit
{
    /// <summary>The switch's number (1 for the first): the generator thread tags its generation times with it.</summary>
    public int Epoch { get; init; }

    public FrameGenQuality From { get; init; }

    public FrameGenQuality To { get; init; }

    /// <summary>When the pacing thread saw the request and started preparing it.</summary>
    public long RequestedNs { get; init; }

    /// <summary>When it committed: D was ready for the new cost (or the wait gave up), the switch was queued.</summary>
    public long CommittedNs { get; init; }

    /// <summary>The scheduler's D when the request was seen and at the commit.</summary>
    public long DelayAtRequestNs { get; init; }

    public long DelayAtCommitNs { get; init; }

    /// <summary>The generation time the scheduler's estimate started again from.</summary>
    public long EstimateNs { get; init; }

    /// <summary>True when D had not reached what the new cost needs within <see cref="QualitySwitchControl.MaxPrepareNs"/>.</summary>
    public bool GaveUpWaiting { get; init; }
}

/// <summary>
/// Live quality switching between two generator qualities whose instances are both loaded (Q in the window). The pacing
/// thread owns the decision: a request from any thread (<see cref="Toggle"/>) is prepared on the scheduler first
/// (<see cref="FrameScheduler.PrepareGenerationTimeChange"/>: for a dearer quality D rises at its normal slew before
/// anything changes), then committed (the scheduler's generation-time estimate starts again from a figure for the new
/// quality, and the caller queues a <see cref="WriteKind.Quality"/> job behind every write already handed out), so the
/// slots decided with the old lead are generated at the old quality and every slot decided after the commit at the new
/// one. Rapid requests follow the latest wish: one not yet committed is called off when the wish goes back. Allocation-free
/// after construction.
/// </summary>
public sealed class QualitySwitchControl
{
    /// <summary>The longest a dearer quality waits for D before it is committed anyway.</summary>
    public const long MaxPrepareNs = Nanoseconds.PerSecond;

    /// <summary>Commits kept for the report (later ones are counted only).</summary>
    public const int LogCapacity = 256;

    private readonly long[] _seed = new long[4];
    private readonly long[] _measured = new long[4];
    private readonly QualityCommit[] _log = new QualityCommit[LogCapacity];
    private int _desired;
    private long _requests;
    private int _committed;
    private int _epoch;
    private int _logCount;
    private long _commits;
    private long _cancels;
    private long _gaveUp;

    // Pacing thread only.
    private FrameGenQuality _target;
    private bool _pending;
    private long _pendingSinceNs;
    private long _delayAtRequestNs;
    private long _estimateNs;

    /// <param name="start">The quality playback starts at (its instance active).</param>
    /// <param name="other">The other loaded quality.</param>
    /// <param name="seed">The generation time to start the scheduler's estimate from at each quality (about its p99).</param>
    public QualitySwitchControl(FrameGenQuality start, FrameGenQuality other, Func<FrameGenQuality, long> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (start == other || !IsQuality(start) || !IsQuality(other))
        {
            throw new ArgumentException($"Two different qualities are needed, got {start} and {other}.");
        }

        Start = start;
        Other = other;
        _seed[(int)start] = Math.Max(0, seed(start));
        _seed[(int)other] = Math.Max(0, seed(other));
        _desired = (int)start;
        _committed = (int)start;
    }

    public FrameGenQuality Start { get; }

    public FrameGenQuality Other { get; }

    /// <summary>What the latest request asks for.</summary>
    public FrameGenQuality Desired => (FrameGenQuality)Volatile.Read(ref _desired);

    /// <summary>The quality the slots decided now are generated at (committed).</summary>
    public FrameGenQuality Committed => (FrameGenQuality)Volatile.Read(ref _committed);

    /// <summary>Commits so far: generation times tagged with an older epoch belong to the quality before.</summary>
    public int Epoch => Volatile.Read(ref _epoch);

    /// <summary>A switch is being prepared (pacing thread).</summary>
    public bool Pending => _pending;

    public long Requests => Interlocked.Read(ref _requests);

    public long Commits => Interlocked.Read(ref _commits);

    /// <summary>Prepared switches called off because the wish went back before they were committed.</summary>
    public long Cancels => Interlocked.Read(ref _cancels);

    /// <summary>Dearer switches committed without D at what they need (the wait gave up).</summary>
    public long GaveUp => Interlocked.Read(ref _gaveUp);

    /// <summary>The commits kept (the first <see cref="LogCapacity"/>); any thread, each entry written before it is counted.</summary>
    public ReadOnlySpan<QualityCommit> Log => _log.AsSpan(0, Volatile.Read(ref _logCount));

    /// <summary>The commit with <paramref name="epoch"/>, if kept.</summary>
    public bool TryGetCommit(int epoch, out QualityCommit commit)
    {
        var log = Log;
        if (epoch >= 1 && epoch <= log.Length)
        {
            commit = log[epoch - 1];
            return true;
        }

        commit = default;
        return false;
    }

    /// <summary>Asks for the other quality than the one last asked for (Q). Any thread; allocation-free.</summary>
    public void Toggle()
    {
        Interlocked.Increment(ref _requests);
        while (true)
        {
            var current = Volatile.Read(ref _desired);
            var next = current == (int)Start ? (int)Other : (int)Start;
            if (Interlocked.CompareExchange(ref _desired, next, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Pacing thread, once a turn. Starts preparing a requested switch, calls one off whose request went back, and commits
    /// one whose D is ready (or that waited <see cref="MaxPrepareNs"/>). True when a switch was committed now: the caller
    /// queues the generator's switch to <paramref name="to"/> (tagged <paramref name="epoch"/>) before any later write.
    /// </summary>
    public bool Poll(FrameScheduler scheduler, long nowNs, out FrameGenQuality to, out int epoch)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        to = default;
        epoch = 0;
        var desired = Desired;
        var committed = (FrameGenQuality)_committed;
        if (_pending && desired != _target)
        {
            scheduler.CancelGenerationTimeChange();
            _pending = false;
            Interlocked.Increment(ref _cancels);
        }

        if (!_pending && desired != committed)
        {
            _target = desired;
            _pending = true;
            _pendingSinceNs = nowNs;
            _delayAtRequestNs = scheduler.PipelineDelayNs;
            _estimateNs = EstimateFor(desired);
            scheduler.PrepareGenerationTimeChange(_estimateNs);
        }

        if (!_pending)
        {
            return false;
        }

        var ready = scheduler.GenerationTimeChangeReady;
        if (!ready && nowNs - _pendingSinceNs < MaxPrepareNs)
        {
            return false;
        }

        // What the outgoing quality measured, for the next switch back to it.
        if (scheduler.GenerationSampleCount >= PipelineDelayTuner.ConfidentSampleCount)
        {
            _measured[(int)committed] = scheduler.Diagnostics.GenerationTimeNs;
        }

        scheduler.CommitGenerationTimeChange();
        _pending = false;
        var next = _epoch + 1;
        if (next <= LogCapacity)
        {
            _log[next - 1] = new QualityCommit
            {
                Epoch = next,
                From = committed,
                To = _target,
                RequestedNs = _pendingSinceNs,
                CommittedNs = nowNs,
                DelayAtRequestNs = _delayAtRequestNs,
                DelayAtCommitNs = scheduler.PipelineDelayNs,
                EstimateNs = _estimateNs,
                GaveUpWaiting = !ready,
            };
            Volatile.Write(ref _logCount, next);
        }

        if (!ready)
        {
            Interlocked.Increment(ref _gaveUp);
        }

        Volatile.Write(ref _committed, (int)_target);
        Volatile.Write(ref _epoch, next);
        Interlocked.Increment(ref _commits);
        to = _target;
        epoch = next;
        return true;
    }

    /// <summary>
    /// The figure the scheduler's estimate starts from at <paramref name="quality"/>: what it measured the last time it ran
    /// (the scheduler's p99 with enough samples), else the seed.
    /// </summary>
    public long EstimateFor(FrameGenQuality quality) =>
        IsQuality(quality) && _measured[(int)quality] > 0 ? _measured[(int)quality] : _seed[(int)quality];

    private static bool IsQuality(FrameGenQuality q) => q is FrameGenQuality.Low or FrameGenQuality.Medium or FrameGenQuality.High;

    /// <summary>Q's other quality for a run that starts at <paramref name="start"/>: High, or Medium when it starts at High.</summary>
    public static FrameGenQuality OtherOf(FrameGenQuality start) => Normalise(start) == FrameGenQuality.High ? FrameGenQuality.Medium : FrameGenQuality.High;

    /// <summary>The backend default is Medium for VFG.</summary>
    public static FrameGenQuality Normalise(FrameGenQuality quality) => quality == FrameGenQuality.Default ? FrameGenQuality.Medium : quality;
}
