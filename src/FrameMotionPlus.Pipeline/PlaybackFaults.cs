using System.Globalization;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// Fault injection for diagnostics and tests (<c>fmp play</c> reads it from <c>FMP_PLAY_FAULT</c>): GPU work on the ring
/// writes' stream that does not finish, to check that a session still stops, reports and lets the process exit.
/// <list type="bullet">
/// <item><c>gpu-wait@N</c>: the N-th generated frame first waits, GPU-side, for a release point nothing will ever reach
/// (its slot's release value plus a million): the stream stops there for good.</item>
/// <item><c>gpu-busy@N:S</c>: the N-th generated frame first keeps the GPU busy for S seconds (a chain of 250 ms delay
/// kernels): longer than any teardown bound, but it ends.</item>
/// </list>
/// </summary>
public static class PlaybackFaults
{
    /// <summary>The wrapper for <paramref name="spec"/>; null for none. Throws <see cref="FormatException"/> on a bad one.</summary>
    public static Func<GeneratorHandle, CudaStream, ImageKernels, GeneratorHandle>? Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var at = spec.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            throw new FormatException($"FMP_PLAY_FAULT '{spec}': expected gpu-wait@N or gpu-busy@N:S.");
        }

        var kind = spec[..at];
        var rest = spec[(at + 1)..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        var call = int.Parse(colon < 0 ? rest : rest[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture);
        switch (kind)
        {
            case "gpu-wait":
                return (inner, stream, kernels) => InjectedFault.Wrap(inner, stream, kernels, call, busyNs: 0);
            case "gpu-busy" when colon > 0:
                var seconds = double.Parse(rest[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture);
                return (inner, stream, kernels) => InjectedFault.Wrap(inner, stream, kernels, call, (long)(seconds * 1e9));
            default:
                throw new FormatException($"FMP_PLAY_FAULT '{spec}': expected gpu-wait@N or gpu-busy@N:S.");
        }
    }

    private sealed class InjectedFault : IFrameGenerator
    {
        private const long DelayChunkNs = 250_000_000;
        private readonly IFrameGenerator _inner;
        private readonly CudaStream _stream;
        private readonly ImageKernels _kernels;
        private readonly int _at;
        private readonly long _busyNs;
        private int _calls;

        private InjectedFault(IFrameGenerator inner, CudaStream stream, ImageKernels kernels, int at, long busyNs)
        {
            _inner = inner;
            _stream = stream;
            _kernels = kernels;
            _at = at;
            _busyNs = busyNs;
        }

        public FrameGenCapabilities Capabilities => _inner.Capabilities;

        public static GeneratorHandle Wrap(GeneratorHandle handle, CudaStream stream, ImageKernels kernels, int at, long busyNs) =>
            new(new InjectedFault(handle.Generator, stream, kernels, at, busyNs), handle.ReadGpuTime, handle.Name + (busyNs > 0 ? " (GPU kept busy)" : " (GPU wait that never completes)"));

        public void Configure(in FrameGenConfig config) => _inner.Configure(config);

        public void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done)
        {
            if (++_calls == _at)
            {
                if (_busyNs > 0)
                {
                    for (var left = _busyNs; left > 0; left -= DelayChunkNs)
                    {
                        _kernels.EnqueueDelay(_stream, Math.Min(left, DelayChunkNs));
                    }
                }
                else
                {
                    _stream.Wait(output.Ready with { Value = output.Ready.Value + 1_000_000 });
                }
            }

            _inner.Enqueue(prev, curr, t, output, done);
        }

        public void NotifyDiscontinuity(long contentTimeNs) => _inner.NotifyDiscontinuity(contentTimeNs);

        public void Dispose() => _inner.Dispose();
    }
}
