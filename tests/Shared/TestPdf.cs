using System.Globalization;
using System.Text;

namespace Modit.TestSupport;

/// <summary>
/// Writes small, valid, fully redistributable PDFs for tests: one line of Helvetica text per page.
/// Generated at test time so no binary fixtures need to be committed for the basics.
/// </summary>
public static class TestPdf
{
    public const float LetterWidthPt = 612;
    public const float LetterHeightPt = 792;

    public static string Create(string directory, string name, int pageCount, string textPrefix = "Hello Modit", string? title = null)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Build(pageCount, textPrefix, title));
        return path;
    }

    public static byte[] Build(int pageCount, string textPrefix = "Hello Modit", string? title = null)
    {
        // Object layout: 1 catalog, 2 pages, 3 font, 4 info, then (page, content) pairs.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Empty,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Title ({title ?? "Modit test document"}) /Producer (Modit tests) >>",
        };

        var kids = new StringBuilder();
        for (int i = 0; i < pageCount; i++)
        {
            int pageObject = objects.Count + 1;
            int contentObject = pageObject + 1;
            kids.Append(CultureInfo.InvariantCulture, $"{pageObject} 0 R ");

            string content = $"BT /F1 24 Tf 72 700 Td ({textPrefix} page {i + 1}) Tj ET\n" +
                             "0 0 0 rg 72 600 200 40 re f";
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {LetterWidthPt} {LetterHeightPt}] " +
                $"/Resources << /Font << /F1 3 0 R >> >> /Contents {contentObject} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        objects[1] = $"<< /Type /Pages /Count {pageCount} /Kids [ {kids}] >>";

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(
            CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 4 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        // Content is pure ASCII, so string offsets equal byte offsets.
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
