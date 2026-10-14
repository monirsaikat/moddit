using System.Text;

namespace Modit;

/// <summary>
/// Identifies a file format from its first bytes (docs/plan/05, "Format sniffing"). Content wins over the
/// extension: a PNG named photo.jpg is a PNG. Returns canonical lowercase ids such as <c>png</c> or <c>pdf</c>.
/// </summary>
public static class FormatSniffer
{
    /// <summary>Bytes needed for every check except SVG, which looks further into the text.</summary>
    public const int HeaderLength = 64;

    private const int SvgProbeLength = 4096;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    public static string? Detect(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        byte[] buffer = new byte[SvgProbeLength];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Detect(buffer.AsSpan(0, read));
    }

    public static string? Detect(ReadOnlySpan<byte> h)
    {
        if (h.StartsWith(PngSignature))
        {
            return "png";
        }

        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
        {
            return "jpeg";
        }

        if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8))
        {
            return "gif";
        }

        if (h.Length >= 12 && h.StartsWith("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        if (h.StartsWith("II*\0"u8) || h.StartsWith("MM\0*"u8))
        {
            return "tiff";
        }

        if (h.StartsWith("%PDF-"u8))
        {
            return "pdf";
        }

        if (h.StartsWith("BM"u8) && h.Length >= 18)
        {
            return "bmp";
        }

        // ICONDIR: reserved 0, type 1 (icon), at least one image.
        if (h.Length >= 6 && h[0] == 0 && h[1] == 0 && h[2] == 1 && h[3] == 0 && (h[4] | h[5]) != 0)
        {
            return "ico";
        }

        if (h.Length >= 12 && h[4..8].SequenceEqual("ftyp"u8))
        {
            ReadOnlySpan<byte> brand = h[8..12];
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
            {
                return "avif";
            }

            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8) ||
                brand.SequenceEqual("msf1"u8) || brand.SequenceEqual("hevc"u8))
            {
                return "heic";
            }
        }

        return LooksLikeSvg(h) ? "svg" : null;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> h)
    {
        // Text formats: skip a UTF-8 BOM, then require markup and an <svg element near the start.
        if (h.StartsWith(Utf8Bom))
        {
            h = h[3..];
        }

        int start = 0;
        while (start < h.Length && h[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            start++;
        }

        if (start >= h.Length || h[start] != (byte)'<')
        {
            return false;
        }

        string text = Encoding.UTF8.GetString(h[start..]);
        return text.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }
}
