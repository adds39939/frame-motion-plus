namespace FrameMotionPlus.Audio.Dsp;

/// <summary>
/// Reads the ring at a fractional position that advances by a ratio per output frame (input frames consumed per frame
/// played; 1 plays at the input's own rate), through <see cref="SincKernel"/>. The ratio is ramped linearly across each
/// block, so a change of ratio is never a step. Two ways to move the position without a click: <see cref="CrossFadeTo"/>
/// (a second reader runs on at the old position and the two are mixed with an equal-gain cross-fade: the weights sum to 1,
/// so the output never exceeds the louder of the two; the two are the same sound shifted a little, i.e. correlated, where
/// an equal-power fade would be 3 dB loud for its 20 ms) and
/// <see cref="Seek"/> combined with a gain ramp (fade out, move, fade in: for resynchronising after the data ran out).
/// Single-threaded (the playback side); allocation-free after construction.
/// </summary>
public sealed class ResamplingReader
{
    private readonly SincKernel _kernel = SincKernel.Instance;
    private readonly float[] _coefficients = new float[SincKernel.Taps];
    private long _index;
    private double _fraction;
    private long _oldIndex;
    private double _oldFraction;
    private int _fadeLeft;
    private int _fadeLength;
    private float _gain;
    private float _gainTarget;
    private float _gainStep;

    /// <summary>The read position in absolute input frames (whole part and fraction).</summary>
    public double Position => _index + _fraction;

    public long Index => _index;

    public double Fraction => _fraction;

    /// <summary>A cross-fade from an earlier position is still running.</summary>
    public bool CrossFading => _fadeLeft > 0;

    public float Gain => _gain;

    /// <summary>Moves the read position at once (no cross-fade): pair it with a gain of 0 and a fade in.</summary>
    public void Seek(double position)
    {
        _index = (long)Math.Floor(position);
        _fraction = position - _index;
        _fadeLeft = 0;
    }

    /// <summary>Moves the read position to <paramref name="position"/>, cross-fading from the current one over <paramref name="frames"/> frames.</summary>
    public void CrossFadeTo(double position, int frames)
    {
        if (frames <= 0)
        {
            Seek(position);
            return;
        }

        _oldIndex = _index;
        _oldFraction = _fraction;
        _index = (long)Math.Floor(position);
        _fraction = position - _index;
        _fadeLeft = frames;
        _fadeLength = frames;
    }

    /// <summary>Sets the output gain at once.</summary>
    public void SetGain(float gain)
    {
        _gain = gain;
        _gainTarget = gain;
        _gainStep = 0;
    }

    /// <summary>Ramps the output gain linearly to <paramref name="target"/> over <paramref name="frames"/> output frames.</summary>
    public void RampGain(float target, int frames)
    {
        _gainTarget = target;
        _gainStep = frames <= 0 ? float.PositiveInfinity : Math.Abs(target - _gain) / frames;
        if (frames <= 0)
        {
            _gain = target;
        }
    }

    /// <summary>The oldest absolute input frame the next render reads.</summary>
    public long FirstFrameNeeded() => Math.Min(_index, _fadeLeft > 0 ? _oldIndex : long.MaxValue) - SincKernel.Before;

    /// <summary>The end (exclusive) of the input frames a render of <paramref name="frames"/> at up to <paramref name="maxRatio"/> reads.</summary>
    public long EndFrameNeeded(int frames, double maxRatio)
    {
        var from = Math.Max(Position, _fadeLeft > 0 ? _oldIndex + _oldFraction : double.MinValue);
        return (long)Math.Ceiling(from + (frames * maxRatio)) + SincKernel.After + 1;
    }

    /// <summary>
    /// Renders <paramref name="frames"/> interleaved stereo frames into <paramref name="output"/>, the ratio going linearly
    /// from <paramref name="ratioFrom"/> to <paramref name="ratioTo"/> across the block. The caller has checked that every
    /// frame read (<see cref="FirstFrameNeeded"/> to <see cref="EndFrameNeeded"/>) is in the ring.
    /// </summary>
    public void Render(SampleRing ring, Span<float> output, int frames, double ratioFrom, double ratioTo)
    {
        var c = _coefficients.AsSpan();
        var step = frames > 0 ? (ratioTo - ratioFrom) / frames : 0;
        for (var i = 0; i < frames; i++)
        {
            var ratio = ratioFrom + (step * i);
            Sample(ring, _index, _fraction, c, out var left, out var right);
            if (_fadeLeft > 0)
            {
                Sample(ring, _oldIndex, _oldFraction, c, out var oldLeft, out var oldRight);
                var gNew = 1f - (_fadeLeft / (float)_fadeLength);
                var gOld = 1f - gNew;
                left = (gNew * left) + (gOld * oldLeft);
                right = (gNew * right) + (gOld * oldRight);
                Advance(ref _oldIndex, ref _oldFraction, ratio);
                _fadeLeft--;
            }

            if (_gain != _gainTarget)
            {
                _gain = _gain < _gainTarget ? Math.Min(_gainTarget, _gain + _gainStep) : Math.Max(_gainTarget, _gain - _gainStep);
            }

            output[2 * i] = left * _gain;
            output[(2 * i) + 1] = right * _gain;
            Advance(ref _index, ref _fraction, ratio);
        }
    }

    private void Sample(SampleRing ring, long index, double fraction, Span<float> c, out float left, out float right)
    {
        _kernel.Coefficients(fraction, c);
        var first = index - SincKernel.Before;
        float l = 0, r = 0;
        for (var k = 0; k < SincKernel.Taps; k++)
        {
            var h = c[k];
            if (h != 0f)
            {
                l += h * ring.Read(first + k, 0);
                r += h * ring.Read(first + k, 1);
            }
        }

        left = l;
        right = r;
    }

    private static void Advance(ref long index, ref double fraction, double ratio)
    {
        fraction += ratio;
        var whole = (long)Math.Floor(fraction);
        index += whole;
        fraction -= whole;
    }
}
