using System.Runtime.InteropServices;
using FrameMotionPlus.Core.Statistics;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Runtime;
using FrameMotionPlus.Platform.Tests.Support;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Platform.Tests.Time;

/// <summary>
/// "Never early" and ordering are asserted strictly. Lateness is only bounded loosely (tens of milliseconds), so a
/// busy 2-core CI runner cannot fail these tests; the real lateness numbers come from <c>fmp bench jitter</c>.
/// </summary>
public class DeadlineWaiterTests
{
    public static TheoryData<WaitStrategy, long> Waiters => new()
    {
        { WaitStrategy.Sleep, 0 },
        { WaitStrategy.Hybrid, 20_000 },
        { WaitStrategy.Hybrid, DeadlineWaiter.DefaultSpinWindowNs },
        { WaitStrategy.Hybrid, 2_000_000 },
    };

    [Theory]
    [MemberData(nameof(Waiters))]
    public void Never_returns_before_the_deadline(WaitStrategy strategy, long spinNs)
    {
        var waiter = new DeadlineWaiter(strategy, spinNs);
        var random = new DeterministicRandom(42);
        for (var i = 0; i < 200; i++)
        {
            var deadline = Monotonic.NowNs + (long)(random.NextDouble() * 1_500_000);
            var woke = waiter.WaitUntil(deadline);
            var after = Monotonic.NowNs;

            Assert.True(woke >= deadline, $"wait {i} returned {deadline - woke} ns early");
            Assert.True(after >= woke, "the returned wake time is in the future");
        }
    }

    [Theory]
    [MemberData(nameof(Waiters))]
    public void A_deadline_in_the_past_returns_at_once_with_the_current_time(WaitStrategy strategy, long spinNs)
    {
        var waiter = new DeadlineWaiter(strategy, spinNs);
        foreach (var deadline in new[] { long.MinValue, 0, Monotonic.NowNs - Nanoseconds.PerSecond, Monotonic.NowNs })
        {
            var before = Monotonic.NowNs;
            var woke = waiter.WaitUntil(deadline);
            var after = Monotonic.NowNs;

            Assert.InRange(woke, before, after);
            Assert.True(after - before < 50 * Nanoseconds.PerMillisecond, $"a past deadline took {after - before} ns");
        }
    }

    [Theory]
    [MemberData(nameof(Waiters))]
    public void Lateness_is_loosely_bounded(WaitStrategy strategy, long spinNs)
    {
        var waiter = new DeadlineWaiter(strategy, spinNs);
        var lateness = new long[41];
        for (var i = 0; i < lateness.Length; i++)
        {
            var deadline = Monotonic.NowNs + Nanoseconds.PerMillisecond;
            lateness[i] = waiter.WaitUntil(deadline) - deadline;
        }

        Array.Sort(lateness);

        // Catches unit mistakes (us for ns would sleep 1 s) without judging the machine.
        Assert.True(lateness[lateness.Length / 2] < 20 * Nanoseconds.PerMillisecond, $"median lateness {lateness[lateness.Length / 2]} ns");
    }

    [Fact]
    public void Hybrid_with_a_window_longer_than_the_wait_spins_to_the_deadline()
    {
        var waiter = DeadlineWaiter.Hybrid(5_000_000);
        var deadline = Monotonic.NowNs + 200_000;

        Assert.True(waiter.WaitUntil(deadline) >= deadline);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "Calls through IClock are what is under test.")]
    public void IClock_SleepUntil_never_returns_early()
    {
        IClock clock = new MonotonicClock(DeadlineWaiter.Hybrid(100_000));
        for (var i = 0; i < 50; i++)
        {
            var deadline = clock.NowNs + 300_000;
            clock.SleepUntil(deadline);
            Assert.True(clock.NowNs >= deadline);
        }
    }

    [Fact]
    public void Constructor_rejects_inconsistent_settings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadlineWaiter(WaitStrategy.Sleep, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadlineWaiter(WaitStrategy.Hybrid, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadlineWaiter(WaitStrategy.Hybrid, DeadlineWaiter.MaxSpinWindowNs + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadlineWaiter((WaitStrategy)7, 0));
        Assert.Equal("sleep", DeadlineWaiter.Sleep.ToString());
        Assert.Equal("hybrid (spin 200 us)", DeadlineWaiter.Hybrid(200_000).ToString());
    }

    [Fact]
    public void Linux_sleeps_use_clock_nanosleep()
    {
        TestPlatform.SkipUnlessLinux();
        Assert.True(DeadlineWaiter.IsPrecise);
    }
}

/// <summary>
/// A signal delivered to a sleeping thread makes <c>clock_nanosleep</c> return EINTR (it is never restarted, whatever
/// SA_RESTART says). The waiter must resume against the same absolute deadline: not return early, not allocate.
/// </summary>
[Collection(AllocationMeasurementDefinition.Name)]
public class DeadlineWaiterSignalTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Linux_signals_during_a_sleep_neither_end_it_early_nor_allocate()
    {
        TestPlatform.SkipUnlessLinux();

        // SIGWINCH is ignored by default (no EINTR then); a registration installs the runtime's handler for it.
        using var registration = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, static context => context.Cancel = true);
        var interruptedBefore = DeadlineWaiter.InterruptedSleeps;
        var waiter = DeadlineWaiter.Sleep;
        _ = waiter.WaitUntil(Monotonic.NowNs + 1000); // warm up

        var tid = 0;
        var ready = new ManualResetEventSlim();
        long deadline = 0, woke = 0, allocated = -1;
        var sleeper = new Thread(() =>
        {
            tid = FrameMotionPlus.Platform.Threading.ThreadScheduling.CurrentOsThreadId;
            _ = waiter.WaitUntil(Monotonic.NowNs + 1000);
            deadline = Monotonic.NowNs + (400 * Nanoseconds.PerMillisecond);
            var before = GcSnapshot.CurrentThreadAllocatedBytes;
            ready.Set();
            woke = waiter.WaitUntil(deadline);
            allocated = GcSnapshot.CurrentThreadAllocatedBytes - before;
        }) { IsBackground = true, Name = "eintr-sleeper" };
        sleeper.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var pid = TestPlatform.GetPid();
        var sent = 0;
        for (var i = 0; i < 5 && sleeper.IsAlive; i++)
        {
            Thread.Sleep(40);
            if (sleeper.IsAlive && TestPlatform.TgKill(pid, tid, TestPlatform.Sigwinch) == 0)
            {
                sent++;
            }
        }

        Assert.True(sleeper.Join(TimeSpan.FromSeconds(10)));
        Assert.True(sent > 0, "no signal could be sent");
        Assert.True(woke >= deadline, $"woke {deadline - woke} ns early");
        Assert.True(DeadlineWaiter.InterruptedSleeps > interruptedBefore, "no sleep was interrupted, so the EINTR path did not run");
        Assert.Equal(0, allocated);
    }
}
