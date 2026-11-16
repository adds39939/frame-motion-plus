using System.Diagnostics;
using System.Runtime.CompilerServices;
using FrameMotionPlus.Core.Collections;

namespace FrameMotionPlus.Core.Tests.Collections;

public class SpscRingTests
{
    private readonly record struct Payload(long Sequence, long Square, long Inverse, double Time);

    [Fact]
    public void Is_fifo_and_reports_full_and_empty()
    {
        var ring = new SpscRing<int>(4);
        Assert.True(ring.IsEmpty);
        Assert.False(ring.TryDequeue(out _));
        for (var i = 0; i < 4; i++)
        {
            Assert.True(ring.TryEnqueue(i));
        }

        Assert.False(ring.TryEnqueue(99));
        Assert.Equal(4, ring.Count);
        Assert.True(ring.TryPeek(out var peeked));
        Assert.Equal(0, peeked);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(ring.TryDequeue(out var v));
            Assert.Equal(i, v);
        }

        Assert.False(ring.TryDequeue(out _));
    }

    [Fact]
    public void Wraps_around_many_times()
    {
        var ring = new SpscRing<long>(8);
        long next = 0, expect = 0;
        for (var round = 0; round < 10_000; round++)
        {
            var n = round % 8;
            for (var i = 0; i < n; i++)
            {
                Assert.True(ring.TryEnqueue(next++));
            }

            for (var i = 0; i < n; i++)
            {
                Assert.True(ring.TryDequeue(out var v));
                Assert.Equal(expect++, v);
            }
        }
    }

    [Fact]
    public void Dequeue_latest_keeps_the_newest_and_counts_the_rest()
    {
        var ring = new SpscRing<int>(8);
        for (var i = 1; i <= 5; i++)
        {
            ring.TryEnqueue(i);
        }

        Assert.True(ring.TryDequeueLatest(out var latest, out var discarded));
        Assert.Equal(5, latest);
        Assert.Equal(4, discarded);
        Assert.True(ring.IsEmpty);
        Assert.False(ring.TryDequeueLatest(out _, out _));
    }

    [Fact]
    public void Releases_references_on_dequeue()
    {
        var ring = new SpscRing<object>(2);
        var weak = Enqueue(ring);
        Assert.True(Dequeue(ring));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Enqueue(SpscRing<object> ring)
    {
        var o = new object();
        ring.TryEnqueue(o);
        return new WeakReference(o);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Dequeue(SpscRing<object> ring) => ring.TryDequeue(out _);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(100)]
    public void Capacity_must_be_a_power_of_two_of_at_least_two(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpscRing<int>(capacity));

    [Fact]
    public void Two_threads_transfer_millions_of_items_in_order_without_loss_or_tearing()
    {
        const long Count = 5_000_000;
        var ring = new SpscRing<Payload>(16);
        var failures = 0L;
        long received = 0, sum = 0;

        // Both loops give up at the deadline, so a ring bug fails the test instead of leaving threads spinning.
        var deadline = Stopwatch.GetTimestamp() + (60 * Stopwatch.Frequency);
        var producer = new Thread(() =>
        {
            var spin = new SpinWait();
            for (long i = 0; i < Count; i++)
            {
                var item = new Payload(i, i * i, ~i, i * 0.5);
                while (!ring.TryEnqueue(item))
                {
                    if (Stopwatch.GetTimestamp() > deadline)
                    {
                        return;
                    }

                    spin.SpinOnce(-1);
                }
            }
        }) { IsBackground = true, Name = "spsc-producer" };

        var consumer = new Thread(() =>
        {
            var spin = new SpinWait();
            long expect = 0;
            while (expect < Count)
            {
                if (!ring.TryDequeue(out var item))
                {
                    if (Stopwatch.GetTimestamp() > deadline)
                    {
                        break;
                    }

                    spin.SpinOnce(-1);
                    continue;
                }

                if (item.Sequence != expect || item.Square != expect * expect || item.Inverse != ~expect || item.Time != expect * 0.5)
                {
                    Interlocked.Increment(ref failures);
                }

                sum += item.Sequence;
                expect++;
            }

            received = expect;
        }) { IsBackground = true, Name = "spsc-consumer" };

        producer.Start();
        consumer.Start();
        Assert.True(producer.Join(TimeSpan.FromSeconds(90)), "producer timed out");
        Assert.True(consumer.Join(TimeSpan.FromSeconds(90)), "consumer timed out");

        Assert.Equal(0, Interlocked.Read(ref failures));
        Assert.Equal(Count, received);
        Assert.Equal(Count * (Count - 1) / 2, sum);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public void Concurrent_count_never_exceeds_capacity()
    {
        const int Count = 1_000_000;
        var result = TransferWithObserver(new SpscRing<int>(64), Count, Count, TimeSpan.FromSeconds(60));

        Assert.True(result.Completed, $"timed out after {result.Received} of {Count} items in {result.Elapsed}");
        Assert.True(result.HelpersStopped, "producer or observer thread still running");
        Assert.Equal(0, result.OutOfOrder);
        Assert.Equal(0, result.BadCounts);
        Assert.True(result.Observations > 0, "the observer never sampled Count");
    }

    [Fact]
    public void Stress_transfer_fails_by_its_deadline_instead_of_hanging_when_items_go_missing()
    {
        // Stands in for a ring bug that loses items: the producer stops early. The consumer must give up at its
        // deadline, so the stress test fails instead of hanging CI, and every helper thread must stop.
        var timeout = TimeSpan.FromMilliseconds(300);
        var result = TransferWithObserver(new SpscRing<int>(64), count: 10_000, produce: 4_000, timeout);

        Assert.False(result.Completed);
        Assert.Equal(4_000, result.Received);
        Assert.InRange(result.Elapsed, timeout, timeout + TimeSpan.FromSeconds(20));
        Assert.True(result.HelpersStopped, "producer or observer thread still running");
    }

    private readonly record struct TransferResult(
        bool Completed, int Received, int OutOfOrder, long BadCounts, long Observations, TimeSpan Elapsed, bool HelpersStopped);

    /// <summary>
    /// Producer thread enqueues 0..<paramref name="produce"/>-1, this thread dequeues until <paramref name="count"/>
    /// items or <paramref name="timeout"/>, and an observer thread samples <see cref="SpscRing{T}.Count"/> meanwhile.
    /// Every wait yields, so the three threads also make progress on a single core.
    /// </summary>
    private static TransferResult TransferWithObserver(SpscRing<int> ring, int count, int produce, TimeSpan timeout)
    {
        var bad = 0L;
        var observations = 0L;
        var done = 0;
        var observer = new Thread(() =>
        {
            long seen = 0;
            while (Volatile.Read(ref done) == 0)
            {
                var c = ring.Count;
                if (c < 0 || c > ring.Capacity)
                {
                    Interlocked.Increment(ref bad);
                }

                seen++;
                Thread.Yield();
            }

            Interlocked.Exchange(ref observations, seen);
        }) { IsBackground = true, Name = "spsc-observer" };
        var producer = new Thread(() =>
        {
            var spin = new SpinWait();
            for (var i = 0; i < produce; i++)
            {
                while (!ring.TryEnqueue(i))
                {
                    if (Volatile.Read(ref done) != 0)
                    {
                        return;
                    }

                    spin.SpinOnce(sleep1Threshold: -1);
                }

                spin.Reset();
            }
        }) { IsBackground = true, Name = "spsc-producer" };

        var started = Stopwatch.GetTimestamp();
        observer.Start();
        producer.Start();
        var got = 0;
        var outOfOrder = 0;
        var wait = new SpinWait();
        while (got < count)
        {
            if (ring.TryDequeue(out var v))
            {
                if (v != got)
                {
                    outOfOrder++;
                }

                got++;
                wait.Reset();
                continue;
            }

            if (Stopwatch.GetElapsedTime(started) > timeout)
            {
                break;
            }

            wait.SpinOnce(sleep1Threshold: -1);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        Volatile.Write(ref done, 1);
        var stopped = producer.Join(TimeSpan.FromSeconds(30)) & observer.Join(TimeSpan.FromSeconds(30));
        return new TransferResult(got == count, got, outOfOrder, Interlocked.Read(ref bad), Interlocked.Read(ref observations), elapsed, stopped);
    }
}
