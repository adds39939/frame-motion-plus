namespace FrameMotionPlus.Audio.Dsp;

/// <summary>
/// The newest capture timing, published by the capture side and read by the playback side without locks (a sequence
/// lock: the writer never waits; the reader retries a torn read a few times): how many frames have been written to the
/// ring and when, on <c>CLOCK_MONOTONIC</c>, the boundary after the last of them was captured. Frame <c>f</c> was
/// captured at <c>EndNs − (EndFrame − f) / rate</c>.
/// </summary>
public sealed class CaptureTimeline
{
    private int _sequence;
    private long _endFrame;
    private long _endNs;

    /// <summary>Writer (one thread): the frames written so far and the capture time of the boundary after them.</summary>
    public void Publish(long endFrame, long endNs)
    {
        _ = Interlocked.Increment(ref _sequence); // odd: being written (a full fence)
        Volatile.Write(ref _endFrame, endFrame);
        Volatile.Write(ref _endNs, endNs);
        _ = Interlocked.Increment(ref _sequence); // even: consistent
    }

    /// <summary>Reader: a consistent pair; false before the first publication or when every retry was torn.</summary>
    public bool TryRead(out long endFrame, out long endNs)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var before = Volatile.Read(ref _sequence);
            if (before == 0)
            {
                break;
            }

            if ((before & 1) != 0)
            {
                Thread.SpinWait(4);
                continue;
            }

            endFrame = Volatile.Read(ref _endFrame);
            endNs = Volatile.Read(ref _endNs);
            if (Volatile.Read(ref _sequence) == before)
            {
                return true;
            }
        }

        endFrame = 0;
        endNs = 0;
        return false;
    }
}
