using FrameMotionPlus.Audio.Dsp;

namespace FrameMotionPlus.Audio.Tests;

/// <summary>
/// The delay line's two sides allocate nothing per block, exactly 0 bytes, over every path: playing and resampling,
/// a cross-faded jump, an underrun and its resynchronisation, a capture gap, mute and unmute.
/// </summary>
public class AllocationTests
{
    [Fact]
    public void The_delay_line_allocates_nothing_per_block_on_any_path()
    {
        var line = new DelayLine(new DelayLineOptions { SettleNs = 0 });
        line.SetVideoLatency(40_000_000);
        var capture = new float[256 * 2];
        var output = new float[256 * 2];
        for (var i = 0; i < capture.Length; i++)
        {
            capture[i] = (float)Math.Sin(i * 0.01);
        }

        const long blockNs = 5_333_333;
        var t = 1_000_000_000L;
        var heardOffset = 7_000_000L;

        void Turn(long index, bool skipCapture, long playbackShift)
        {
            if (!skipCapture)
            {
                line.OnCapture(capture, t - 8_000_000, 8_000_000);
            }

            line.OnPlayback(output, 256, t + heardOffset + playbackShift, 5_333_333);
            t += blockNs + (index % 3); // a little drift
        }

        for (var i = 0; i < 3000; i++)
        {
            Turn(i, false, 0);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20_000; i++)
        {
            var skip = i is >= 5000 and < 5200 || i == 9000; // an outage (underrun, discontinuity, resync) and one lost block (a gap)
            line.SetMuted(i is >= 12_000 and < 12_400);
            var shift = i >= 15_000 ? 60_000_000L : 0; // the sink's timeline jumps: a cross-faded jump
            Turn(i, skip, shift);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);

        var s = line.Statistics;
        Assert.True(s.Underruns >= 1, "the outage underran");
        Assert.True(s.Resyncs >= 1, "and resynchronised");
        Assert.True(s.CaptureGaps >= 1, "the lost block was filled");
        Assert.True(s.MutedBlocks >= 300, "mute ran");
        Assert.True(s.Jumps >= 1, "the sink jump was cross-faded");
        Assert.True(s.MeasuredBlocks > 1000);
    }
}
