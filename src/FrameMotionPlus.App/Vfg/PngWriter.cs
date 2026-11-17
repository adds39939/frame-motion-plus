using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FrameMotionPlus.App.Vfg;

/// <summary>How a PNG's code values are to be read.</summary>
internal enum PngColor
{
    /// <summary>sRGB (an sRGB chunk).</summary>
    Srgb = 0,

    /// <summary>BT.2020 primaries, SMPTE ST 2084 (PQ), full range: a cICP chunk (PNG third edition), so HDR-aware viewers show it as HDR10.</summary>
    Pq = 1,

    /// <summary>No colour information (an error map).</summary>
    None = 2,
}

/// <summary>A minimal PNG encoder: 16-bit RGB or 8-bit grey, the "Up" filter on every row, zlib from the BCL.</summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes 16-bit RGB: <paramref name="rgb"/> holds width × height × 3 samples, row by row.</summary>
    public static void WriteRgb16(string path, int width, int height, ReadOnlySpan<ushort> rgb, PngColor color)
    {
        var rowBytes = width * 6;
        var raw = new byte[height * (rowBytes + 1)];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width * 3; x++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(current.AsSpan(x * 2), rgb[(y * width * 3) + x]);
            }

            Filter(raw, y, current, previous);
            (previous, current) = (current, previous);
        }

        Write(path, width, height, bitDepth: 16, colorType: 2, raw, color);
    }

    /// <summary>Writes 8-bit grey: <paramref name="grey"/> holds width × height samples.</summary>
    public static void WriteGrey8(string path, int width, int height, ReadOnlySpan<byte> grey)
    {
        var raw = new byte[height * (width + 1)];
        var previous = new byte[width];
        var current = new byte[width];
        for (var y = 0; y < height; y++)
        {
            grey.Slice(y * width, width).CopyTo(current);
            Filter(raw, y, current, previous);
            (previous, current) = (current, previous);
        }

        Write(path, width, height, bitDepth: 8, colorType: 0, raw, PngColor.None);
    }

    private static void Filter(byte[] raw, int y, byte[] current, byte[] previous)
    {
        var o = y * (current.Length + 1);
        raw[o] = 2; // Up
        for (var i = 0; i < current.Length; i++)
        {
            raw[o + 1 + i] = (byte)(current[i] - previous[i]);
        }
    }

    private static void Write(string path, int width, int height, byte bitDepth, byte colorType, byte[] raw, PngColor color)
    {
        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = bitDepth;
        header[9] = colorType;
        Chunk(file, "IHDR", header);
        switch (color)
        {
            case PngColor.Pq:
                Chunk(file, "cICP", [9, 16, 0, 1]); // BT.2020, ST 2084, RGB (identity matrix), full range
                break;
            case PngColor.Srgb:
                Chunk(file, "sRGB", [0]);
                break;
        }

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            Chunk(file, "IDAT", compressed.ToArray());
        }

        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
