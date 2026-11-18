using System.Runtime.InteropServices;

namespace FrameMotionPlus.Cuda.Kernels;

/// <summary>How the cadence kernel reads luma samples from a frame's first plane.</summary>
public readonly record struct CadenceSampling(int SampleBytes, bool Sixteen, int Shift)
{
    /// <summary>NV12: 8-bit samples, one byte apart.</summary>
    public static CadenceSampling Nv12 => new(1, false, 0);

    /// <summary>P010: 16-bit samples with the 10 code bits on top.</summary>
    public static CadenceSampling P010 => new(2, true, 6);

    /// <summary>YUYV: the Y bytes of the packed pairs, two bytes apart.</summary>
    public static CadenceSampling Yuyv => new(2, false, 0);

    /// <summary>The largest code value of a sample (255 or 1023).</summary>
    public int MaxCode => Sixteen ? (1 << (16 - Shift)) - 1 : 255;
}

/// <summary>
/// The cadence metric's downsampled luma grid for a frame size: cells of <see cref="CellSize"/>x<see cref="CellSize"/>
/// samples (the plain sum of their code values), tiles of <see cref="TileCells"/>x<see cref="TileCells"/> cells (32x32
/// pixels). Columns and rows past the last whole cell are not sampled.
/// </summary>
public readonly record struct CadenceGrid
{
    public const int CellSize = 4;
    public const int TileCells = 8;

    public CadenceGrid(int width, int height, CadenceSampling sampling)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, CellSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, CellSize);
        CellsX = width / CellSize;
        CellsY = height / CellSize;
        Sampling = sampling;
    }

    public int CellsX { get; }

    public int CellsY { get; }

    public CadenceSampling Sampling { get; }

    /// <summary>Cells in the grid (each a 32-bit sum).</summary>
    public long Cells => (long)CellsX * CellsY;

    /// <summary>Bytes of one grid (a device buffer this size holds the current or the reference grid).</summary>
    public long Bytes => Cells * sizeof(uint);

    public int TilesX => (CellsX + TileCells - 1) / TileCells;

    public int TilesY => (CellsY + TileCells - 1) / TileCells;

    /// <summary>The kernel's tile value (1/65536 code units per sample) as a fraction of full scale: the tracker's metric.</summary>
    public double Normalise(ulong tileQ) => tileQ / 65536.0 / Sampling.MaxCode;

    /// <summary>The largest cell sum as a mean code value per sample.</summary>
    public static double CellMean(ulong cellSum) => cellSum / (double)(CellSize * CellSize);
}

/// <summary>The parameter block of <c>fmp_cadence_metric</c> (native/kernels/cadence.cu, <c>struct fmp_cadence_params</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CadenceKernelParameters
{
    public long CellsX { get; init; }

    public long CellsY { get; init; }

    public long SampleBytes { get; init; }

    public long Sixteen { get; init; }

    public long Shift { get; init; }
}
