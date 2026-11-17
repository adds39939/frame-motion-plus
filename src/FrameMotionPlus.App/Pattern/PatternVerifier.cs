using System.Runtime.InteropServices;
using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.App.Pattern;

/// <summary>
/// Checks read-back frames of <c>fmp pattern</c> bit for bit, written from the test-pattern specification in
/// native/kernels/pattern.cu (not from the kernel). Every band of the pattern has rows that are all alike, so one
/// expected row per band is enough: the bars and the ramp never change and are computed once; the moving box and the
/// frame counter are recomputed per frame. A generated frame (blend at t = 0.5 of two consecutive frames) is checked
/// the same way: blending leaves equal pixels unchanged, so only the box and counter rows differ, and those are
/// blended here with the kernel's documented rounding. Allocation-free after construction.
/// </summary>
/// <summary>
/// A generated frame's mean absolute errors (code values per colour channel) over the moving box band: against the
/// true middle frame, the blend of its neighbours against the truth, the output against each neighbour; the worst
/// error over the static bars; and whether the pair straddles the box's wrap (then there is no "middle").
/// </summary>
internal readonly record struct InterpolationScore(double ToTruth, double BlendToTruth, double ToPrev, double ToCurr, double StaticBars, bool Wrap, bool TenBit)
{
    /// <summary>
    /// Closer to the truth than the blend, not a copy of a neighbour, and the static picture intact: no sampled row of
    /// the bars off by more than 1 code on average in 8-bit, 4 codes in 10-bit (the same 0.4 % of full scale).
    /// </summary>
    public bool IsGenuine => ToTruth < BlendToTruth && ToPrev > 0.01 && ToCurr > 0.01 && StaticBars < (TenBit ? 4.0 : 1.0);
}

internal sealed class PatternVerifier
{
    private const uint HalfWeight = 32768; // round(0.5 × 65536)

    // PQ codes of 0, 0.1, 1, 10, 100, 203, 400, 1000, 4000 and 10000 nits (the 10-bit step wedge).
    private static readonly uint[] PqSteps = [0, 64, 153, 307, 520, 594, 668, 769, 923, 1023];

    private readonly int _width;
    private readonly int _height;
    private readonly bool _tenBit;
    private readonly int _barsEnd;
    private readonly int _boxEnd;
    private readonly int _rampEnd;
    private readonly int _wedgeStart;
    private readonly uint[] _bars;
    private readonly uint[] _ramp;
    private readonly uint[] _wedge;
    private readonly uint[] _box;
    private readonly uint[] _counter;
    private readonly uint[] _scratch;
    private readonly uint[] _prevBox;
    private readonly uint[] _currBox;

    public PatternVerifier(int width, int height, PixelFormat format)
    {
        _width = width;
        _height = height;
        _tenBit = format == PixelFormat.Rgb10A2;
        _barsEnd = height * 7 / 12;
        _boxEnd = height * 9 / 12;
        _rampEnd = height * 10 / 12;
        _wedgeStart = _tenBit ? (_boxEnd + _rampEnd) / 2 : _rampEnd;
        _bars = new uint[width];
        _ramp = new uint[width];
        _wedge = new uint[width];
        _box = new uint[width];
        _counter = new uint[width];
        _scratch = new uint[width];
        _prevBox = new uint[width];
        _currBox = new uint[width];

        var level = _tenBit ? 594u : 191u;
        var max = _tenBit ? 1023u : 255u;
        for (var x = 0; x < width; x++)
        {
            // Bars: white, yellow, cyan, green, magenta, red, blue, black at 75 % (8-bit) or 203 nits (PQ).
            var mask = (0x01452367u >> (x * 8 / width * 4)) & 7u;
            _bars[x] = Pack((mask & 4) != 0 ? level : 0, (mask & 2) != 0 ? level : 0, (mask & 1) != 0 ? level : 0);
            var ramp = width > 1 ? (uint)((long)x * max / (width - 1)) : 0;
            _ramp[x] = Pack(ramp, ramp, ramp);
            var step = PqSteps[x * 10 / width];
            _wedge[x] = Pack(step, step, step);
        }
    }

    /// <summary>Real frame <paramref name="frame"/>: true when <paramref name="pixels"/> (tightly packed rows) is exactly it.</summary>
    public bool IsFrame(ReadOnlySpan<byte> pixels, uint frame)
    {
        FillBox(_box, frame);
        FillCounter(_counter, frame);
        return Compare(pixels);
    }

    /// <summary>Blend at t = 0.5 of real frames <paramref name="frame"/> and <paramref name="frame"/> + 1.</summary>
    public bool IsBlend(ReadOnlySpan<byte> pixels, uint frame)
    {
        FillBox(_box, frame);
        FillBox(_scratch, frame + 1);
        Mix(_box, _scratch);
        FillCounter(_counter, frame);
        FillCounter(_scratch, frame + 1);
        Mix(_counter, _scratch);
        return Compare(pixels);
    }

    /// <summary>
    /// Scores a generated frame meant to be the middle of real pattern frames <paramref name="frame"/> and
    /// <paramref name="frame"/> + 2, whose truth is pattern frame <paramref name="frame"/> + 1 (VFG is not bit-exact
    /// against anything, so it is measured, not compared): mean absolute error in code values over the moving box band
    /// against the truth, against the blend of the neighbours, and against each neighbour; and over the static bars.
    /// Allocation-free.
    /// </summary>
    public InterpolationScore ScoreGenerated(ReadOnlySpan<byte> pixels, uint frame)
    {
        var words = MemoryMarshal.Cast<byte, uint>(pixels);
        FillBox(_box, frame + 1);
        FillBox(_prevBox, frame);
        FillBox(_currBox, frame + 2);
        var blend = _scratch.AsSpan();
        _prevBox.CopyTo(blend);
        Mix(blend, _currBox);

        // Truth rows are alike throughout the band, so the blend's error is one row's.
        var blendError = RowError(blend, _box);
        double toTruth = 0, toPrev = 0, toCurr = 0, bars = 0;
        var rows = _boxEnd - _barsEnd;
        for (var y = _barsEnd; y < _boxEnd; y++)
        {
            var row = words.Slice(y * _width, _width);
            toTruth += RowError(row, _box);
            toPrev += RowError(row, _prevBox);
            toCurr += RowError(row, _currBox);
        }

        for (var y = 0; y < _barsEnd; y += 8)
        {
            bars = Math.Max(bars, RowError(words.Slice(y * _width, _width), _bars));
        }

        return new InterpolationScore(toTruth / rows, blendError, toPrev / rows, toCurr / rows, bars, BoxX(frame + 2) < BoxX(frame), _tenBit);
    }

    /// <summary>The frame counter of a real frame (cell centres, threshold at half scale).</summary>
    public uint DecodeCounter(ReadOnlySpan<byte> pixels)
    {
        var words = MemoryMarshal.Cast<byte, uint>(pixels);
        var cellWidth = _width / 32;
        var row = (_rampEnd + _height) / 2;
        var half = _tenBit ? 512u : 128u;
        uint value = 0;
        for (var cell = 0; cell < 32; cell++)
        {
            var pixel = words[(row * _width) + (cell * cellWidth) + (cellWidth / 2)];
            var green = _tenBit ? (pixel >> 10) & 0x3FF : (pixel >> 8) & 0xFF;
            value = (value << 1) | (green >= half ? 1u : 0u);
        }

        return value;
    }

    private bool Compare(ReadOnlySpan<byte> pixels)
    {
        var words = MemoryMarshal.Cast<byte, uint>(pixels);
        if (words.Length < _width * _height)
        {
            return false;
        }

        for (var y = 0; y < _height; y++)
        {
            ReadOnlySpan<uint> expected = y < _barsEnd ? _bars : y < _boxEnd ? _box : y < _wedgeStart ? _ramp : y < _rampEnd ? _wedge : _counter;
            if (!words.Slice(y * _width, _width).SequenceEqual(expected))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Mean absolute difference per colour channel of two rows, in code values.</summary>
    private double RowError(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        long sum = 0;
        for (var x = 0; x < a.Length; x++)
        {
            var p = a[x];
            var q = b[x];
            sum += _tenBit
                ? Math.Abs((int)(p & 0x3FF) - (int)(q & 0x3FF)) + Math.Abs((int)((p >> 10) & 0x3FF) - (int)((q >> 10) & 0x3FF)) + Math.Abs((int)((p >> 20) & 0x3FF) - (int)((q >> 20) & 0x3FF))
                : Math.Abs((int)(p & 0xFF) - (int)(q & 0xFF)) + Math.Abs((int)((p >> 8) & 0xFF) - (int)((q >> 8) & 0xFF)) + Math.Abs((int)((p >> 16) & 0xFF) - (int)((q >> 16) & 0xFF));
        }

        return sum / (3.0 * a.Length);
    }

    private int BoxX(uint frame)
    {
        var boxWidth = Math.Max(_width / 16, 1);
        var step = (uint)Math.Max(_width / 240, 1);
        var travel = (uint)(_width - boxWidth);
        return travel == 0 ? 0 : (int)((frame % travel * step) % travel);
    }

    private void FillBox(Span<uint> row, uint frame)
    {
        // A box max(W/16, 1) wide moving max(W/240, 1) px per frame, wrapping after W - boxWidth px.
        var boxWidth = Math.Max(_width / 16, 1);
        var boxX = BoxX(frame);
        var inside = _tenBit ? 769u : 255u;
        var outside = _tenBit ? 307u : 48u;
        for (var x = 0; x < _width; x++)
        {
            var v = x >= boxX && x < boxX + boxWidth ? inside : outside;
            row[x] = Pack(v, v, v);
        }
    }

    private void FillCounter(Span<uint> row, uint frame)
    {
        // 32 cells W/32 wide, most significant bit first; a set bit is full scale.
        var cellWidth = _width / 32;
        var max = _tenBit ? 1023u : 255u;
        for (var x = 0; x < _width; x++)
        {
            var cell = cellWidth > 0 ? x / cellWidth : 32;
            var v = cell < 32 && ((frame >> (31 - cell)) & 1) != 0 ? max : 0;
            row[x] = Pack(v, v, v);
        }
    }

    private void Mix(Span<uint> a, ReadOnlySpan<uint> b)
    {
        for (var x = 0; x < a.Length; x++)
        {
            var p = a[x];
            var q = b[x];
            a[x] = _tenBit
                ? Mix(p & 0x3FF, q & 0x3FF) | (Mix((p >> 10) & 0x3FF, (q >> 10) & 0x3FF) << 10) | (Mix((p >> 20) & 0x3FF, (q >> 20) & 0x3FF) << 20) | (Mix(p >> 30, q >> 30) << 30)
                : Mix(p & 0xFF, q & 0xFF) | (Mix((p >> 8) & 0xFF, (q >> 8) & 0xFF) << 8) | (Mix((p >> 16) & 0xFF, (q >> 16) & 0xFF) << 16) | (Mix(p >> 24, q >> 24) << 24);
        }
    }

    /// <summary>The blend kernel's rounding: ((65536 − w)·a + w·b + 32768) >> 16.</summary>
    private static uint Mix(uint a, uint b) => (((65536 - HalfWeight) * a) + (HalfWeight * b) + 32768) >> 16;

    private uint Pack(uint r, uint g, uint b) => _tenBit
        ? r | (g << 10) | (b << 20) | (3u << 30)
        : r | (g << 8) | (b << 16) | (255u << 24);
}
