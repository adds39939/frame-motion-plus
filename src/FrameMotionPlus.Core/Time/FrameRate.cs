using System.Globalization;

namespace FrameMotionPlus.Core.Time;

/// <summary>
/// An exact rational rate in frames per second. Grid instants are computed exactly from the rational,
/// so a 59.94 Hz source or a 120 Hz vblank grid never accumulates rounding drift.
/// </summary>
public readonly record struct FrameRate
{
    public FrameRate(long numerator, long denominator)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);
        var g = Gcd(numerator, denominator);
        Numerator = numerator / g;
        Denominator = denominator / g;
    }

    public long Numerator { get; }

    public long Denominator { get; }

    public bool IsValid => Numerator > 0 && Denominator > 0;

    public double Hz => (double)Numerator / Denominator;

    /// <summary>Exact period in nanoseconds as a double (sub-nanosecond precision for grid maths).</summary>
    public double PeriodNs => Nanoseconds.PerSecond * (double)Denominator / Numerator;

    public static FrameRate FromHz(long hz) => new(hz, 1);

    /// <summary>Rate whose period is the given whole number of nanoseconds (for measured periods).</summary>
    public static FrameRate FromPeriodNs(long periodNs) => new(Nanoseconds.PerSecond, periodNs);

    /// <summary>
    /// Converts a decimal rate to an exact rational, recognising the NTSC family (n·1000/1001, e.g. 59.94,
    /// 29.97, 23.976, 119.88) when the given digits match it.
    /// </summary>
    public static FrameRate FromDouble(double hz, int decimals = 3)
    {
        if (!double.IsFinite(hz) || hz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hz), hz, "Rate must be a positive finite number.");
        }

        decimals = Math.Clamp(decimals, 0, 6);
        var rounded = Math.Round(hz, decimals);
        if (rounded == Math.Round(rounded))
        {
            return new FrameRate((long)rounded, 1);
        }

        var tolerance = 0.5 * Math.Pow(10, -decimals) + 1e-9;
        var ntscBase = Math.Round(hz * 1.001);
        if (ntscBase > 0 && Math.Abs((ntscBase * 1000 / 1001) - hz) <= tolerance)
        {
            return new FrameRate((long)ntscBase * 1000, 1001);
        }

        var scale = (long)Math.Pow(10, decimals);
        return new FrameRate((long)Math.Round(hz * scale), scale);
    }

    /// <summary>Parses "120", "59.94", "143.856" or an explicit rational such as "60000/1001".</summary>
    public static bool TryParse(string? text, out FrameRate rate)
    {
        rate = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            if (long.TryParse(text.AsSpan(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && long.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var d)
                && n > 0 && d > 0)
            {
                rate = new FrameRate(n, d);
                return true;
            }

            return false;
        }

        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hz) || hz <= 0)
        {
            return false;
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var decimals = dot < 0 ? 0 : text.Length - dot - 1;
        rate = FromDouble(hz, Math.Min(decimals, 6));
        return true;
    }

    /// <summary>Exact instant of grid tick <paramref name="index"/> relative to tick 0 (floored to whole ns).</summary>
    public long TickTimeNs(long index) =>
        Nanoseconds.FloorDiv((Int128)index * Nanoseconds.PerSecond * Denominator, Numerator);

    /// <summary>Index of the last tick at or before <paramref name="timeNs"/> (relative to tick 0).</summary>
    public long TickIndexAtOrBefore(long timeNs)
    {
        var index = Nanoseconds.FloorDiv((Int128)timeNs * Numerator, (Int128)Nanoseconds.PerSecond * Denominator);
        while (TickTimeNs(index + 1) <= timeNs)
        {
            index++;
        }

        while (TickTimeNs(index) > timeNs)
        {
            index--;
        }

        return index;
    }

    public override string ToString() =>
        Denominator == 1
            ? Numerator.ToString(CultureInfo.InvariantCulture)
            : Hz.ToString("0.###", CultureInfo.InvariantCulture);

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
