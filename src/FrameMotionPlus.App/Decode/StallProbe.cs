using System.Diagnostics;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;

namespace FrameMotionPlus.App.Decode;

/// <summary>What a <see cref="StallProbe"/> saw.</summary>
internal readonly record struct StallReport(long Calls, long MaxNs, long P99Ns, long P999Ns, double Seconds)
{
    public override string ToString() =>
        $"{Calls} launch+record pairs in {Seconds:0.0} s, worst {MaxNs / 1e6:0.000} ms, p99.9 {P999Ns / 1e6:0.000} ms, p99 {P99Ns / 1e6:0.000} ms";
}

/// <summary>
/// Another thread's ordinary CUDA work, measured: on a stream of its own it launches a small kernel and records an event,
/// back to back, timing each pair on the host. A call that synchronises the context elsewhere (a free, a decoder
/// create or destroy) holds such enqueues until all queued work finishes; the worst pair shows it. The first 2,000
/// pairs (first uses) are not counted; the stream is drained every 256 pairs, outside the timed calls, so its queue never
/// fills.
/// </summary>
internal sealed class StallProbe : IDisposable
{
    private const int Warmup = 2_000;
    private const long BucketNs = 10_000; // 10 µs buckets up to 1 s
    private const int Buckets = 100_000;

    private readonly CudaContext _context;
    private readonly ImageKernels _kernels;
    private readonly CudaStream _stream;
    private readonly CudaEvent _event;
    private readonly CudaDeviceBuffer _target;
    private readonly long[] _histogram = new long[Buckets];
    private readonly Thread _thread;
    private int _stop;
    private long _calls;
    private long _max;
    private long _started;
    private long _ended;

    private StallProbe(CudaContext context, ImageKernels kernels)
    {
        _context = context;
        _kernels = kernels;
        _stream = CudaStream.Create(context);
        _event = CudaEvent.Create(context);
        _target = CudaDeviceBuffer.AllocateImage(context, 64, 16, PixelFormat.Rgba8);
        _thread = new Thread(Run) { IsBackground = true, Name = "fmp-stall-probe" };
    }

    public static StallProbe Start(CudaContext context, ImageKernels kernels)
    {
        var probe = new StallProbe(context, kernels);
        probe._thread.Start();
        return probe;
    }

    /// <summary>Stops the thread and reports.</summary>
    public StallReport Stop()
    {
        Volatile.Write(ref _stop, 1);
        _thread.Join();
        long p99 = 0, p999 = 0, seen = 0;
        var total = Math.Max(1, _calls);
        for (var i = 0; i < Buckets; i++)
        {
            seen += _histogram[i];
            if (p99 == 0 && seen >= total * 0.99)
            {
                p99 = (i + 1) * BucketNs;
            }

            if (p999 == 0 && seen >= total * 0.999)
            {
                p999 = (i + 1) * BucketNs;
            }
        }

        return new StallReport(_calls, _max, p99, p999, Stopwatch.GetElapsedTime(_started, _ended).TotalSeconds);
    }

    public void Dispose()
    {
        if (_thread.IsAlive)
        {
            _ = Stop();
        }

        _stream.Synchronize();
        _target.Dispose();
        _event.Dispose();
        _stream.Dispose();
    }

    private void Run()
    {
        _context.MakeCurrent();
        var frame = _target.AsFrame();
        long n = 0;
        while (Volatile.Read(ref _stop) == 0)
        {
            var t0 = Stopwatch.GetTimestamp();
            _kernels.EnqueueTestPattern(_stream, frame, (uint)n);
            _event.Record(_stream);
            var ns = (long)Stopwatch.GetElapsedTime(t0).TotalNanoseconds;
            n++;
            if (n == Warmup)
            {
                _started = Stopwatch.GetTimestamp();
            }
            else if (n > Warmup)
            {
                _calls++;
                _max = Math.Max(_max, ns);
                _histogram[Math.Min(Buckets - 1, ns / BucketNs)]++;
            }

            if ((n & 255) == 0)
            {
                _stream.Synchronize();
            }
        }

        _ended = Stopwatch.GetTimestamp();
        if (_started == 0)
        {
            _started = _ended;
        }
    }
}
