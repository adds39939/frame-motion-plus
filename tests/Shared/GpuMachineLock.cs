using System.Diagnostics;

[assembly: Xunit.AssemblyFixture(typeof(FrameMotionPlus.TestSupport.GpuMachineLock))]

namespace FrameMotionPlus.TestSupport;

/// <summary>
/// Lets only one test assembly that measures real time, or loads the machine, run at a time on this machine.
/// </summary>
/// <remarks>
/// <c>dotnet test</c> on the solution starts every test project at once. The hardware suites measure display cadence,
/// GPU span times and back-pressure, and two of them sharing the GPU and the compositor disturb each other: a windowed
/// cadence test was shown 168 frames/s on a 240 Hz display while another suite ran. The audio suite's loopback tests run
/// a delay line on a thread that is not real-time against PipeWire's clock, and Core's and Platform's suites keep every
/// core busy (scheduler sweeps, stress tests) or measure timer wake-ups: beside them the real-clock windows of the
/// Pipeline, Vulkan and Audio tests (a latch measured to ±0.4 ms, clicks through a delay line, zero missed slots) failed
/// in whole-solution runs and passed alone. So every test assembly of the solution holds an exclusive lock on one file
/// for its whole run, and they queue behind each other (the GPU suites dominate the wall time either way). The
/// operating system drops the lock when the test host exits, however it exits, so a leftover file means nothing. Without
/// a GPU the hardware tests skip and the lock is held for as long as the remaining tests take.
/// </remarks>
public sealed class GpuMachineLock : IDisposable
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SayAfter = TimeSpan.FromSeconds(5);

    private readonly FileStream? _held;

    public GpuMachineLock()
    {
        var path = Path.Combine(Path.GetTempPath(), "frame-motion-plus-gpu-tests.lock");
        var started = Stopwatch.GetTimestamp();
        var said = false;
        while (true)
        {
            try
            {
                _held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or PathTooLongException or NotSupportedException)
            {
                // Not a held lock: someone else's file, or a temp directory that cannot hold one. Run without it.
                Console.Error.WriteLine($"GPU test lock unavailable ({e.GetType().Name}: {path}); running without it, so hardware suites may overlap.");
                return;
            }
            catch (IOException)
            {
                // Held by another test assembly (a sharing violation), or the file cannot be created.
                var waited = Stopwatch.GetElapsedTime(started);
                if (waited > MaxWait)
                {
                    Console.Error.WriteLine($"GPU test lock not obtained after {waited.TotalMinutes:F0} min ({path}); running without it, so hardware suites may overlap.");
                    return;
                }

                if (!said && waited > SayAfter)
                {
                    said = true;
                    Console.Error.WriteLine($"Waiting for another test assembly to finish ({path}).");
                }

                Thread.Sleep(100);
            }
        }
    }

    public void Dispose() => _held?.Dispose();
}
