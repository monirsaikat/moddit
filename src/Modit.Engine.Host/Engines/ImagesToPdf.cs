using System.Globalization;
using System.IO.Compression;
using System.Text;
using NetVips;

namespace Modit.Engine.Host.Engines;

/// <summary>
/// Builds a PDF with one image per page. Baseline and progressive JPEGs in RGB or gray are embedded byte for
/// byte (DCTDecode), so photos lose nothing; everything else is decoded and stored losslessly (FlateDecode).
/// </summary>
internal static class ImagesToPdf
{
    private const double A4WidthPt = 595.28;
    private const double A4HeightPt = 841.89;
    private const double LetterWidthPt = 612;
    private const double LetterHeightPt = 792;

    public static ImagesToPdfResult Write(ImagesToPdfRequest request, CancellationToken cancellationToken)
    {
        if (request.InputPaths.Count == 0)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Choose at least one image.");
        }

        string pageSize = request.PageSize.ToLowerInvariant();
        if (pageSize is not ("image" or "a4" or "letter"))
        {
            throw new ModitException(ErrorCode.InvalidArgument, $"Unknown page size '{request.PageSize}'. Use image, a4 or letter.");
        }

        double margin = Math.Max(0, request.MarginPt);
        int passthrough = 0;
        try
        {
            using var writer = new PdfImageWriter(request.OutputPath);
            foreach (string path in request.InputPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PdfImage image = Prepare(path, request.VectorDpi);
                if (image.Filter == "DCTDecode")
                {
                    passthrough++;
                }

                double imageWidthPt = image.Width * 72.0 / image.Dpi;
                double imageHeightPt = image.Height * 72.0 / image.Dpi;
                (double pageWidth, double pageHeight) = pageSize switch
                {
                    "a4" => Orient(A4WidthPt, A4HeightPt, imageWidthPt > imageHeightPt),
                    "letter" => Orient(LetterWidthPt, LetterHeightPt, imageWidthPt > imageHeightPt),
                    _ => (imageWidthPt + (2 * margin), imageHeightPt + (2 * margin)),
                };

                // Shrink to fit inside the margins, never enlarge, and center.
                double scale = Math.Min(
                    1, Math.Min((pageWidth - (2 * margin)) / imageWidthPt, (pageHeight - (2 * margin)) / imageHeightPt));
                double drawWidth = imageWidthPt * scale;
                double drawHeight = imageHeightPt * scale;
                writer.AddPage(
                    image, pageWidth, pageHeight, (pageWidth - drawWidth) / 2, (pageHeight - drawHeight) / 2, drawWidth, drawHeight);
            }

            int pages = writer.PageCount;
            writer.Finish();
            return new ImagesToPdfResult(pages, new FileInfo(request.OutputPath).Length, passthrough);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModitException(ErrorCode.OutputWriteFailed, $"Could not write {request.OutputPath}: {ex.Message}", ex);
        }
    }

    private static (double Width, double Height) Orient(double width, double height, bool landscape) =>
        landscape ? (height, width) : (width, height);

    private static PdfImage Prepare(string path, double vectorDpi)
    {
        string format = ImageEngine.SniffReadable(path);
        if (format == "jpeg" && TryPassthroughJpeg(path) is { } jpeg)
        {
            return jpeg;
        }

        (byte[] pixels, int width, int height, int bands, double dpi) = ImageEngine.DecodeForPdf(path, vectorDpi);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(pixels);
        }

        return new PdfImage(compressed.ToArray(), "FlateDecode", width, height, bands == 1 ? "DeviceGray" : "DeviceRGB", dpi);
    }

    private static PdfImage? TryPassthroughJpeg(string path)
    {
        try
        {
            using Image header = Image.NewFromFile(path, access: Enums.Access.Sequential);
            int orientation = header.Contains("orientation") ? (int)header.Get("orientation") : 1;
            bool plain = header.Format == Enums.BandFormat.Uchar &&
                         orientation <= 1 &&
                         ((header.Bands == 3 && header.Interpretation == Enums.Interpretation.Srgb) ||
                          (header.Bands == 1 && header.Interpretation == Enums.Interpretation.Bw));
            if (!plain)
            {
                // CMYK, rotated by EXIF, or unusual: decode and re-store losslessly instead.
                return null;
            }

            return new PdfImage(
                File.ReadAllBytes(path), "DCTDecode", header.Width, header.Height,
                header.Bands == 1 ? "DeviceGray" : "DeviceRGB", ImageEngine.DpiOf(header));
        }
        catch (VipsException)
        {
            return null;
        }
    }

    private sealed record PdfImage(byte[] Data, string Filter, int Width, int Height, string ColorSpace, double Dpi);

    /// <summary>Minimal PDF 1.4 writer: catalog, page tree, one image XObject and content stream per page.</summary>
    private sealed class PdfImageWriter : IDisposable
    {
        private const int CatalogId = 1;
        private const int PagesId = 2;

        private readonly FileStream _stream;
        private readonly Dictionary<int, long> _offsets = [];
        private readonly List<int> _pageIds = [];
        private int _lastId = PagesId;

        public PdfImageWriter(string path)
        {
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            Ascii("%PDF-1.4\n");
            _stream.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']); // marks the file as binary
        }

        public int PageCount => _pageIds.Count;

        public void AddPage(PdfImage image, double pageWidth, double pageHeight, double x, double y, double width, double height)
        {
            int imageId = ++_lastId;
            Begin(imageId);
            Ascii(Invariant(
                $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /{image.ColorSpace} /BitsPerComponent 8 /Filter /{image.Filter} /Length {image.Data.Length} >>\nstream\n"));
            _stream.Write(image.Data);
            Ascii("\nendstream\nendobj\n");

            byte[] content = Encoding.ASCII.GetBytes(Invariant($"q {N(width)} 0 0 {N(height)} {N(x)} {N(y)} cm /Im0 Do Q\n"));
            int contentId = ++_lastId;
            Begin(contentId);
            Ascii(Invariant($"<< /Length {content.Length} >>\nstream\n"));
            _stream.Write(content);
            Ascii("\nendstream\nendobj\n");

            int pageId = ++_lastId;
            Begin(pageId);
            Ascii(Invariant(
                $"<< /Type /Page /Parent {PagesId} 0 R /MediaBox [0 0 {N(pageWidth)} {N(pageHeight)}] /Resources << /XObject << /Im0 {imageId} 0 R >> /ProcSet [/PDF /ImageB /ImageC] >> /Contents {contentId} 0 R >>\nendobj\n"));
            _pageIds.Add(pageId);
        }

        public void Finish()
        {
            Begin(PagesId);
            Ascii(Invariant($"<< /Type /Pages /Kids [{string.Join(' ', _pageIds.Select(id => Invariant($"{id} 0 R")))}] /Count {_pageIds.Count} >>\nendobj\n"));
            Begin(CatalogId);
            Ascii(Invariant($"<< /Type /Catalog /Pages {PagesId} 0 R >>\nendobj\n"));
            int infoId = ++_lastId;
            Begin(infoId);
            Ascii(Invariant($"<< /Producer (Modit) /CreationDate (D:{DateTime.UtcNow:yyyyMMddHHmmss}Z) >>\nendobj\n"));

            long xref = _stream.Position;
            var table = new StringBuilder();
            table.Append(Invariant($"xref\n0 {_lastId + 1}\n0000000000 65535 f \n"));
            for (int id = 1; id <= _lastId; id++)
            {
                table.Append(Invariant($"{_offsets[id]:D10} 00000 n \n"));
            }

            table.Append(Invariant($"trailer\n<< /Size {_lastId + 1} /Root {CatalogId} 0 R /Info {infoId} 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
            Ascii(table.ToString());
            _stream.Flush(flushToDisk: true);
        }

        public void Dispose() => _stream.Dispose();

        private void Begin(int id)
        {
            _offsets[id] = _stream.Position;
            Ascii(Invariant($"{id} 0 obj\n"));
        }

        private void Ascii(string text) => _stream.Write(Encoding.ASCII.GetBytes(text));

        private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
