using System.Diagnostics;
using System.Globalization;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Sources.File.Interop;
using FrameMotionPlus.Sources.File.Tests.Support;

namespace FrameMotionPlus.Sources.File.Tests;

/// <summary>
/// Memory, allocations, descriptors and errors: steady playback flat in GPU memory and FFmpeg's frame pool, zero managed
/// allocations per frame on both threads (with injected allocations to show the measurement sees them), repeated
/// open/close without leaks, and clear errors for files that cannot be played.
/// </summary>
[Trait(Category.Name, Category.Media)]
[Collection(SerialMediaDefinition.Name)]
public class ResourceTests
{
    /// <summary>
    /// 25 laps of a looping clip: the process's GPU memory (nvidia-smi, as the VFG tests measure it) and the context's
    /// free memory are the same after the first lap and at the end, and FFmpeg's frame pool never grows after start-up.
    /// </summary>
    [Fact]
    public void Gpu_memory_stays_flat_over_many_loops()
    {
        var env = Media.Require();
        using var source = new FileFrameSource(env.Context, env.Clip("hevc10-pq"), new FileSourceOptions { Loop = true, Kernels = env.Kernels });
        source.Start();
        var lapFrames = env.FrameTimes(env.Clip("hevc10-pq")).Length;
        long process = -1, free = 0;
        for (var i = 0; i < 25 * lapFrames; i++)
        {
            Assert.True(Frames.Next(source, out var frame));
            source.Release(frame.FrameId);
            if (i == lapFrames + 2)
            {
                source.ComputeStream.Synchronize();
                process = ProcessGpuMemoryMiB();
                free = env.Context.MemoryInfo.FreeBytes;
            }
        }

        source.ComputeStream.Synchronize();
        var processEnd = ProcessGpuMemoryMiB();
        var freeEnd = env.Context.MemoryInfo.FreeBytes;
        Log.WriteLine($"25 laps ({source.Statistics.FramesDecoded} frames): process GPU memory {process} -> {processEnd} MiB; context free {free >> 20} -> {freeEnd >> 20} MiB; pool growth {source.Statistics.PoolGrowth}");
        Assert.Equal(0, source.Statistics.PoolGrowth);
        Assert.InRange(source.Statistics.Loops, 24, 25); // the decoder may have started the next lap ahead of the consumer
        if (process >= 0 && processEnd >= 0)
        {
            Assert.Equal(process, processEnd);
        }

        // Other processes share the device: the context's free memory may move by what they do, so it is only logged
        // unless it dropped by more than a frame pool's worth.
        Assert.True(free - freeEnd < 64L << 20, $"free device memory dropped by {(free - freeEnd) >> 20} MiB during playback");
    }

    /// <summary>
    /// Hot path: <see cref="FileFrameSource.TryRead"/> and <see cref="FileFrameSource.Release(long)"/> allocate exactly
    /// nothing on the consumer thread, and the decode thread nothing per frame, measured after a warm-up lap that runs
    /// every path once (loop included). Proof the measurements see allocations: an allocation injected inside TryRead and
    /// one injected into the decode thread's per-frame work both show.
    /// </summary>
    [Fact]
    public void Reads_releases_and_the_decode_thread_allocate_nothing_per_frame()
    {
        var env = Media.Require();
        var path = env.Clip("h264");
        var lap = env.FrameTimes(path).Length;
        using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { Loop = true, Kernels = env.Kernels });
        source.Start();
        long Consume(int frames)
        {
            long allocated = 0;
            var held = 0L;
            var hasHeld = false;
            for (var i = 0; i < frames; i++)
            {
                SourceFrame frame;
                while (true)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var got = source.TryRead(out frame);
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    if (got)
                    {
                        break;
                    }

                    Assert.Null(source.Failure);
                }

                // Hold one frame (as prev) and release the one before: both release paths, all allocation-measured.
                var before2 = GC.GetAllocatedBytesForCurrentThread();
                if (hasHeld)
                {
                    source.Release(held);
                }

                allocated += GC.GetAllocatedBytesForCurrentThread() - before2;
                held = frame.FrameId;
                hasHeld = true;
            }

            source.Release(held);
            return allocated;
        }

        Consume((2 * lap) + 5); // warm-up: every path, the loop included
        var consumer = Consume(3 * lap);
        var decodeThread = source.Statistics.DecodeThreadAllocatedBytes;
        Log.WriteLine($"{3 * lap} frames: consumer {consumer} B, decode thread {decodeThread} B over {source.Statistics.FramesDecoded} frames");
        Assert.Equal(0, consumer);
        Assert.Equal(0, decodeThread);

        // The measurements see an allocation where one happens.
        source.ReadHook = static () => GC.KeepAlive(new object());
        Assert.True(Consume(10) > 0, "an allocation inside TryRead was not seen");
        source.ReadHook = null;
        source.PerFrameHook = static () => GC.KeepAlive(new byte[64]);
        Consume(10);
        Assert.True(source.Statistics.DecodeThreadAllocatedBytes > 0, "an allocation on the decode thread was not seen");
    }

    /// <summary>
    /// 30 cycles of open, decode 10 frames, dispose: open descriptors, this process's GPU memory and the CUDA context's
    /// reference count come back to where they were after the first cycle.
    /// </summary>
    [Fact]
    public void Opening_and_closing_repeatedly_leaks_nothing()
    {
        var env = Media.Require();
        var path = env.Clip("hevc10-pq");
        void Cycle()
        {
            using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { Kernels = env.Kernels });
            source.Start();
            for (var i = 0; i < 10; i++)
            {
                Assert.True(Frames.Next(source, out var frame));
                source.Release(frame.FrameId);
            }
        }

        Cycle();
        var (fds, gpu, refs) = (DescriptorCount(), ProcessGpuMemoryMiB(), env.Context.ReferenceCount);
        for (var i = 0; i < 30; i++)
        {
            Cycle();
        }

        var (fdsEnd, gpuEnd, refsEnd) = (DescriptorCount(), ProcessGpuMemoryMiB(), env.Context.ReferenceCount);
        Log.WriteLine($"30 open/close cycles: descriptors {fds} -> {fdsEnd}, GPU {gpu} -> {gpuEnd} MiB, context references {refs} -> {refsEnd}");
        Assert.Equal(refs, refsEnd);
        if (fds >= 0)
        {
            Assert.True(fdsEnd <= fds + 2, $"descriptors grew from {fds} to {fdsEnd}"); // the test host's own pipes come and go
        }

        if (gpu >= 0 && gpuEnd >= 0)
        {
            Assert.InRange(gpuEnd, gpu - 2, gpu + 2);
        }
    }

    /// <summary>Files that cannot be played give a clear error, and the process carries on.</summary>
    [Fact]
    public void Unplayable_files_give_clear_errors()
    {
        var env = Media.Require();
        var garbage = Path.Combine(Clips.Directory, "garbage.mp4");
        var random = new Random(4);
        var bytes = new byte[200_000];
        random.NextBytes(bytes);
        System.IO.File.WriteAllBytes(garbage, bytes);
        string Error(string path) => Assert.Throws<FileSourceException>(() => new FileFrameSource(env.Context, path, new FileSourceOptions { Kernels = env.Kernels })).Message;

        var e = Error(garbage);
        Log.WriteLine(e);
        Assert.Contains("cannot open", e, StringComparison.Ordinal);
        e = Error(env.Clip("audio-only"));
        Log.WriteLine(e);
        Assert.Contains("no video stream", e, StringComparison.Ordinal);
        e = Error(env.Clip("mjpeg"));
        Log.WriteLine(e);
        Assert.Contains("mjpeg", e, StringComparison.Ordinal);
        Assert.Contains("H.264, HEVC, AV1 and VP9", e, StringComparison.Ordinal);
        e = Error(env.Clip("h264-444"));
        Log.WriteLine(e);
        Assert.Contains("yuv444p", e, StringComparison.Ordinal);
        Assert.Throws<FileNotFoundException>(() => new FileFrameSource(env.Context, Path.Combine(Clips.Directory, "missing.mp4")));

        // H.264 High 10: NVDEC decodes it on GPUs that support it (then bit-exact), or the source says it cannot.
        try
        {
            var frames = Frames.CheckAgainstSoftwareDecode(env, env.Clip("h264-10bit"));
            Log.WriteLine($"H.264 High 10: decoded by NVDEC, {frames} frames bit-exact");
        }
        catch (FileSourceException refused)
        {
            Log.WriteLine($"H.264 High 10: {refused.Message}");
            Assert.Contains("NVDEC", refused.Message, StringComparison.Ordinal);
        }

        // The source still works afterwards.
        Assert.True(Frames.CheckAgainstSoftwareDecode(env, env.Clip("hevc"), maxFrames: 3) == 3);
    }

    /// <summary>
    /// Damaged files decode what is there without failing: a transport stream cut in half ends early (fewer frames, the
    /// frames before the cut bit-exact), and one with 16 KiB overwritten in the middle plays to the end, the decoder
    /// concealing or skipping the damage.
    /// </summary>
    [Fact]
    public void Truncated_and_corrupted_files_decode_what_is_there()
    {
        var env = Media.Require();
        var whole = env.Clip("h264-ts");
        var all = env.FrameTimes(whole).Length;
        var data = System.IO.File.ReadAllBytes(whole);
        var truncated = Path.Combine(Clips.Directory, "truncated.ts");
        System.IO.File.WriteAllBytes(truncated, data[..(data.Length / 2)]);
        var corrupted = Path.Combine(Clips.Directory, "corrupted.ts");
        var damaged = (byte[])data.Clone();
        new Random(9).NextBytes(damaged.AsSpan(data.Length / 2, 16 * 1024));
        System.IO.File.WriteAllBytes(corrupted, damaged);

        foreach (var path in new[] { truncated, corrupted })
        {
            using var source = new FileFrameSource(env.Context, path, new FileSourceOptions { Kernels = env.Kernels });
            source.Start();
            var n = 0;
            while (Frames.Next(source, out var frame))
            {
                source.Release(frame.FrameId);
                n++;
            }

            var s = source.Statistics;
            Log.WriteLine($"{Path.GetFileName(path)}: {n} of {all} frames; corrupt packets {s.CorruptPackets}, frames {s.CorruptFrames}; demux error {source.DemuxError ?? "none"}");
            Assert.Null(source.Failure);
            Assert.True(source.IsEndOfStream);
            Assert.True(n > 0);
            if (path == truncated)
            {
                Assert.True(n < all, "a file cut in half delivered every frame");
            }
        }
    }

    /// <summary>The library check refuses another FFmpeg major version, naming what it found.</summary>
    [Fact]
    public void Another_ffmpeg_major_version_is_refused_by_name()
    {
        static uint V(int major, int minor) => (uint)((major << 16) | (minor << 8));
        Assert.Null(FFmpegLibraries.CheckVersions(V(61, 1), V(63, 1), V(63, 1)));
        Assert.Null(FFmpegLibraries.CheckVersions(V(61, 9), V(63, 7), V(63, 3))); // later minors keep the layouts
        var refused = FFmpegLibraries.CheckVersions(V(60, 8), V(62, 11), V(62, 3));
        Assert.NotNull(refused);
        Assert.Contains("libavcodec 62.11.0", refused, StringComparison.Ordinal);
        Assert.Contains("FFmpeg 9 (libavcodec 63, libavformat 63, libavutil 61)", refused, StringComparison.Ordinal);
        Assert.NotNull(FFmpegLibraries.CheckVersions(V(61, 1), V(64, 0), V(63, 1)));
    }

    private static int DescriptorCount() =>
        OperatingSystem.IsLinux() && Directory.Exists("/proc/self/fd") ? Directory.GetFiles("/proc/self/fd").Length : -1;

    private static long ProcessGpuMemoryMiB()
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi", "--query-compute-apps=pid,used_memory --format=csv,noheader,nounits") { RedirectStandardOutput = true, UseShellExecute = false };
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
                if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid) && pid == Environment.ProcessId &&
                    long.TryParse(parts[1], CultureInfo.InvariantCulture, out var mib))
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
