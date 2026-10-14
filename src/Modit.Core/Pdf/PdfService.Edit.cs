using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Modit.Engine;

namespace Modit.Core.Pdf;

/// <summary>A document used by <see cref="PdfService.OrganizeAsync"/>.</summary>
public sealed record OrganizeSource(string Path, string? Password = null);

/// <summary>
/// One page of an organized document: page <see cref="PageIndex"/> (0-based) of source
/// <see cref="SourceIndex"/>, turned clockwise by <see cref="RotationDegrees"/> on top of its own rotation.
/// <see cref="SourceIndex"/> -1 is a blank page of the given size.
/// </summary>
public sealed record OrganizePage(
    int SourceIndex, int PageIndex, int RotationDegrees = 0, double BlankWidthPt = 595.28, double BlankHeightPt = 841.89)
{
    public const int BlankSource = -1;

    public static OrganizePage Blank(double widthPt, double heightPt) => new(BlankSource, 0, 0, widthPt, heightPt);

    public bool IsBlank => SourceIndex == BlankSource;
}

/// <summary>Changes drawn onto pages. See <see cref="PdfEditRequest"/> for the order they are applied in.</summary>
public sealed record PdfEditOptions
{
    /// <summary>Pages to change; null means every page.</summary>
    public PageRange? Pages { get; init; }

    public PdfTextStamp? Text { get; init; }

    public PdfImageStamp? Image { get; init; }

    public PdfCrop? Crop { get; init; }

    /// <summary>Burns form fields and annotations into the page so they can no longer be changed.</summary>
    public bool Flatten { get; init; }
}

public sealed partial class PdfService
{
    /// <summary>
    /// Builds a new document from pages of one or more documents in any order, with extra rotation and blank
    /// pages. QPDF does the work, so links, form fields and page content are carried over unchanged.
    /// </summary>
    public async Task<PdfOperationResult> OrganizeAsync(
        IReadOnlyList<OrganizeSource> sources, IReadOnlyList<OrganizePage> pages, string outputPath,
        OutputOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "The document must keep at least one page.");
        }

        if (pages.Any(p => p.RotationDegrees % 90 != 0))
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Pages can only be turned in steps of 90 degrees.");
        }

        outputPath = PrepareOutput(outputPath, options, sources.Select(s => s.Path));
        string[] paths = [.. sources.Select(s => Path.GetFullPath(s.Path))];
        long inputBytes = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            DocumentInfo info = await GetInfoAsync(paths[i], sources[i].Password, cancellationToken).ConfigureAwait(false);
            inputBytes += info.FileSizeBytes;
            foreach (OrganizePage page in pages.Where(p => p.SourceIndex == i))
            {
                if (page.PageIndex < 0 || page.PageIndex >= info.PageCount)
                {
                    throw new ModitException(
                        ErrorCode.PdfPageOutOfRange, $"{Path.GetFileName(paths[i])} has no page {page.PageIndex + 1}.");
                }
            }
        }

        if (pages.Any(p => !p.IsBlank && (p.SourceIndex < 0 || p.SourceIndex >= sources.Count)))
        {
            throw new ModitException(ErrorCode.InvalidArgument, "A page refers to a document that is not in the list.");
        }

        // Blank pages come from a small generated PDF holding one page per blank, in order.
        string? blankPath = null;
        var blankNumbers = new Dictionary<int, int>();
        List<int> blanks = [.. Enumerable.Range(0, pages.Count).Where(i => pages[i].IsBlank)];
        try
        {
            if (blanks.Count > 0)
            {
                blankPath = outputPath + ".blank.modit-tmp";
                BlankPdf.Write(blankPath, [.. blanks.Select(i => (pages[i].BlankWidthPt, pages[i].BlankHeightPt))]);
                for (int n = 0; n < blanks.Count; n++)
                {
                    blankNumbers[blanks[n]] = n + 1;
                }
            }

            // Consecutive pages from the same file become one QPDF page spec such as "3,1,2".
            var specs = new JsonArray();
            int run = 0;
            while (run < pages.Count)
            {
                int source = pages[run].SourceIndex;
                var numbers = new List<int>();
                int next = run;
                while (next < pages.Count && pages[next].SourceIndex == source)
                {
                    numbers.Add(pages[next].IsBlank ? blankNumbers[next] : pages[next].PageIndex + 1);
                    next++;
                }

                var spec = new JsonObject
                {
                    ["file"] = source == OrganizePage.BlankSource ? blankPath : paths[source],
                    ["range"] = string.Join(',', numbers.Select(n => n.ToString(CultureInfo.InvariantCulture))),
                };
                if (source >= 0 && sources[source].Password is { } password)
                {
                    spec["password"] = password;
                }

                specs.Add(spec);
                run = next;
            }

            // Rotation applies to pages of the output, after they are assembled.
            var rotations = new JsonArray();
            foreach (IGrouping<int, int> group in Enumerable.Range(0, pages.Count)
                .GroupBy(i => ((pages[i].RotationDegrees % 360) + 360) % 360)
                .Where(g => g.Key != 0))
            {
                rotations.Add(string.Create(
                    CultureInfo.InvariantCulture, $"+{group.Key}:{string.Join(',', group.Select(i => i + 1))}"));
            }

            JsonObject job = sources.Count > 0 ? NewJob(paths[0], sources[0].Password) : NewJob(blankPath!, null);
            job["pages"] = specs;
            if (rotations.Count > 0)
            {
                job["rotate"] = rotations;
            }

            return await RunToFileAsync(job, outputPath, options, pages.Count, null, inputBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (blankPath is not null)
            {
                TryDelete(blankPath);
            }
        }
    }

    /// <summary>Writes a copy without the selected pages.</summary>
    public async Task<PdfOperationResult> RemovePagesAsync(
        string sourcePath, PageRange remove, string outputPath, string? password = null, OutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remove);
        DocumentInfo info = await GetInfoAsync(sourcePath, password, cancellationToken).ConfigureAwait(false);
        var removed = new HashSet<int>(remove.Resolve(info.PageCount));
        List<int> keep = [.. Enumerable.Range(0, info.PageCount).Where(i => !removed.Contains(i))];
        if (keep.Count == 0)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "That would remove every page. Keep at least one.");
        }

        return await ExtractAsync(sourcePath, PageRange.Parse(ToRangeText(keep)), outputPath, password, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Draws text or a picture on pages, crops them or flattens forms, then saves a full copy. If the source was
    /// password protected, the copy is not (the result carries a warning).
    /// </summary>
    public async Task<PdfOperationResult> EditAsync(
        string sourcePath, PdfEditOptions edit, string outputPath, string? password = null, OutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        outputPath = PrepareOutput(outputPath, options, edit.Image is { } image ? [sourcePath, image.ImagePath] : [sourcePath]);
        string source = Path.GetFullPath(sourcePath);
        DocumentInfo info = await GetInfoAsync(source, password, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<int>? indexes = edit.Pages?.Resolve(info.PageCount);

        PdfTextStamp? text = edit.Text;
        if (text is not null && string.IsNullOrEmpty(text.DocumentName))
        {
            text = text with { DocumentName = Path.GetFileNameWithoutExtension(source) };
        }

        PdfImageStamp? picture = edit.Image is { } stamp ? stamp with { ImagePath = Path.GetFullPath(stamp.ImagePath) } : null;
        string tempPath = outputPath + ".modit-tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var request = new PdfEditRequest(source, password, tempPath, indexes, text, picture, edit.Crop, edit.Flatten);
            PdfEditResult result = await _engine.EditPdfAsync(request, cancellationToken).ConfigureAwait(false);

            DocumentInfo written = await GetInfoAsync(tempPath, null, cancellationToken).ConfigureAwait(false);
            if (written.PageCount != info.PageCount)
            {
                throw new ModitException(
                    ErrorCode.PdfStructureOperationFailed,
                    $"Verification failed: expected {info.PageCount} pages but the output has {written.PageCount}.");
            }

            File.Move(tempPath, outputPath, options?.Overwrite == true);
            return new PdfOperationResult(outputPath, written.PageCount, result.Warnings, info.FileSizeBytes, written.FileSizeBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModitException(ErrorCode.OutputWriteFailed, $"Could not write {outputPath}: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Saves the text of the selected pages as UTF-8. Pages are separated by a form feed, as pdftotext does,
    /// so tools can split them again. Scanned pages without a text layer come out empty.
    /// </summary>
    public async Task<PdfOperationResult> ExportTextAsync(
        string sourcePath, string outputPath, PageRange? pages = null, string? password = null,
        OutputOptions? options = null, CancellationToken cancellationToken = default)
    {
        outputPath = PrepareOutput(outputPath, options, [sourcePath]);
        DocumentInfo info = await _engine.OpenDocumentAsync(sourcePath, password, cancellationToken).ConfigureAwait(false);
        string tempPath = outputPath + ".modit-tmp";
        try
        {
            IReadOnlyList<int> indexes = (pages ?? PageRange.All).Resolve(Math.Max(1, info.PageCount));
            var text = new StringBuilder();
            int emptyPages = 0;
            for (int i = 0; i < indexes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string pageText = await _engine.GetPageTextAsync(info.DocumentId, indexes[i], cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(pageText))
                {
                    emptyPages++;
                }

                if (i > 0)
                {
                    text.Append('\f');
                }

                text.Append(pageText.TrimEnd()).Append("\r\n");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(tempPath, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempPath, outputPath, options?.Overwrite == true);

            List<string> warnings = emptyPages == 0
                ? []
                : [string.Create(CultureInfo.InvariantCulture, $"{emptyPages} page(s) have no text layer (scanned images need OCR).")];
            return new PdfOperationResult(outputPath, indexes.Count, warnings, info.FileSizeBytes, new FileInfo(outputPath).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModitException(ErrorCode.OutputWriteFailed, $"Could not write {outputPath}: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(tempPath);
            await _engine.CloseDocumentAsync(info.DocumentId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>0-based page indexes to QPDF/Modit range text: [0,1,2,4] becomes "1-3,5".</summary>
    internal static string ToRangeText(IReadOnlyList<int> indexes)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < indexes.Count)
        {
            int start = indexes[i];
            int end = start;
            while (i + 1 < indexes.Count && indexes[i + 1] == end + 1)
            {
                end = indexes[++i];
            }

            parts.Add(start == end
                ? (start + 1).ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{start + 1}-{end + 1}"));
            i++;
        }

        return string.Join(',', parts);
    }
}

/// <summary>Writes a PDF of empty pages with the given sizes (in points). Used for "insert blank page".</summary>
internal static class BlankPdf
{
    public static void Write(string path, IReadOnlyList<(double WidthPt, double HeightPt)> sizes)
    {
        // Objects: 1 catalog, 2 page tree, 3.. pages.
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Create(
                CultureInfo.InvariantCulture,
                $"<< /Type /Pages /Kids [{string.Join(' ', sizes.Select((_, i) => $"{i + 3} 0 R"))}] /Count {sizes.Count} >>"),
        };
        bodies.AddRange(sizes.Select(s => string.Create(
            CultureInfo.InvariantCulture,
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {s.WidthPt:0.##} {s.HeightPt:0.##}] /Resources << >> >>")));

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
    }
}
