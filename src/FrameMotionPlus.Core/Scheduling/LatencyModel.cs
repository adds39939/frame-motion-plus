namespace FrameMotionPlus.Core.Scheduling;

/// <summary>One row of the design doc's latency table for a given T, M and d.</summary>
public readonly record struct LatencyEstimate
{
    public long SourceIntervalNs { get; init; }

    public int Multiplier { get; init; }

    public long GenerationTimeNs { get; init; }

    /// <summary>(M − 1)/M · T: how long a real frame waits for its generated predecessors to be shown.</summary>
    public long HoldNs { get; init; }

    /// <summary>T / M: each generated frame must finish within one output interval.</summary>
    public long PerFrameBudgetNs { get; init; }

    /// <summary>ΔL = d + (M − 1)/M · T.</summary>
    public long AddedLatencyNs { get; init; }

    /// <summary>d ≤ T / M.</summary>
    public bool Fits { get; init; }
}

/// <summary>
/// The latency model: interpolation cannot show a generated frame between N and N+1 until N+1 has arrived,
/// and N+1 then waits for its generated predecessors, so every real frame gains ΔL = d + (M − 1)/M · T,
/// subject to d ≤ T/M.
/// </summary>
public static class LatencyModel
{
    public static long HoldNs(long sourceIntervalNs, int multiplier)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(multiplier, 1);
        return (long)Math.Round((double)sourceIntervalNs * (multiplier - 1) / multiplier);
    }

    public static long PerFrameBudgetNs(long sourceIntervalNs, int multiplier)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(multiplier, 1);
        return (long)Math.Round((double)sourceIntervalNs / multiplier);
    }

    public static long AddedLatencyNs(long sourceIntervalNs, int multiplier, long generationTimeNs) =>
        generationTimeNs + HoldNs(sourceIntervalNs, multiplier);

    public static bool Fits(long sourceIntervalNs, int multiplier, long generationTimeNs) =>
        generationTimeNs <= PerFrameBudgetNs(sourceIntervalNs, multiplier);

    /// <summary>Largest M ≤ <paramref name="cap"/> whose budget T/M still holds d; 1 means no generation fits.</summary>
    public static int MaxMultiplierThatFits(long sourceIntervalNs, long generationTimeNs, int cap = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        for (var m = cap; m >= 2; m--)
        {
            if (Fits(sourceIntervalNs, m, generationTimeNs))
            {
                return m;
            }
        }

        return 1;
    }

    public static LatencyEstimate Evaluate(long sourceIntervalNs, int multiplier, long generationTimeNs) => new()
    {
        SourceIntervalNs = sourceIntervalNs,
        Multiplier = multiplier,
        GenerationTimeNs = generationTimeNs,
        HoldNs = HoldNs(sourceIntervalNs, multiplier),
        PerFrameBudgetNs = PerFrameBudgetNs(sourceIntervalNs, multiplier),
        AddedLatencyNs = AddedLatencyNs(sourceIntervalNs, multiplier, generationTimeNs),
        Fits = Fits(sourceIntervalNs, multiplier, generationTimeNs),
    };

    /// <summary>
    /// Hold on a fixed-refresh panel when the ratio is not an integer: the smallest generated timestep
    /// approaches the snap epsilon, so the hold approaches (1 − ε) · T and D approaches T + d.
    /// </summary>
    public static long NonIntegerFixedRefreshHoldNs(long sourceIntervalNs, double snapEpsilon) =>
        (long)Math.Round(sourceIntervalNs * (1.0 - snapEpsilon));
}
