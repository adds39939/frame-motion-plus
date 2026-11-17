using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>
/// Events and span timers. A span starts when the stream reaches <c>Begin</c>; on an idle stream that is at once, so
/// host time spent enqueueing the work (JIT on first use, a busy CPU) would count. Each measured span therefore
/// starts behind a GPU delay that keeps the stream busy until the host has enqueued everything.
/// </summary>
[Collection(SerialGpuDefinition.Name)]
[Trait("Category", "Gpu")]
public class EventTests
{
    private const long Busy = 5_000_000;
    private const long Slack = 2_000_000;

    [Fact]
    public void Events_report_completion_and_elapsed_gpu_time()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var start = CudaEvent.CreateForTiming(env.Context);
        using var end = CudaEvent.CreateForTiming(env.Context);
        Assert.True(start.TimingEnabled);

        env.Kernels.EnqueueDelay(stream, Busy);
        start.Record(stream);
        env.Kernels.EnqueueDelay(stream, 5_000_000);
        end.Record(stream);
        Assert.False(end.IsComplete);
        Assert.False(CudaEvent.TryGetElapsedNanoseconds(start, end, out _));
        Assert.Equal(CudaResult.ErrorNotReady, Assert.Throws<CudaException>(() => CudaEvent.ElapsedNanoseconds(start, end)).Result);

        end.Synchronize();
        Assert.True(end.IsComplete);
        Assert.InRange(CudaEvent.ElapsedNanoseconds(start, end), 5_000_000, 5_000_000 + Slack);
    }

    [Fact]
    public void Sync_events_have_no_timing_and_signal_through_gpu_signal()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var a = CudaEvent.Create(env.Context);
        using var b = CudaEvent.Create(env.Context);
        Assert.False(a.TimingEnabled);
        Assert.True(a.IsComplete); // never recorded

        var signal = a.Signal;
        Assert.Equal(GpuSignalKind.CudaEvent, signal.Kind);
        Assert.Equal(MemoryDomain.CudaDevice, signal.Domain);
        Assert.Equal((ulong)a.Handle, signal.Handle);

        stream.Signal(signal);
        b.Record(stream);
        stream.Synchronize();
        var error = Assert.Throws<CudaException>(() => CudaEvent.ElapsedNanoseconds(a, b));
        Assert.Equal(CudaResult.ErrorInvalidHandle, error.Result);
    }

    [Fact]
    public void Delay_kernel_keeps_the_stream_busy_for_the_requested_time()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var timer = CudaSpanTimer.Create(env.Context);
        Assert.False(timer.TryGetElapsedNanoseconds(out _));
        Assert.Throws<InvalidOperationException>(() => timer.End(stream));

        env.Kernels.EnqueueDelay(stream, Busy);
        timer.Begin(stream);
        env.Kernels.EnqueueDelay(stream, 3_000_000);
        timer.End(stream);
        Assert.False(stream.IsIdle);
        Assert.False(timer.TryGetElapsedNanoseconds(out _));
        var ns = timer.WaitElapsedNanoseconds();
        Assert.InRange(ns, 3_000_000, 3_000_000 + Slack);
        Assert.True(timer.TryGetElapsedNanoseconds(out var again));
        Assert.Equal(ns, again);
        Assert.True(stream.IsIdle);
    }

    [Fact]
    public void Span_timer_ring_reads_spans_in_order_and_counts_drops()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var ring = CudaSpanTimerRing.Create(env.Context, 4);
        Assert.False(ring.TryRead(out _));

        // Delays far enough apart that the order is unambiguous within the slack.
        long[] delays = [1_000_000, 6_000_000, 3_000_000];
        env.Kernels.EnqueueDelay(stream, Busy);
        foreach (var delay in delays)
        {
            ring.Begin(stream);
            env.Kernels.EnqueueDelay(stream, delay);
            ring.End(stream);
        }

        Assert.False(ring.TryRead(out _));
        stream.Synchronize();
        foreach (var delay in delays)
        {
            Assert.True(ring.TryRead(out var ns));
            Assert.InRange(ns, delay, delay + Slack);
        }

        Assert.False(ring.TryRead(out _));

        for (var i = 0; i < 6; i++)
        {
            ring.Begin(stream);
            ring.End(stream);
        }

        stream.Synchronize();
        Assert.Equal(2, ring.Dropped);
        var read = 0;
        while (ring.TryRead(out _))
        {
            read++;
        }

        Assert.Equal(4, read);
        Assert.Throws<InvalidOperationException>(() => ring.End(stream));
        ring.Begin(stream);
        Assert.Throws<InvalidOperationException>(() => ring.Begin(stream));
        ring.Abort();
        ring.Begin(stream);
        ring.End(stream);
        stream.Synchronize();
        Assert.True(ring.TryRead(out _));
    }

    [Fact]
    public void Span_timer_ring_keeps_unread_spans_and_drops_new_ones_when_full()
    {
        var env = Gpu.Require();
        using var stream = CudaStream.Create(env.Context);
        using var ring = CudaSpanTimerRing.Create(env.Context, 2);
        long[] delays = [1_000_000, 3_000_000, 5_000_000];
        env.Kernels.EnqueueDelay(stream, Busy);
        foreach (var delay in delays)
        {
            ring.Begin(stream);
            env.Kernels.EnqueueDelay(stream, delay);
            ring.End(stream);
        }

        stream.Synchronize();
        Assert.Equal(1, ring.Dropped);
        Assert.True(ring.TryRead(out var first));
        Assert.True(ring.TryRead(out var second));
        Assert.False(ring.TryRead(out _));
        Assert.InRange(first, delays[0], delays[0] + Slack);
        Assert.InRange(second, delays[1], delays[1] + Slack);
    }

    [Fact]
    public void Span_timer_ring_is_safe_with_a_writer_and_a_reader_thread()
    {
        var env = Gpu.Require();
        const int Spans = 20_000;
        using var stream = CudaStream.Create(env.Context);
        using var ring = CudaSpanTimerRing.Create(env.Context, 8);
        long read = 0;
        var writing = 1;
        Exception? failure = null;
        var reader = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    if (ring.TryRead(out var ns))
                    {
                        Assert.InRange(ns, 0, 1_000_000_000);
                        read++;
                    }
                    else if (Volatile.Read(ref writing) == 0)
                    {
                        // The writer has finished and synchronised: whatever is left is complete.
                        while (ring.TryRead(out _))
                        {
                            read++;
                        }

                        return;
                    }
                    else
                    {
                        Thread.Yield();
                    }
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        reader.Start();
        for (var i = 0; i < Spans; i++)
        {
            ring.Begin(stream);
            ring.End(stream);
            if (i % 512 == 0)
            {
                stream.Synchronize();
            }
        }

        stream.Synchronize();
        Volatile.Write(ref writing, 0);
        reader.Join();
        Assert.Null(failure);
        Assert.Equal(Spans, read + ring.Dropped);
        Assert.True(read > 0);
    }
}
