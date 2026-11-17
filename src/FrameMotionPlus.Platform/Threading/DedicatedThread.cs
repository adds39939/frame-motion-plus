using System.Runtime.ExceptionServices;

namespace FrameMotionPlus.Platform.Threading;

/// <summary>
/// A named OS thread of its own (never the thread pool) for capture, scheduling or present. Before the body runs, the
/// thread applies its <see cref="ThreadSchedulingRequest"/> to itself; <see cref="Start"/> returns once that is done,
/// so <see cref="Report"/> is available at once. An exception escaping the body is captured (see
/// <see cref="Failure"/>) instead of taking the process down.
/// </summary>
public sealed class DedicatedThread
{
    private readonly Action _body;
    private readonly ThreadSchedulingRequest _request;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private ThreadSchedulingReport? _report;
    private ExceptionDispatchInfo? _failure;
    private bool _configured;

    private DedicatedThread(string name, ThreadSchedulingRequest request, Action body, int maxStackSizeBytes, bool background)
    {
        Name = name;
        _request = request;
        _body = body;
        _thread = new Thread(Entry, maxStackSizeBytes) { Name = name, IsBackground = background };
    }

    public string Name { get; }

    /// <summary>What the thread asked for and got. Set before <see cref="Start"/> returns.</summary>
    public ThreadSchedulingReport Report => _report ?? throw new InvalidOperationException("The thread has not configured itself yet.");

    /// <summary>The exception that escaped the body, if any (valid after <see cref="Join()"/>).</summary>
    public Exception? Failure => _failure?.SourceException;

    public bool IsAlive => _thread.IsAlive;

    /// <summary>
    /// Starts <paramref name="body"/> on a new OS thread named <paramref name="name"/> (Linux shows the first 15 bytes)
    /// with <paramref name="request"/> applied, and waits until the thread has configured itself.
    /// </summary>
    /// <param name="background">A background thread does not keep the process alive; join it explicitly.</param>
    public static DedicatedThread Start(
        string name,
        ThreadSchedulingRequest request,
        Action body,
        int maxStackSizeBytes = 0,
        bool background = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(maxStackSizeBytes);
        request.Validate();

        var thread = new DedicatedThread(name, request, body, maxStackSizeBytes, background);
        thread._thread.Start();
        lock (thread._gate)
        {
            while (!thread._configured)
            {
                Monitor.Wait(thread._gate);
            }
        }

        return thread;
    }

    public void Join() => _thread.Join();

    public bool Join(TimeSpan timeout) => _thread.Join(timeout);

    /// <summary>Rethrows the body's exception, with its original stack, on the calling thread.</summary>
    public void ThrowIfFailed() => _failure?.Throw();

    private void Entry()
    {
        try
        {
            _report = ThreadScheduling.ApplyToCurrentThread(_request, Name);
        }
        catch (Exception e)
        {
            // Applying is best effort by contract; an unexpected failure is reported, not fatal.
            _report = new ThreadSchedulingReport
            {
                Requested = _request,
                Name = Name,
                Policy = "unknown",
                RealTimeError = "could not apply the request: " + e.Message,
            };
        }
        finally
        {
            lock (_gate)
            {
                _configured = true;
                Monitor.PulseAll(_gate);
            }
        }

        try
        {
            _body();
        }
        catch (Exception e)
        {
            _failure = ExceptionDispatchInfo.Capture(e);
        }
    }
}
