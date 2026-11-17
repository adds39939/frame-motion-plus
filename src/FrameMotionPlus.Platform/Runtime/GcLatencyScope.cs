using System.Runtime;

namespace FrameMotionPlus.Platform.Runtime;

/// <summary>
/// Sets <see cref="GCSettings.LatencyMode"/> for the duration of playback and restores the previous mode on dispose.
/// The mode is process-wide: enter one scope at a time, from the thread that owns the pipeline's lifetime.
/// <para>
/// <see cref="GCLatencyMode.SustainedLowLatency"/> (the default request, as the design doc asks for playback) only
/// takes effect with concurrent GC, which <c>fmp</c> runs with; where concurrent GC is off (the test projects, for exact
/// allocation counters) the runtime keeps <see cref="GCLatencyMode.Batch"/>. <see cref="Obtained"/> says which applies.
/// A zero-allocation steady state performs no collections in either mode.
/// </para>
/// </summary>
public sealed class GcLatencyScope : IDisposable
{
    private bool _disposed;

    private GcLatencyScope(GCLatencyMode previous, GCLatencyMode requested, GCLatencyMode obtained)
    {
        Previous = previous;
        Requested = requested;
        Obtained = obtained;
    }

    public GCLatencyMode Previous { get; }

    public GCLatencyMode Requested { get; }

    /// <summary>The mode the runtime actually uses inside the scope.</summary>
    public GCLatencyMode Obtained { get; }

    public bool Applied => Obtained == Requested;

    public static GcLatencyScope Enter(GCLatencyMode requested = GCLatencyMode.SustainedLowLatency)
    {
        if (requested == GCLatencyMode.NoGCRegion || !Enum.IsDefined(requested))
        {
            throw new ArgumentOutOfRangeException(nameof(requested), requested, "Use GC.TryStartNoGCRegion for a no-GC region.");
        }

        lock (GcConfiguration.LatencyModeLock)
        {
            var previous = GCSettings.LatencyMode;
            GCSettings.LatencyMode = requested;
            return new GcLatencyScope(previous, requested, GCSettings.LatencyMode);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (GcConfiguration.LatencyModeLock)
        {
            if (Previous != GCLatencyMode.NoGCRegion && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
            {
                GCSettings.LatencyMode = Previous;
            }
        }
    }
}

/// <summary>
/// Process-wide GC counters at one instant, cheap and allocation-free to take (for per-second telemetry and for
/// "no collections during playback" checks). Collection counts follow <see cref="GC.CollectionCount"/>: a gen1
/// collection also counts as a gen0 one, so <see cref="Gen0Collections"/> is the number of collections of any kind.
/// <see cref="TotalAllocatedBytes"/> is the runtime's cheap estimate: it counts allocation contexts handed to threads
/// in full, so it can drop by a few KB per thread when a collection flushes them. The precise figure would suspend
/// every managed thread, the pacing thread included. Use <see cref="CurrentThreadAllocatedBytes"/> for exact checks.
/// </summary>
public readonly record struct GcSnapshot(
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long TotalAllocatedBytes,
    TimeSpan TotalPauseDuration)
{
    public static GcSnapshot Take() => new(
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        GC.GetTotalAllocatedBytes(precise: false),
        GC.GetTotalPauseDuration());

    /// <summary>Bytes the calling thread has allocated since it started; exact with non-concurrent GC.</summary>
    public static long CurrentThreadAllocatedBytes => GC.GetAllocatedBytesForCurrentThread();

    /// <summary>Collections of any generation.</summary>
    public int Collections => Gen0Collections;

    /// <summary>The change from <paramref name="earlier"/> to this snapshot.</summary>
    public GcSnapshot Since(in GcSnapshot earlier) => new(
        Gen0Collections - earlier.Gen0Collections,
        Gen1Collections - earlier.Gen1Collections,
        Gen2Collections - earlier.Gen2Collections,
        TotalAllocatedBytes - earlier.TotalAllocatedBytes,
        TotalPauseDuration - earlier.TotalPauseDuration);
}

/// <summary>The GC flavour this process runs with.</summary>
public static class GcConfiguration
{
    /// <summary>Held by <see cref="GcLatencyScope"/> and by the probe below whenever they change the latency mode.</summary>
    internal static readonly Lock LatencyModeLock = new();

    private static int _concurrent = -1; // -1 not probed yet, 0 no, 1 yes

    public static bool IsServer => GCSettings.IsServerGC;

    /// <summary>
    /// Background (concurrent) gen2 collections are enabled. Asked of the runtime itself, so environment overrides
    /// (<c>DOTNET_gcConcurrent</c>) count: it accepts <see cref="GCLatencyMode.SustainedLowLatency"/> only when
    /// background GC is available. Probed once (the GC flavour is fixed at start-up) by setting that mode and
    /// restoring the previous one under the same lock <see cref="GcLatencyScope"/> takes, then cached.
    /// </summary>
    public static bool IsConcurrent
    {
        get
        {
            var known = Volatile.Read(ref _concurrent);
            if (known >= 0)
            {
                return known == 1;
            }

            lock (LatencyModeLock)
            {
                if (_concurrent >= 0)
                {
                    return _concurrent == 1;
                }

                var mode = GCSettings.LatencyMode;
                if (mode == GCLatencyMode.NoGCRegion)
                {
                    // The mode cannot be changed inside a no-GC region: answer from the runtime configuration, uncached.
                    return AppContext.GetData("System.GC.Concurrent") is not false;
                }

                var concurrent = mode == GCLatencyMode.SustainedLowLatency;
                if (!concurrent)
                {
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                    concurrent = GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency;
                    GCSettings.LatencyMode = mode;
                }

                Volatile.Write(ref _concurrent, concurrent ? 1 : 0);
                return concurrent;
            }
        }
    }

    /// <summary>Tests only: makes the next <see cref="IsConcurrent"/> probe again.</summary>
    internal static void ForgetProbe() => Volatile.Write(ref _concurrent, -1);

    /// <summary>e.g. "workstation, non-concurrent, latency mode Batch".</summary>
    public static string Describe() =>
        $"{(IsServer ? "server" : "workstation")}, {(IsConcurrent ? "concurrent" : "non-concurrent")}, latency mode {GCSettings.LatencyMode}";
}
