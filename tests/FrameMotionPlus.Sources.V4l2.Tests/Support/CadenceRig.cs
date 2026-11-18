using FrameMotionPlus.Core.Cadence;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;

namespace FrameMotionPlus.Sources.V4l2.Tests.Support;

/// <summary>
/// The CPU reference of the cadence kernel (native/kernels/cadence.cu), written from its description: 4x4-sample cells
/// summed exactly, 8x8-cell tiles, per tile floor(65536 · Σ|cur − ref| / (cells · 16)), the maximum over tiles, and the
/// largest cell sum. Columns and rows past the last whole cell are not sampled.
/// </summary>
internal static class CadenceReference
{
    public static (ulong TileQ, ulong MaxCell, uint[] Grid) Run(byte[] plane, int pitch, int width, int height, CadenceSampling sampling, uint[] reference)
    {
        var cellsX = width / 4;
        var cellsY = height / 4;
        var grid = new uint[cellsX * cellsY];
        ulong maxCell = 0;
        for (var cy = 0; cy < cellsY; cy++)
        {
            for (var cx = 0; cx < cellsX; cx++)
            {
                uint sum = 0;
                for (var dy = 0; dy < 4; dy++)
                {
                    for (var dx = 0; dx < 4; dx++)
                    {
                        var at = ((cy * 4) + dy) * pitch + (((cx * 4) + dx) * sampling.SampleBytes);
                        sum += sampling.Sixteen ? (uint)((plane[at] | (plane[at + 1] << 8)) >> sampling.Shift) : plane[at];
                    }
                }

                grid[(cy * cellsX) + cx] = sum;
                maxCell = Math.Max(maxCell, sum);
            }
        }

        ulong tileQ = 0;
        for (var ty = 0; ty < (cellsY + 7) / 8; ty++)
        {
            for (var tx = 0; tx < (cellsX + 7) / 8; tx++)
            {
                ulong diff = 0, cells = 0;
                for (var cy = ty * 8; cy < Math.Min(cellsY, (ty * 8) + 8); cy++)
                {
                    for (var cx = tx * 8; cx < Math.Min(cellsX, (tx * 8) + 8); cx++)
                    {
                        var i = (cy * cellsX) + cx;
                        diff += (ulong)Math.Abs((long)grid[i] - reference[i]);
                        cells++;
                    }
                }

                tileQ = Math.Max(tileQ, (diff << 16) / (cells * 16));
            }
        }

        return (tileQ, maxCell, grid);
    }
}

/// <summary>
/// Captures through the real cadence kernel and Core's tracker, as <see cref="V4l2FrameSource"/> runs them: each frame's
/// luma plane is uploaded, the kernel compares it with the last unique frame's grid, the 16-byte result comes back and
/// the tracker decides; a unique frame's grid becomes the reference.
/// </summary>
internal sealed class CadenceRig : IDisposable
{
    private readonly CudaContext _context;
    private readonly CudaStream _stream;
    private readonly CudaDeviceBuffer _plane;
    private readonly CudaDeviceBuffer[] _grids = new CudaDeviceBuffer[2];
    private readonly CudaDeviceBuffer _result;
    private readonly ulong[] _words = new ulong[2];
    private int _ref;

    public CadenceRig(CudaContext context, int width, int height, CadenceSampling sampling, CadenceOptions? options = null)
    {
        _context = context;
        Width = width;
        Height = height;
        Sampling = sampling;
        Pitch = width * sampling.SampleBytes;
        Grid = new CadenceGrid(width, height, sampling);
        _stream = CudaStream.Create(context);
        _plane = CudaDeviceBuffer.Allocate(context, (long)Pitch * height);
        for (var i = 0; i < 2; i++)
        {
            _grids[i] = CudaDeviceBuffer.Allocate(context, Grid.Bytes);
            _stream.MemsetAsync(_grids[i].Address, 0, Grid.Bytes);
        }

        _result = CudaDeviceBuffer.Allocate(context, 16);
        Tracker = new CadenceTracker(options ?? CaptureSourceOptions.CaptureCadence);
    }

    public int Width { get; }

    public int Height { get; }

    public int Pitch { get; }

    public CadenceSampling Sampling { get; }

    public CadenceGrid Grid { get; }

    public CadenceTracker Tracker { get; }

    /// <summary>The metric of <paramref name="plane"/> against the reference, without deciding anything.</summary>
    public (ulong TileQ, ulong MaxCell) Measure(byte[] plane)
    {
        _stream.CopyFromHost<byte>(plane, _plane.Address);
        CaptureHardware.Kernels.EnqueueCadenceMetric(_stream, _plane.Address, Pitch, Grid, _grids[_ref].Address, _grids[1 - _ref].Address, _result.Address);
        _stream.CopyToHost<ulong>(_result.Address, _words);
        return (_words[0], _words[1]);
    }

    /// <summary>One capture at <paramref name="timeNs"/>: measured, decided, and the reference advanced when new.</summary>
    public CadenceResult Capture(byte[] plane, long timeNs, bool pq)
    {
        var (q, _) = Measure(plane);
        var r = Tracker.Process(timeNs, Grid.Normalise(q), pq);
        if (r.IsUnique)
        {
            _ref = 1 - _ref;
        }

        return r;
    }

    public uint[] ReadGrid(bool current)
    {
        var grid = new uint[Grid.Cells];
        _stream.CopyToHost<uint>(_grids[current ? 1 - _ref : _ref].Address, grid);
        return grid;
    }

    public void SetReference(uint[] grid) => _stream.CopyFromHost<uint>(grid, _grids[_ref].Address);

    public void Dispose()
    {
        _stream.Synchronize();
        _plane.Dispose();
        foreach (var g in _grids)
        {
            g.Dispose();
        }

        _result.Dispose();
        _stream.Dispose();
        _ = _context;
    }
}

/// <summary>Synthetic luma: a textured scene that moves by whole pixels per picture, small patches (a HUD) and noise.</summary>
internal sealed class SyntheticLuma
{
    private readonly ushort[] _texture;
    private readonly int _textureWidth;

    public SyntheticLuma(int width, int height, int bits, int seed = 1)
    {
        Width = width;
        Height = height;
        Bits = bits;
        _textureWidth = width + 4096;
        _texture = new ushort[_textureWidth * height];
        var random = new Random(seed);
        var black = 16 << (bits - 8);
        var span = 200 << (bits - 8);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < _textureWidth; x++)
            {
                // A smooth gradient with texture: moving it changes most tiles.
                var smooth = (Math.Sin(x * 0.05) * 0.3) + (Math.Cos(y * 0.07) * 0.2) + 0.5;
                _texture[(y * _textureWidth) + x] = (ushort)(black + (smooth * span * 0.8) + random.Next(span / 5));
            }
        }
    }

    public int Width { get; }

    public int Height { get; }

    public int Bits { get; }

    /// <summary>
    /// The luma plane of picture <paramref name="picture"/> (the scene moved <paramref name="picture"/> × 2 pixels), with
    /// a HUD patch of value <paramref name="hud"/> (0: none) and uniform noise of ±<paramref name="noise"/> codes on a
    /// <paramref name="noiseDensity"/> share of the samples.
    /// </summary>
    public byte[] Plane(CadenceSampling sampling, int picture, int hud = 0, int noise = 0, int noiseSeed = 0, double noiseDensity = 1)
    {
        var pitch = Width * sampling.SampleBytes;
        var plane = new byte[pitch * Height];
        var random = new Random(noiseSeed);
        var max = (1 << Bits) - 1;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                int v = _texture[(y * _textureWidth) + x + (picture * 2)];
                if (hud > 0 && x >= 40 && x < 48 && y >= 40 && y < 48)
                {
                    v = hud;
                }

                if (noise > 0 && (noiseDensity >= 1 || random.NextDouble() < noiseDensity))
                {
                    v += random.Next(-noise, noise + 1);
                }

                v = Math.Clamp(v, 0, max);
                var at = (y * pitch) + (x * sampling.SampleBytes);
                if (sampling.Sixteen)
                {
                    var word = (ushort)(v << sampling.Shift);
                    plane[at] = (byte)word;
                    plane[at + 1] = (byte)(word >> 8);
                }
                else
                {
                    plane[at] = (byte)v;
                }
            }
        }

        return plane;
    }

    public static long Tick(FrameRate signal, int capture) => 1_000_000_000L + signal.TickTimeNs(capture);
}
