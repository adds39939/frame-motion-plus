using System.Numerics;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Kernels;

namespace FrameMotionPlus.App.Decode;

/// <summary>
/// The CPU reference of the YCbCr-to-RGB conversion, written from ITU-T H.273 and the BT.601 / BT.709 / BT.2020 numbers
/// without the GPU path's code or closed-form coefficients, so that agreeing with it bit for bit means something.
/// <para>
/// It builds the <em>forward</em> matrix the standards define (Y' = Kr·R' + Kg·G' + Kb·B', Cb = (B' − Y') / (2(1 − Kb)),
/// Cr = (R' − Y') / (2(1 − Kr))) in exact rational arithmetic and inverts it by Gauss–Jordan elimination; the kernel's
/// coefficients come from the closed-form inverse instead. Dequantisation follows H.273's equations (limited range:
/// Y' = (Y / 2^(n−8) − 16) / 219, C = (C / 2^(n−8) − 128) / 224; full range: Y' = Y / (2^n − 1),
/// C = (C − 2^(n−1)) / (2^n − 1)). Chroma at a luma position is the bilinear interpolation at the siting H.273 gives for
/// <c>chroma_sample_loc_type</c> (chroma at luma coordinates (ox, oy) + 2·(i, j), ox ∈ {0, ½}, oy ∈ {0, ½, 1}), edges
/// clamped. Each output code is the exact value (2^m − 1)·R' rounded half up and clamped to [0, 2^m − 1].
/// </para>
/// <para>
/// Per pixel the exact rationals are evaluated with 128-bit integers over each channel's own common denominator.
/// </para>
/// </summary>
internal sealed class YuvReference
{
    private readonly Int128[] _a = new Int128[3];  // per channel, on (Y code − y0)
    private readonly Int128[] _b = new Int128[3];  // on (16·Cb − c0)
    private readonly Int128[] _r = new Int128[3];  // on (16·Cr − c0)
    private readonly Int128[] _den = new Int128[3];
    private readonly int _y0;
    private readonly int _c0x16;
    private readonly int _maxOut;
    private readonly int _ox2; // 2·ox
    private readonly int _oy2; // 2·oy

    private YuvReference(MatrixCoefficients matrix, ColorRange range, int sourceBits, ChromaSiting siting, PixelFormat output)
    {
        Matrix = matrix;
        Range = range;
        SourceBits = sourceBits;
        Siting = siting;
        Output = output;
        _maxOut = output == PixelFormat.Rgb10A2 ? 1023 : 255;
        var (kr, kb) = matrix switch
        {
            MatrixCoefficients.Bt601 => (new Q(299, 1000), new Q(114, 1000)),
            MatrixCoefficients.Bt709 => (new Q(2126, 10000), new Q(722, 10000)),
            MatrixCoefficients.Bt2020NonConstant => (new Q(2627, 10000), new Q(593, 10000)),
            _ => throw new ArgumentException($"No reference for {matrix}.", nameof(matrix)),
        };
        var kg = Q.One - kr - kb;

        // The forward matrix, rows Y', Cb, Cr over columns R', G', B'.
        var y = new[] { kr, kg, kb };
        var forward = new Q[3, 3];
        for (var c = 0; c < 3; c++)
        {
            forward[0, c] = y[c];
            forward[1, c] = ((c == 2 ? Q.One : Q.Zero) - y[c]) / (new Q(2) * (Q.One - kb));
            forward[2, c] = ((c == 0 ? Q.One : Q.Zero) - y[c]) / (new Q(2) * (Q.One - kr));
        }

        var inverse = Invert(forward);

        // Dequantisation: Y' = (Y − y0) / yScale, C = (16·Csum/16... ) — chroma arrives as 16 × the interpolated code.
        var n = sourceBits;
        Q yScale, cScale;
        if (range == ColorRange.Limited)
        {
            var s = 1 << (n - 8);
            _y0 = 16 * s;
            _c0x16 = 16 * 128 * s;
            yScale = new Q(219 * s);
            cScale = new Q(224 * s);
        }
        else
        {
            _y0 = 0;
            _c0x16 = 16 * (1 << (n - 1));
            yScale = new Q((1 << n) - 1);
            cScale = new Q((1 << n) - 1);
        }

        var m = new Q(_maxOut);
        for (var ch = 0; ch < 3; ch++)
        {
            // value·M = M·(inv[ch,0]·(Y − y0)/yScale + inv[ch,1]·(16Cb − c0)/(16·cScale) + inv[ch,2]·(16Cr − c0)/(16·cScale))
            var qa = m * inverse[ch, 0] / yScale;
            var qb = m * inverse[ch, 1] / (new Q(16) * cScale);
            var qr = m * inverse[ch, 2] / (new Q(16) * cScale);
            var den = Lcm(Lcm(qa.Den, qb.Den), qr.Den);
            _a[ch] = (Int128)(qa.Num * (den / qa.Den));
            _b[ch] = (Int128)(qb.Num * (den / qb.Den));
            _r[ch] = (Int128)(qr.Num * (den / qr.Den));
            _den[ch] = (Int128)den;
        }

        (_ox2, _oy2) = siting switch
        {
            ChromaSiting.Left => (0, 1),
            ChromaSiting.Center => (1, 1),
            ChromaSiting.TopLeft => (0, 0),
            ChromaSiting.Top => (1, 0),
            ChromaSiting.BottomLeft => (0, 2),
            ChromaSiting.Bottom => (1, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(siting)),
        };
    }

    public MatrixCoefficients Matrix { get; }

    public ColorRange Range { get; }

    public int SourceBits { get; }

    public ChromaSiting Siting { get; }

    public PixelFormat Output { get; }

    public static YuvReference Create(MatrixCoefficients matrix, ColorRange range, int sourceBits, ChromaSiting siting, PixelFormat output)
    {
        if (range is not (ColorRange.Limited or ColorRange.Full) || sourceBits is not (8 or 10) || output is not (PixelFormat.Rgba8 or PixelFormat.Rgb10A2))
        {
            throw new ArgumentException($"No reference for {range} {sourceBits}-bit to {output}.");
        }

        return new YuvReference(matrix, range, sourceBits, siting, output);
    }

    /// <summary>
    /// Converts planar 4:2:0 code values (<paramref name="luma"/>: width × height; <paramref name="cb"/> and
    /// <paramref name="cr"/>: ⌈width/2⌉ × ⌈height/2⌉) into packed RGBA8 or RGB10A2 pixels, row by row in parallel.
    /// </summary>
    public void Convert(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr, int width, int height, Span<uint> output)
    {
        var cw = (width + 1) / 2;
        var ch = (height + 1) / 2;
        if (luma.Length < width * height || cb.Length < cw * ch || cr.Length < cw * ch || output.Length < width * height)
        {
            throw new ArgumentException("Plane or output too small.");
        }

        var l = luma.ToArray();
        var b = cb.ToArray();
        var r = cr.ToArray();
        var o = new uint[width * height];
        Parallel.For(0, height, yy =>
        {
            for (var x = 0; x < width; x++)
            {
                o[(yy * width) + x] = Pixel(l, b, r, width, height, x, yy);
            }
        });
        o.CopyTo(output);
    }

    /// <summary>The output pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public uint Pixel(ushort[] luma, ushort[] cb, ushort[] cr, int width, int height, int x, int y)
    {
        var cw = (width + 1) / 2;
        var ch = (height + 1) / 2;

        // Chroma sample i sits at luma column ox + 2i, so luma column x is at chroma position (x − ox) / 2: in quarters,
        // 2x − 2ox. Bilinear between floor and floor + 1, weights (4 − f) and f quarters per axis.
        var px = (2 * x) - _ox2;
        var py = (2 * y) - _oy2;
        var i0 = FloorDiv(px, 4);
        var j0 = FloorDiv(py, 4);
        var fx = px - (4 * i0);
        var fy = py - (4 * j0);
        long cbSum = 0, crSum = 0;
        for (var dj = 0; dj < 2; dj++)
        {
            var wy = dj == 0 ? 4 - fy : fy;
            var row = Math.Clamp(j0 + dj, 0, ch - 1) * cw;
            for (var di = 0; di < 2; di++)
            {
                var wx = di == 0 ? 4 - fx : fx;
                var column = Math.Clamp(i0 + di, 0, cw - 1);
                cbSum += wx * wy * cb[row + column];
                crSum += wx * wy * cr[row + column];
            }
        }

        // cbSum is 16 × the interpolated Cb code.
        Int128 a = luma[(y * width) + x] - _y0;
        Int128 qb = cbSum - _c0x16;
        Int128 qr = crSum - _c0x16;
        Span<uint> rgb = stackalloc uint[3];
        for (var c = 0; c < 3; c++)
        {
            var numerator = (_a[c] * a) + (_b[c] * qb) + (_r[c] * qr);
            rgb[c] = RoundHalfUp(numerator, _den[c], _maxOut);
        }

        return Output == PixelFormat.Rgb10A2
            ? rgb[0] | (rgb[1] << 10) | (rgb[2] << 20) | (3u << 30)
            : rgb[0] | (rgb[1] << 8) | (rgb[2] << 16) | (255u << 24);
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    /// <summary>floor(n / d + ½) clamped to [0, max], for d &gt; 0.</summary>
    private static uint RoundHalfUp(Int128 n, Int128 d, int max)
    {
        var twice = (2 * n) + d;
        if (twice < 0)
        {
            return 0;
        }

        var q = twice / (2 * d);
        return q > max ? (uint)max : (uint)q;
    }

    private static BigInteger Lcm(BigInteger a, BigInteger b) => a / BigInteger.GreatestCommonDivisor(a, b) * b;

    /// <summary>Gauss–Jordan elimination with exact rationals.</summary>
    private static Q[,] Invert(Q[,] m)
    {
        var n = m.GetLength(0);
        var a = new Q[n, 2 * n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                a[i, j] = m[i, j];
                a[i, n + j] = i == j ? Q.One : Q.Zero;
            }
        }

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            while (a[pivot, col].IsZero)
            {
                pivot++;
            }

            for (var j = 0; j < 2 * n; j++)
            {
                (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
            }

            var p = a[col, col];
            for (var j = 0; j < 2 * n; j++)
            {
                a[col, j] /= p;
            }

            for (var i = 0; i < n; i++)
            {
                if (i == col || a[i, col].IsZero)
                {
                    continue;
                }

                var f = a[i, col];
                for (var j = 0; j < 2 * n; j++)
                {
                    a[i, j] -= f * a[col, j];
                }
            }
        }

        var inverse = new Q[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                inverse[i, j] = a[i, n + j];
            }
        }

        return inverse;
    }

    /// <summary>An exact rational, always reduced, denominator positive.</summary>
    internal readonly record struct Q
    {
        public Q(BigInteger num, BigInteger den)
        {
            if (den.IsZero)
            {
                throw new DivideByZeroException();
            }

            if (den.Sign < 0)
            {
                (num, den) = (-num, -den);
            }

            var g = BigInteger.GreatestCommonDivisor(num, den);
            (Num, Den) = g.IsZero || g.IsOne ? (num, den) : (num / g, den / g);
        }

        public Q(BigInteger value)
            : this(value, BigInteger.One)
        {
        }

        public static Q Zero => new(0);

        public static Q One => new(1);

        public BigInteger Num { get; }

        public BigInteger Den { get; }

        public bool IsZero => Num.IsZero;

        public static Q operator +(Q a, Q b) => new((a.Num * b.Den) + (b.Num * a.Den), a.Den * b.Den);

        public static Q operator -(Q a, Q b) => new((a.Num * b.Den) - (b.Num * a.Den), a.Den * b.Den);

        public static Q operator *(Q a, Q b) => new(a.Num * b.Num, a.Den * b.Den);

        public static Q operator /(Q a, Q b) => new(a.Num * b.Den, a.Den * b.Num);
    }
}
