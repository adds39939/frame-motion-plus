using FrameMotionPlus.Platform.Tests.Support;
using FrameMotionPlus.Platform.Threading;

namespace FrameMotionPlus.Platform.Tests.Threading;

/// <summary>
/// The helpers must never throw because the OS refused something, and must report exactly what the thread got. Where
/// possible the report is checked against /proc, independently of the code under test. Whether real-time or a
/// negative nice is granted depends on the machine (CI runners and this dev box refuse it; a privileged container
/// may not), so those tests accept either outcome but demand that the report matches reality.
/// </summary>
public class ThreadSchedulingTests
{
    private const int NiceField = 19;
    private const int RtPriorityField = 40;
    private const int PolicyField = 41;

    /// <summary>Runs <paramref name="probe"/> on a dedicated thread while it is alive and configured.</summary>
    private static (ThreadSchedulingReport Report, T Probe) OnDedicatedThread<T>(
        string name,
        ThreadSchedulingRequest request,
        Func<int, T> probe)
    {
        using var hold = new ManualResetEventSlim();
        var thread = DedicatedThread.Start(name, request, () => hold.Wait(TimeSpan.FromSeconds(30)));
        try
        {
            return (thread.Report, probe(thread.Report.OsThreadId));
        }
        finally
        {
            hold.Set();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
            thread.ThrowIfFailed();
        }
    }

    [Fact]
    public void A_dedicated_thread_is_its_own_named_thread_not_a_pool_thread()
    {
        int callerId = Environment.CurrentManagedThreadId, bodyId = 0;
        var pool = true;
        var thread = DedicatedThread.Start("fmp-test-named", ThreadSchedulingRequest.Default, () =>
        {
            bodyId = Environment.CurrentManagedThreadId;
            pool = Thread.CurrentThread.IsThreadPoolThread;
        });

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        thread.ThrowIfFailed();
        Assert.NotEqual(callerId, bodyId);
        Assert.False(pool);
        Assert.Equal("fmp-test-named", thread.Name);
        Assert.Equal("fmp-test-named", thread.Report.Name);
    }

    [Fact]
    public void Linux_os_thread_name_is_set_and_truncated_to_15_bytes()
    {
        TestPlatform.SkipUnlessLinux();

        var (report, comm) = OnDedicatedThread("fmp-a-very-long-thread-name", ThreadSchedulingRequest.Default, TestPlatform.ThreadComm);

        Assert.NotEqual(0, report.OsThreadId);
        Assert.Equal("fmp-a-very-long", comm);
    }

    [Theory]
    [InlineData(RealTimePolicy.Fifo)]
    [InlineData(RealTimePolicy.RoundRobin)]
    public void Real_time_request_never_throws_and_reports_what_the_thread_got(RealTimePolicy policy)
    {
        var request = new ThreadSchedulingRequest { RealTime = policy, RealTimePriority = 5 };
        var (report, proc) = OnDedicatedThread("fmp-test-rt", request, tid =>
            TestPlatform.IsLinux64 ? (TestPlatform.ThreadStatField(tid, PolicyField), TestPlatform.ThreadStatField(tid, RtPriorityField)) : (-1L, -1L));

        Assert.Equal(policy, report.Requested.RealTime);
        if (report.RealTimeObtained)
        {
            Assert.Null(report.RealTimeError);
            Assert.Equal(policy, report.RealTime);
            Assert.InRange(report.RealTimePriority, 1, 5);
        }
        else
        {
            Assert.NotNull(report.RealTimeError);
            Assert.Equal(0, report.RealTimePriority);
            Assert.False(report.FullyGranted);
        }

        if (TestPlatform.IsLinux64)
        {
            // /proc: policy 0 SCHED_OTHER, 1 SCHED_FIFO, 2 SCHED_RR.
            Assert.Equal(report.RealTimeObtained ? (long)policy : 0L, proc.Item1);
            Assert.Equal(report.RealTimePriority, proc.Item2);
            Assert.Equal(report.RealTimeObtained ? (policy == RealTimePolicy.Fifo ? "SCHED_FIFO" : "SCHED_RR") : "SCHED_OTHER", report.Policy);
            if (!report.RealTimeObtained)
            {
                Assert.Contains("EPERM", report.RealTimeError, StringComparison.Ordinal);
                Assert.NotEmpty(report.Advice);
            }
        }
    }

    [Fact]
    public void Refused_real_time_falls_back_to_the_nice_route_and_reports_it()
    {
        TestPlatform.SkipUnlessLinux();
        var request = new ThreadSchedulingRequest { RealTime = RealTimePolicy.Fifo, Nice = -5 };

        var (report, nice) = OnDedicatedThread("fmp-test-fb", request, tid => TestPlatform.ThreadStatField(tid, NiceField));

        if (report.RealTimeObtained)
        {
            Assert.Null(report.NiceError); // the nice route is not taken when real-time is granted
            return;
        }

        Assert.Equal((int)nice, report.Nice);
        if (nice == -5)
        {
            Assert.Null(report.NiceError);
        }
        else
        {
            Assert.NotNull(report.NiceError);
            Assert.InRange(nice, -5L, 0L); // unchanged, or lowered as far as RLIMIT_NICE allows
        }
    }

    [Fact]
    public void Linux_raising_nice_is_always_allowed_and_applies_to_that_thread_only()
    {
        TestPlatform.SkipUnlessLinux();
        var callerNice = TestPlatform.ThreadStatField(ThreadScheduling.CurrentOsThreadId, NiceField);

        var (report, nice) = OnDedicatedThread("fmp-test-nice", new ThreadSchedulingRequest { Nice = 5 }, tid => TestPlatform.ThreadStatField(tid, NiceField));

        Assert.Equal(5, nice);
        Assert.Equal(5, report.Nice);
        Assert.Null(report.NiceError);
        Assert.True(report.FullyGranted);
        Assert.Equal(callerNice, TestPlatform.ThreadStatField(ThreadScheduling.CurrentOsThreadId, NiceField));
    }

    [Fact]
    public void Linux_pinning_to_an_allowed_cpu_keeps_the_thread_there()
    {
        TestPlatform.SkipUnlessLinux();
        var allowed = ThreadScheduling.AllowedCpus();
        Assert.NotEmpty(allowed);
        var core = allowed[^1];

        int[] seen = [];
        var thread = DedicatedThread.Start("fmp-test-pin", new ThreadSchedulingRequest { Core = core }, () =>
        {
            var cpus = new int[200];
            for (var i = 0; i < cpus.Length; i++)
            {
                cpus[i] = ThreadScheduling.CurrentCpu;
                Thread.Yield();
            }

            seen = cpus;
        });
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        thread.ThrowIfFailed();

        Assert.Equal(core, thread.Report.Core);
        Assert.Null(thread.Report.AffinityError);
        Assert.All(seen, cpu => Assert.Equal(core, cpu));
    }

    [Fact]
    public void Pinning_to_an_impossible_cpu_is_reported_not_thrown()
    {
        var thread = DedicatedThread.Start("fmp-test-badpin", new ThreadSchedulingRequest { Core = 5000 }, static () => { });
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

        Assert.NotNull(thread.Report.AffinityError);
        Assert.NotEqual(5000, thread.Report.Core);
        Assert.False(thread.Report.FullyGranted);
    }

    [Fact]
    public void Linux_pinning_to_a_cpu_that_does_not_exist_reports_EINVAL()
    {
        TestPlatform.SkipUnlessLinux();
        var allowed = ThreadScheduling.AllowedCpus();

        // Not merely outside the current mask: a thread may widen its affinity to any CPU of its cpuset.
        var possible = File.ReadAllText("/sys/devices/system/cpu/possible").Trim();
        var lastPossible = int.Parse(possible[(possible.LastIndexOfAny(['-', ',']) + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        var thread = DedicatedThread.Start("fmp-test-einval", new ThreadSchedulingRequest { Core = lastPossible + 1 }, static () => { });
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

        Assert.StartsWith("EINVAL", thread.Report.AffinityError, StringComparison.Ordinal);
        Assert.Equal(string.Join(',', allowed), ExpandCpuList(thread.Report.AllowedCpus!));
    }

    [Fact]
    public void Linux_timer_slack_is_applied_per_thread()
    {
        TestPlatform.SkipUnlessLinux();

        var (precise, _) = OnDedicatedThread("fmp-test-slack1", new ThreadSchedulingRequest { TimerSlackNs = 1 }, static _ => 0);
        var (usual, _) = OnDedicatedThread("fmp-test-slack50", new ThreadSchedulingRequest { TimerSlackNs = 50_000 }, static _ => 0);

        Assert.Equal(1, precise.TimerSlackNs);
        Assert.Null(precise.TimerSlackError);
        Assert.True(precise.FullyGranted);
        Assert.Equal(50_000, usual.TimerSlackNs);
        Assert.True(usual.FullyGranted);
    }

    /// <summary>
    /// A new thread's slack, and what "0" would reset it to, is its creator's slack. So a value is always set explicitly
    /// (0 is rejected), and a thread started from a 1 ns thread asking for the usual 50 us gets exactly that.
    /// </summary>
    [Fact]
    public void Linux_timer_slack_is_inherited_from_the_creating_thread_so_it_is_set_explicitly()
    {
        TestPlatform.SkipUnlessLinux();
        ThreadSchedulingReport? inherited = null, explicitUsual = null;
        var outer = DedicatedThread.Start("fmp-test-outer", new ThreadSchedulingRequest { TimerSlackNs = 1 }, () =>
        {
            var a = DedicatedThread.Start("fmp-test-inh", ThreadSchedulingRequest.Default with { TimerSlackNs = null }, static () => { });
            var b = DedicatedThread.Start("fmp-test-50us", new ThreadSchedulingRequest { TimerSlackNs = 50_000 }, static () => { });
            a.Join();
            b.Join();
            inherited = a.Report;
            explicitUsual = b.Report;
        });
        Assert.True(outer.Join(TimeSpan.FromSeconds(30)));
        outer.ThrowIfFailed();

        Assert.Equal(1, inherited!.TimerSlackNs);
        Assert.Equal(50_000, explicitUsual!.TimerSlackNs);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThreadSchedulingRequest { TimerSlackNs = 0 }.Validate());
    }

    [Fact]
    public void Linux_affinity_read_grows_its_buffer_when_the_kernel_says_it_is_too_small()
    {
        TestPlatform.SkipUnlessLinux();

        // A zero-length first try is refused with EINVAL, as a 1024-CPU buffer is on a kernel configured for more.
        var grown = ThreadScheduling.ReadAffinityMask(initialWords: 0);
        var normal = ThreadScheduling.ReadAffinityMask();

        Assert.NotNull(grown);
        Assert.NotNull(normal);
        Assert.Equal(normal.SelectMany(BitsOf), grown.SelectMany(BitsOf));
        Assert.Equal(ThreadScheduling.AllowedCpus().Length, grown.Sum(static w => System.Numerics.BitOperations.PopCount(w)));
    }

    private static IEnumerable<int> BitsOf(ulong word, int index) =>
        Enumerable.Range(0, 64).Where(b => (word & (1UL << b)) != 0).Select(b => (index * 64) + b);

    [Fact]
    public void Other_os_requests_map_to_managed_priority_without_throwing()
    {
        Assert.SkipWhen(TestPlatform.IsLinux64, "covered by the Linux tests");
        var request = new ThreadSchedulingRequest { RealTime = RealTimePolicy.Fifo, Core = 0 };

        var (report, _) = OnDedicatedThread("fmp-test-portable", request, static _ => 0);

        Assert.StartsWith("thread priority", report.Policy, StringComparison.Ordinal);
        Assert.NotNull(report.RealTimeError);
        Assert.NotNull(report.AffinityError);
    }

    [Fact]
    public void An_exception_in_the_body_is_captured_not_fatal()
    {
        var thread = DedicatedThread.Start("fmp-test-throw", ThreadSchedulingRequest.Default, static () => throw new InvalidOperationException("boom"));

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.IsType<InvalidOperationException>(thread.Failure);
        Assert.Throws<InvalidOperationException>(thread.ThrowIfFailed);
    }

    [Fact]
    public void Invalid_requests_are_rejected_before_a_thread_starts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DedicatedThread.Start("x", new ThreadSchedulingRequest { RealTimePriority = 0 }, static () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DedicatedThread.Start("x", new ThreadSchedulingRequest { RealTimePriority = 100 }, static () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DedicatedThread.Start("x", new ThreadSchedulingRequest { Nice = -21 }, static () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DedicatedThread.Start("x", new ThreadSchedulingRequest { Core = -1 }, static () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DedicatedThread.Start("x", new ThreadSchedulingRequest { TimerSlackNs = -1 }, static () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DedicatedThread.Start("x", new ThreadSchedulingRequest { TimerSlackNs = 0 }, static () => { }));
        Assert.Throws<ArgumentException>(() => DedicatedThread.Start("", ThreadSchedulingRequest.Default, static () => { }));
    }

    [Fact]
    public void Describing_the_current_thread_changes_nothing()
    {
        var report = ThreadScheduling.DescribeCurrentThread();

        Assert.False(report.RealTimeObtained);
        Assert.Empty(report.Problems);
        if (TestPlatform.IsLinux64)
        {
            Assert.Equal("SCHED_OTHER", report.Policy);
            Assert.Equal(ThreadScheduling.CurrentOsThreadId, report.OsThreadId);
            Assert.NotNull(report.AllowedCpus);
        }
    }

    private static string ExpandCpuList(string list) => string.Join(',', list.Split(',').SelectMany(static part =>
    {
        var dash = part.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return new[] { int.Parse(part, System.Globalization.CultureInfo.InvariantCulture) };
        }

        var from = int.Parse(part.AsSpan(0, dash), System.Globalization.CultureInfo.InvariantCulture);
        var to = int.Parse(part.AsSpan(dash + 1), System.Globalization.CultureInfo.InvariantCulture);
        return Enumerable.Range(from, to - from + 1).ToArray();
    }));
}
