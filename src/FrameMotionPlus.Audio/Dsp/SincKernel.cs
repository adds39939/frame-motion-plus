namespace FrameMotionPlus.Audio.Dsp;

/// <summary>
/// The fractional-delay interpolator of the resampler: a Kaiser-windowed sinc (β = 7, about 70 dB down outside its band)
/// of <see cref="Taps"/> taps, tabulated at <see cref="Phases"/> fractions of a frame and interpolated linearly between
/// neighbouring phases. The sample at position <c>n + f</c> (0 ≤ f &lt; 1) is <c>Σ h_f[k] · x[n − Before + k]</c>. The cut-off
/// is the Nyquist frequency, so at f = 0 the kernel is exactly a unit impulse: at a whole-frame position the output is the
/// input, bit for bit. Each phase is normalised to unit gain at DC. The response is flat to about 20 kHz at every phase;
/// only above that does it depend on the fraction (a ratio a few hundred ppm from 1 sweeps the fraction slowly). Built
/// once; read-only afterwards.
/// </summary>
public sealed class SincKernel
{
    public const int Taps = 32;

    /// <summary>Input frames used before <c>n</c> (the whole part of the position).</summary>
    public const int Before = (Taps / 2) - 1;

    /// <summary>Input frames used after <c>n</c>.</summary>
    public const int After = Taps / 2;

    public const int Phases = 256;

    public const double Beta = 7.0;

    private readonly float[] _table = new float[(Phases + 1) * Taps];

    private SincKernel()
    {
        var row = new double[Taps];
        var half = Taps / 2.0;
        for (var p = 0; p <= Phases; p++)
        {
            var f = p / (double)Phases;
            var sum = 0.0;
            for (var k = 0; k < Taps; k++)
            {
                var t = k - Before - f;
                row[k] = Sinc(t) * Kaiser(t / half);
                sum += row[k];
            }

            for (var k = 0; k < Taps; k++)
            {
                _table[(p * Taps) + k] = (float)(row[k] / sum);
            }
        }
    }

    public static SincKernel Instance { get; } = new();

    /// <summary>The coefficients for <paramref name="fraction"/> in [0, 1) into <paramref name="into"/> (at least <see cref="Taps"/>).</summary>
    public void Coefficients(double fraction, Span<float> into)
    {
        var p = fraction * Phases;
        var i = (int)p;
        if (i >= Phases)
        {
            i = Phases - 1;
        }
        else if (i < 0)
        {
            i = 0;
        }

        var w = (float)(p - i);
        var row0 = _table.AsSpan(i * Taps, Taps);
        var row1 = _table.AsSpan((i + 1) * Taps, Taps);
        if (w == 0f)
        {
            row0.CopyTo(into);
            return;
        }

        for (var k = 0; k < Taps; k++)
        {
            into[k] = row0[k] + (w * (row1[k] - row0[k]));
        }
    }

    internal static double Sinc(double t)
    {
        if (t == 0)
        {
            return 1;
        }

        // Exactly zero at the other whole frames (sin(πt) is not, in floating point): the impulse at f = 0 stays exact.
        if (t == Math.Round(t))
        {
            return 0;
        }

        var x = Math.PI * t;
        return Math.Sin(x) / x;
    }

    internal static double Kaiser(double x)
    {
        if (x <= -1 || x >= 1)
        {
            return x is -1 or 1 ? 1 / BesselI0(Beta) : 0;
        }

        return BesselI0(Beta * Math.Sqrt(1 - (x * x))) / BesselI0(Beta);
    }

    /// <summary>The modified Bessel function of the first kind, order 0 (power series; converges fast for β ≤ 20).</summary>
    internal static double BesselI0(double x)
    {
        var sum = 1.0;
        var term = 1.0;
        var q = x * x / 4;
        for (var k = 1; k < 64; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }
}
