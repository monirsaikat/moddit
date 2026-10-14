using System.Buffers.Binary;
using NetVips;

namespace Modit.Engine.Host.Engines;

/// <summary>Writes 24-bit uncompressed BMP, the form every program can read.</summary>
internal static class BmpWriter
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int PixelsPerMeterAt96Dpi = 3780;

    /// <param name="image">An 8-bit, 3-band sRGB image.</param>
    public static void Write(string path, Image image)
    {
        if (image.Bands != 3 || image.Format != Enums.BandFormat.Uchar)
        {
            throw new ModitException(ErrorCode.ConvertFailed, "BMP output needs an 8-bit RGB image.");
        }

        int width = image.Width;
        int height = image.Height;
        int rowBytes = width * 3;
        int stride = (rowBytes + 3) & ~3;
        long imageSize = (long)stride * height;
        if (FileHeaderSize + InfoHeaderSize + imageSize > uint.MaxValue)
        {
            throw new ModitException(ErrorCode.ConvertFailed, "The image is too large for the BMP format.");
        }

        byte[] rgb = image.WriteToMemory<byte>();
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

        Span<byte> header = stackalloc byte[FileHeaderSize + InfoHeaderSize];
        header.Clear();
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..], (uint)(FileHeaderSize + InfoHeaderSize + imageSize));
        BinaryPrimitives.WriteUInt32LittleEndian(header[10..], FileHeaderSize + InfoHeaderSize);
        Span<byte> info = header[FileHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(info, InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(info[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(info[8..], height); // positive: rows stored bottom-up
        BinaryPrimitives.WriteUInt16LittleEndian(info[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(info[14..], 24);
        BinaryPrimitives.WriteUInt32LittleEndian(info[20..], (uint)imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(info[24..], PixelsPerMeterAt96Dpi);
        BinaryPrimitives.WriteInt32LittleEndian(info[28..], PixelsPerMeterAt96Dpi);
        stream.Write(header);

        byte[] row = new byte[stride];
        for (int y = height - 1; y >= 0; y--)
        {
            int source = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int s = source + (x * 3);
                int d = x * 3;
                row[d] = rgb[s + 2];
                row[d + 1] = rgb[s + 1];
                row[d + 2] = rgb[s];
            }

            stream.Write(row);
        }
    }
}

/// <summary>Writes a Windows icon whose entries are PNG images (supported since Windows Vista).</summary>
internal static class IcoWriter
{
    public sealed record Entry(int Size, byte[] Png);

    public static void Write(string path, IReadOnlyList<Entry> entries)
    {
        const int DirectorySize = 6;
        const int EntrySize = 16;

        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> directory = stackalloc byte[DirectorySize];
        directory.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(directory[2..], 1); // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(directory[4..], (ushort)entries.Count);
        stream.Write(directory);

        uint offset = (uint)(DirectorySize + (EntrySize * entries.Count));
        Span<byte> record = stackalloc byte[EntrySize];
        foreach (Entry entry in entries)
        {
            record.Clear();
            record[0] = (byte)(entry.Size >= 256 ? 0 : entry.Size); // 0 means 256
            record[1] = (byte)(entry.Size >= 256 ? 0 : entry.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], 1);  // color planes
            BinaryPrimitives.WriteUInt16LittleEndian(record[6..], 32); // bits per pixel
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)entry.Png.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], offset);
            stream.Write(record);
            offset += (uint)entry.Png.Length;
        }

        foreach (Entry entry in entries)
        {
            stream.Write(entry.Png);
        }
    }
}
