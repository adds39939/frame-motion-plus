namespace FrameMotionPlus.App.Vfg;

/// <summary>
/// A smooth, detailed, colourful texture: sums of sinusoids at several scales and orientations per channel, values
/// around 0.5 in [0, 1]. Evaluated separably (sin(au + bv) = sin(au)cos(bv) + cos(au)sin(bv), with per-column and
/// per-row tables), so a 4K frame takes tens of milliseconds. A crop at whole-pixel offsets is an exact pan.
/// </summary>
internal sealed class SyntheticTexture
{
    // (channel, a, b, phase, amplitude): low, middle and high frequencies in several directions.
    private static readonly (int Channel, double A, double B, double Phase, double Amp)[] Terms =
    [
        (0, 0.031, 0.017, 0, 0.22), (0, 0.11, -0.07, 0, 0.15), (0, 0.29, 0.23, 0, 0.10),
        (1, 0.023, -0.029, 1, 0.22), (1, 0.05, 0.13, 0, 0.15), (1, 0.37, -0.19, 0, 0.10),
        (2, 0.0, 0.027, 2, 0.22), (2, 0.09, 0.09, 0, 0.15), (2, 0.21, 0.41, 3, 0.10),
    ];

    private readonly double[][] _sinU;
    private readonly double[][] _cosU;
    private readonly double[][] _sinV;
    private readonly double[][] _cosV;

    public SyntheticTexture(int width, int height)
    {
        Width = width;
        Height = height;
        _sinU = new double[Terms.Length][];
        _cosU = new double[Terms.Length][];
        _sinV = new double[Terms.Length][];
        _cosV = new double[Terms.Length][];
        for (var k = 0; k < Terms.Length; k++)
        {
            var (_, a, b, phase, amp) = Terms[k];
            _sinU[k] = new double[width];
            _cosU[k] = new double[width];
            _sinV[k] = new double[height];
            _cosV[k] = new double[height];
            for (var u = 0; u < width; u++)
            {
                _sinU[k][u] = amp * Math.Sin((a * u) + phase);
                _cosU[k][u] = amp * Math.Cos((a * u) + phase);
            }

            for (var v = 0; v < height; v++)
            {
                _sinV[k][v] = Math.Sin(b * v);
                _cosV[k][v] = Math.Cos(b * v);
            }
        }
    }

    /// <summary>Width of the whole texture: crops of narrower frames pan across it.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Linear RGB in [0, 1] of texel (u, v).</summary>
    public (double R, double G, double B) At(int u, int v)
    {
        double r = 0.5, g = 0.5, b = 0.5;
        for (var k = 0; k < Terms.Length; k++)
        {
            var value = (_sinU[k][u] * _cosV[k][v]) + (_cosU[k][u] * _sinV[k][v]);
            switch (Terms[k].Channel)
            {
                case 0:
                    r += value;
                    break;
                case 1:
                    g += value;
                    break;
                default:
                    b += value;
                    break;
            }
        }

        return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
    }
}
