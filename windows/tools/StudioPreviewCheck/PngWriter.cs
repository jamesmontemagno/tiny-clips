using System.Buffers.Binary;
using System.IO.Compression;

namespace TinyClips.Tools.StudioPreviewCheck;

/// <summary>A minimal PNG encoder (8-bit RGB, no filtering), so the tool can leave screenshots behind as evidence.</summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes tightly packed top-down BGRA pixels as an opaque RGB PNG.</summary>
    public static void WriteBgra(string path, ReadOnlySpan<byte> bgra, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stride = (width * 3) + 1;
        var raw = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var source = bgra.Slice(y * width * 4, width * 4);
            var target = raw.AsSpan(y * stride, stride);
            target[0] = 0;
            for (var x = 0; x < width; x++)
            {
                target[1 + (x * 3)] = source[(x * 4) + 2];
                target[2 + (x * 3)] = source[(x * 4) + 1];
                target[3 + (x * 3)] = source[x * 4];
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var file = File.Create(path);
        file.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 2;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(file, "IHDR"u8, header);
        WriteChunk(file, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(file, "IEND"u8, []);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
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
