namespace FrameMotionPlus.Core.Time;

/// <summary>Conversions for the single time base: <see langword="long"/> nanoseconds on CLOCK_MONOTONIC.</summary>
public static class Nanoseconds
{
    public const long PerMicrosecond = 1_000;
    public const long PerMillisecond = 1_000_000;
    public const long PerSecond = 1_000_000_000;

    public static long FromMilliseconds(double milliseconds) => (long)Math.Round(milliseconds * PerMillisecond);

    public static long FromMicroseconds(double microseconds) => (long)Math.Round(microseconds * PerMicrosecond);

    public static long FromSeconds(double seconds) => (long)Math.Round(seconds * PerSecond);

    public static double ToMilliseconds(long nanoseconds) => nanoseconds / (double)PerMillisecond;

    public static double ToMilliseconds(double nanoseconds) => nanoseconds / PerMillisecond;

    public static double ToSeconds(long nanoseconds) => nanoseconds / (double)PerSecond;

    /// <summary>Floor division that rounds towards negative infinity, for grid index arithmetic.</summary>
    internal static long FloorDiv(Int128 numerator, Int128 denominator)
    {
        var q = numerator / denominator;
        var r = numerator % denominator;
        if (r != 0 && ((r < 0) != (denominator < 0)))
        {
            q -= 1;
        }

        return (long)q;
    }
}
