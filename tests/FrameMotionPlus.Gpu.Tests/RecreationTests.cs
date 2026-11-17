using System.Diagnostics;
using System.Globalization;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Gpu.Tests.Support;
using Reference = FrameMotionPlus.Cuda.Tests.Support.Reference;

namespace FrameMotionPlus.Gpu.Tests;

/// <summary>
/// Format and size changes mid-run: the bridge quiesces the old ring with writes still in flight, destroys the imports,
/// the presenter replaces the ring and the new one is imported. Repeated until a leak of descriptors, CUDA objects or
/// GPU memory would be unmistakable; Vulkan objects are checked by the validation layer's leak report at teardown.
/// </summary>
public class RecreationTests
{
    private static readonly (int Width, int Height, PixelFormat Format)[] Shapes =
    [
        (640, 360, PixelFormat.Rgba8),
        (1280, 720, PixelFormat.Rgb10A2),
        (333, 97, PixelFormat.Rgba8),
        (1920, 1080, PixelFormat.Rgb10A2),
        (1277, 719, PixelFormat.Rgb10A2),
        (800, 600, PixelFormat.Rgba8),
    ];

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Recreating_the_ring_mid_run_leaks_no_descriptor_cuda_object_or_gpu_memory() => Interop.Run(Shapes[0].Width, Shapes[0].Height, Shapes[0].Format, rig =>
    {
        const int Recreations = 48;
        var warmup = Shapes.Length;
        var expectedObjects = (2 * rig.Ring.SlotCount) + 2;
        var fdsAfterWarmup = 0;
        long memoryAfterWarmup = 0;
        var generation = rig.Bridge.RingGeneration;
        for (var r = 0; r <= Recreations; r++)
        {
            var (w, h, format) = Shapes[r % Shapes.Length];
            if (r > 0)
            {
                // Mid-run: two writes still running on the GPU (one behind a delay), neither presented.
                rig.WritePattern(1);
                var slot = rig.Bridge.AcquireSlot();
                rig.Kernels.EnqueueDelay(rig.Stream, 20_000_000);
                rig.Stream.Wait(slot.ProducerWait);
                rig.Kernels.EnqueueTestPattern(rig.Stream, slot.ProducerView, 2);
                rig.Stream.Signal(slot.ProducerSignal);
                var oldRing = rig.Ring;

                rig.Reconfigure(w, h, format);

                Assert.NotSame(oldRing, rig.Ring);
                Assert.Equal(++generation, rig.Bridge.RingGeneration);
                Assert.Equal(rig.Presenter.RingGeneration, rig.Bridge.RingGeneration);
                Assert.True(rig.Stream.IsIdle, "producer work against the old ring was still queued after Configure");
            }

            Assert.Equal(expectedObjects, rig.Bridge.LiveImportObjects);
            Assert.Equal(0UL, rig.Bridge.LastAcquiredWrite);

            // A lap and a half of frames on the new ring; the last one compared whole.
            var frames = (rig.Ring.SlotCount * 3 / 2) + 1;
            for (var i = 1; i <= frames; i++)
            {
                var slot = rig.WritePattern((uint)((r * 100) + i));
                Assert.Equal((ulong)i, slot.ProducerSignal.Value);
                rig.Present(slot);
            }

            Interop.AssertPixels(Reference.Pattern(w, h, (uint)((r * 100) + frames), format), rig.ReadPixels(), w, $"recreation {r}: {w}x{h} {format}");

            if (r == warmup)
            {
                memoryAfterWarmup = ProcessGpuMemoryMiB();
                fdsAfterWarmup = OpenDescriptors();
            }
        }

        var fds = OpenDescriptors();
        var memory = ProcessGpuMemoryMiB();
        Interop.WriteLine($"device/anon descriptors after warm-up {fdsAfterWarmup}, at the end {fds}; process GPU memory {memoryAfterWarmup} MiB -> {memory} MiB; " +
            $"all descriptors {Directory.GetFiles("/proc/self/fd").Length}");
        Assert.Equal(fdsAfterWarmup, fds);
        if (memory >= 0 && memoryAfterWarmup >= 0)
        {
            // One leaked ring here is 32 MiB or more; 42 recreations after the warm-up would leak gigabytes.
            Assert.InRange(memory - memoryAfterWarmup, -256, 24);
        }
    }, new RigOptions { Presenter = new Vulkan.VulkanPresenterOptions { SlotCount = 5 } });

    [Fact]
    [Trait(Category.Name, Category.Gpu)]
    public void Reconfiguring_with_writes_laps_ahead_of_any_release_drains_without_deadlock() => Interop.Run(320, 180, PixelFormat.Rgba8, rig =>
    {
        // Three laps written, none presented: everything past the first lap is blocked GPU-side on 'released'. The
        // bridge must release all of it (a single release of the last write would wait for a write that itself waits
        // for an earlier release) before it may destroy the imports.
        var n = rig.Ring.SlotCount;
        for (var i = 1; i <= 3 * n; i++)
        {
            if (i == n + 1)
            {
                Assert.True(rig.Ring.WaitWritten((ulong)n, 2_000_000_000)); // the first lap finishes; the rest waits
            }

            rig.WritePattern((uint)i);
        }

        Thread.Sleep(50);
        Assert.Equal((ulong)n, rig.Ring.ReadWrittenValue());
        var start = Stopwatch.GetTimestamp();
        rig.Reconfigure(640, 360, PixelFormat.Rgb10A2);
        Interop.WriteLine($"reconfigure took {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1} ms");
        Assert.True(rig.Stream.IsIdle);
        rig.Present(rig.WritePattern(42));
        Interop.AssertPixels(Reference.Pattern(640, 360, 42, PixelFormat.Rgb10A2), rig.ReadPixels(), 640, "first frame of the new ring");
    });

    /// <summary>
    /// Open descriptors of the kinds an interop leak would leave: devices (an exported or imported GPU object is an
    /// <c>/dev/nvidiactl</c> descriptor) and anonymous inodes (eventfds, sync files). Files and pipes are left out:
    /// the runtime opens assemblies and libraries lazily, and running nvidia-smi creates pipes, neither of which is
    /// ours to count.
    /// </summary>
    internal static int OpenDescriptors() => Directory.GetFiles("/proc/self/fd")
        .Select(static f => new FileInfo(f).LinkTarget ?? string.Empty)
        .Count(static t => t.StartsWith("/dev/", StringComparison.Ordinal)
            || (t.StartsWith("anon_inode:", StringComparison.Ordinal) && t != "anon_inode:[eventpoll]")); // the runtime's I/O engine

    /// <summary>This process's GPU memory as nvidia-smi reports it (MiB), or -1 when it cannot tell.</summary>
    internal static long ProcessGpuMemoryMiB()
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi", "--query-compute-apps=pid,used_memory --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(info);
            if (process is null)
            {
                return -1;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid) && pid == Environment.ProcessId
                    && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var mib))
                {
                    return mib;
                }
            }

            return -1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}
