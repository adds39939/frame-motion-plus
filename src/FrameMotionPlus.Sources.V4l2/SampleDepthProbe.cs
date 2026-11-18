namespace FrameMotionPlus.Sources.V4l2;

/// <summary>What a P010 frame's samples say about the signal's bit depth.</summary>
public enum SampleDepth
{
    /// <summary>Too little picture to tell (black, flat, or too few distinct values).</summary>
    Inconclusive = 0,

    /// <summary>Every sampled code's two low bits are zero over a varied picture: an 8-bit signal put into P010.</summary>
    EightBit = 1,

    /// <summary>The two low bits are in use: a signal of 10 or more bits.</summary>
    TenBit = 2,
}

/// <summary>One frame's sample-depth verdict and what it rests on.</summary>
public readonly record struct SampleDepthReading(SampleDepth Depth, int Samples, int LowBitSamples, int DistinctCodes);

/// <summary>
/// Tells an 8-bit signal from a 10-bit one in a P010 capture by its luma codes. The Elgato 4K X captures an 8-bit HDMI
/// signal into P010 with the two low bits of every 10-bit code zero (measured 2026-11-18: an SDR PS5 signal, every luma
/// and chroma code a multiple of 4 at 2160p30 and 1440p60), and a 10-bit one with them in use (the same day's HDR captures:
/// brightest-cell means in sixteenths, impossible with codes in steps of 4). A PS5 sends SDR in 8 bits and HDR10 in 10 or
/// more, so on this setup the depth says SDR or HDR. (A console set to send SDR with more than 8 bits would read as 10-bit.)
/// <para>
/// A frame is judged from a grid of luma samples (<see cref="Columns"/> x <see cref="Rows"/>) read from the capture buffer
/// on the CPU while the GPU uploads it (no added latency): samples at or below black + 16 codes are left out; with at least
/// <see cref="MinSamples"/> left, 8-bit is "no low bits set over at least <see cref="MinDistinct"/> distinct codes" (a
/// varied 10-bit picture shows low bits on about three quarters of its samples; that 16 distinct 10-bit codes all fall on
/// multiples of 4 by chance is about 1 in 4^16), 10-bit is "low bits on at least 1/16 of them". Allocation-free.
/// </para>
/// </summary>
public static class SampleDepthProbe
{
    public const int Columns = 64;
    public const int Rows = 32;
    public const int MinSamples = 256;
    public const int MinDistinct = 16;

    /// <summary>
    /// Judges the P010 luma plane at <paramref name="luma"/> (16-bit little-endian samples, 10 significant bits high,
    /// <paramref name="pitchBytes"/> per row). <paramref name="blackCode"/>: the 10-bit code of black (64 limited, 0 full).
    /// </summary>
    public static unsafe SampleDepthReading Judge(byte* luma, int pitchBytes, int width, int height, int blackCode)
    {
        if (luma == null || width < Columns || height < Rows)
        {
            return default;
        }

        var seen = stackalloc ulong[16];
        new Span<ulong>(seen, 16).Clear();
        var samples = 0;
        var low = 0;
        var floor = blackCode + 16;
        for (var r = 0; r < Rows; r++)
        {
            // Rows and columns at the centres of an even grid, so borders and letterbox bars weigh little.
            var y = ((2 * r) + 1) * height / (2 * Rows);
            var row = (ushort*)(luma + ((long)y * pitchBytes));
            for (var c = 0; c < Columns; c++)
            {
                var x = ((2 * c) + 1) * width / (2 * Columns);
                var code = row[x] >> 6;
                if (code <= floor)
                {
                    continue;
                }

                samples++;
                low += (code & 3) != 0 ? 1 : 0;
                seen[code >> 6] |= 1UL << (code & 63);
            }
        }

        var distinct = 0;
        for (var i = 0; i < 16; i++)
        {
            distinct += System.Numerics.BitOperations.PopCount(seen[i]);
        }

        var depth = samples < MinSamples ? SampleDepth.Inconclusive
            : low == 0 ? (distinct >= MinDistinct ? SampleDepth.EightBit : SampleDepth.Inconclusive)
            : low * 16 >= samples ? SampleDepth.TenBit
            : SampleDepth.Inconclusive;
        return new SampleDepthReading(depth, samples, low, distinct);
    }
}

/// <summary>
/// Follows a capture's sample depth: switches between HDR10 (10-bit) and SDR (8-bit) after <see cref="AgreeFrames"/>
/// conclusive frames in a row disagree with the current state; the first conclusive frame of a capture decides alone (so
/// a capture that starts on an SDR signal shows its first picture as SDR). Inconclusive frames change nothing. Pure.
/// </summary>
public sealed class SampleDepthFollower
{
    /// <summary>Conclusive frames in a row it takes to switch (about 0.1 s at 30 fps).</summary>
    public const int AgreeFrames = 3;

    private int _against;
    private bool _decided;

    public SampleDepthFollower(bool hdr)
    {
        IsHdr = hdr;
    }

    public bool IsHdr { get; private set; }

    public long TenBitFrames { get; private set; }

    public long EightBitFrames { get; private set; }

    public long InconclusiveFrames { get; private set; }

    /// <summary>Back to following from <paramref name="hdr"/> (after a manual choice), with no evidence counted yet.</summary>
    public void Reset(bool hdr)
    {
        IsHdr = hdr;
        _against = 0;
        _decided = true;
    }

    /// <summary>Feeds one frame's verdict; true when it switched (<see cref="IsHdr"/> is the new state).</summary>
    public bool Feed(SampleDepth depth)
    {
        switch (depth)
        {
            case SampleDepth.TenBit:
                TenBitFrames++;
                break;
            case SampleDepth.EightBit:
                EightBitFrames++;
                break;
            default:
                InconclusiveFrames++;
                return false;
        }

        var first = !_decided;
        _decided = true;
        if ((depth == SampleDepth.TenBit) == IsHdr)
        {
            _against = 0;
            return false;
        }

        if (++_against < AgreeFrames && !first)
        {
            return false;
        }

        IsHdr = !IsHdr;
        _against = 0;
        return true;
    }
}

/// <summary>A colour change asked of a running capture (<see cref="V4l2FrameSource.RequestColor"/>).</summary>
public enum CaptureColorRequest
{
    None = 0,

    /// <summary>To the other of SDR and HDR10, by hand (stops following).</summary>
    Toggle = 1,

    /// <summary>HDR10, by hand.</summary>
    Hdr = 2,

    /// <summary>SDR, by hand.</summary>
    Sdr = 3,

    /// <summary>Follow the signal's sample depth again (P010 captures).</summary>
    Follow = 4,
}

/// <summary>Why a capture's colour changed.</summary>
public enum ColorSwitchCause
{
    /// <summary>The sample-depth follower saw the signal change.</summary>
    Followed = 0,

    /// <summary>Asked for by hand.</summary>
    Manual = 1,
}

/// <summary>One change of a capture's colour: the capture timestamp of the first frame in the new colour, the colour, the cause.</summary>
public readonly record struct ColorSwitch(long TimestampNs, bool ToHdr, ColorSwitchCause Cause, SampleDepthReading Depth);
