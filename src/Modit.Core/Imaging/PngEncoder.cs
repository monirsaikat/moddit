using System.Buffers.Binary;
using System.IO.Compression;

namespace Modit.Core.Imaging;

/// <summary>
/// Minimal PNG writer for BGRA page renders. It exists so the CLI and tests can save a page image
/// before the libvips imaging engine arrives in milestone 3; it is not a general-purpose encoder.
/// </summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void WriteBgra(Stream output, ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (stride < width * 4 || bgra.Length < (long)stride * (height - 1) + (width * 4))
        {
            throw new ArgumentException("Pixel buffer is too small for the given dimensions.", nameof(bgra));
        }

        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering (we emit filter type 0 on every row)
        header[12] = 0; // no interlace
        WriteChunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[1 + (width * 4)];
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> source = bgra.Slice(y * stride, width * 4);
                for (int x = 0; x < width; x++)
                {
                    int i = x * 4;
                    row[1 + i] = source[i + 2];
                    row[2 + i] = source[i + 1];
                    row[3 + i] = source[i];
                    row[4 + i] = source[i + 3];
                }

                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        WriteChunk(output, "IEND"u8, []);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        output.Write(type);
        output.Write(data);

        uint crc = UpdateCrc(UpdateCrc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        output.Write(word);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
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
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
