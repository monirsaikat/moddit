using System.Globalization;
using System.Text.Json.Nodes;
using Modit.Core.Imaging;
using Modit.Engine;
using Modit.Engine.Client;

namespace Modit.Core.Pdf;

/// <summary>One input of a merge: a file, an optional page selection and an optional password.</summary>
public sealed record MergeSource(string Path, PageRange? Pages = null, string? Password = null);

public sealed record OutputOptions
{
    /// <summary>Replace an output file that already exists. Off by default: never destroy data silently.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>What a reader of a protected document may do without the owner password.</summary>
public sealed record ProtectOptions
{
    public required string UserPassword { get; init; }

    /// <summary>Password that lifts the restrictions. Defaults to the user password.</summary>
    public string? OwnerPassword { get; init; }

    public bool AllowPrinting { get; init; } = true;

    public bool AllowCopying { get; init; } = true;

    public bool AllowEditing { get; init; } = true;
}

public sealed record PdfOperationResult(
    string OutputPath, int PageCount, IReadOnlyList<string> Warnings, long InputBytes = 0, long OutputBytes = 0);

public sealed record PdfMultiResult(IReadOnlyList<string> OutputPaths);

public enum PdfPageSize
{
    /// <summary>Each page is exactly as large as its image (plus the margin).</summary>
    Image,
    A4,
    Letter,
}

public sealed record ImagesToPdfOptions
{
    public PdfPageSize PageSize { get; init; } = PdfPageSize.Image;

    /// <summary>Blank border around each image, in PDF points (1/72 inch).</summary>
    public double MarginPt { get; init; }

    /// <summary>Resolution SVG input is rendered at.</summary>
    public double VectorDpi { get; init; } = 96;
}

/// <summary>
/// PDF operations shared by the GUI and the CLI. Structural work is delegated to QPDF inside the
/// engine host; this class validates input, builds the job, and applies the safe-save contract:
/// write to a temporary file beside the target, verify that it re-opens with the expected page
/// count, then move it into place (docs/plan/02, "Save strategy"). The source file is never modified.
/// </summary>
public sealed partial class PdfService
{
    private const int QpdfExitWarning = 3;

    private readonly EngineClient _engine;

    public PdfService(EngineClient engine)
    {
        _engine = engine;
    }

    public async Task<DocumentInfo> GetInfoAsync(string path, string? password = null, CancellationToken cancellationToken = default)
    {
        DocumentInfo info = await _engine.OpenDocumentAsync(path, password, cancellationToken).ConfigureAwait(false);
        await _engine.CloseDocumentAsync(info.DocumentId, cancellationToken).ConfigureAwait(false);
        return info;
    }

    /// <summary>Combines files, or selected pages of them, in the given order.</summary>
    public async Task<PdfOperationResult> MergeAsync(
        IReadOnlyList<MergeSource> sources, string outputPath, OutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Choose at least one input file.");
        }

        outputPath = PrepareOutput(outputPath, options, sources.Select(s => s.Path));

        // Opening every input first turns bad passwords, damaged files and out-of-range pages into
        // precise errors before any output is written.
        var pages = new JsonArray();
        int expectedPages = 0;
        long inputBytes = 0;
        foreach (MergeSource source in sources)
        {
            string fullPath = Path.GetFullPath(source.Path);
            DocumentInfo info = await GetInfoAsync(fullPath, source.Password, cancellationToken).ConfigureAwait(false);
            PageRange range = source.Pages ?? PageRange.All;
            expectedPages += range.Resolve(info.PageCount).Count;
            inputBytes += info.FileSizeBytes;

            var entry = new JsonObject { ["file"] = fullPath, ["range"] = range.ToQpdfRange(info.PageCount) };
            if (source.Password is not null)
            {
                entry["password"] = source.Password;
            }

            pages.Add(entry);
        }

        JsonObject job = NewJob(sources[0].Path, sources[0].Password);
        job["pages"] = pages;
        return await RunToFileAsync(job, outputPath, options, expectedPages, null, inputBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Writes the selected pages of one document to a new file.</summary>
    public Task<PdfOperationResult> ExtractAsync(
        string sourcePath, PageRange pages, string outputPath, string? password = null, OutputOptions? options = null,
        CancellationToken cancellationToken = default) =>
        MergeAsync([new MergeSource(sourcePath, pages, password)], outputPath, options, cancellationToken);

    /// <summary>
    /// Splits a document into files of <paramref name="pagesPerFile"/> pages each, named
    /// <c>&lt;name&gt;_001.pdf</c>, <c>&lt;name&gt;_002.pdf</c> ... in <paramref name="outputDirectory"/>.
    /// </summary>
    public async Task<PdfMultiResult> SplitAsync(
        string sourcePath, int pagesPerFile, string outputDirectory, string? password = null, OutputOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (pagesPerFile < 1)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Pages per file must be at least 1.");
        }

        sourcePath = Path.GetFullPath(sourcePath);
        DocumentInfo info = await GetInfoAsync(sourcePath, password, cancellationToken).ConfigureAwait(false);
        int parts = (int)Math.Ceiling(info.PageCount / (double)pagesPerFile);
        int digits = Math.Max(3, parts.ToString(CultureInfo.InvariantCulture).Length);
        string baseName = Path.GetFileNameWithoutExtension(sourcePath);

        var outputs = new List<string>(parts);
        for (int part = 0; part < parts; part++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int first = (part * pagesPerFile) + 1;
            int last = Math.Min(info.PageCount, first + pagesPerFile - 1);
            string output = Path.Combine(
                outputDirectory,
                string.Create(CultureInfo.InvariantCulture, $"{baseName}_{(part + 1).ToString("D" + digits, CultureInfo.InvariantCulture)}.pdf"));

            PdfOperationResult result = await ExtractAsync(
                sourcePath, PageRange.Parse($"{first}-{last}"), output, password, options, cancellationToken).ConfigureAwait(false);
            outputs.Add(result.OutputPath);
            progress?.Report((part + 1) / (double)parts);
        }

        return new PdfMultiResult(outputs);
    }

    /// <summary>Rotates the selected pages clockwise by 90, 180 or 270 degrees.</summary>
    public async Task<PdfOperationResult> RotateAsync(
        string sourcePath, int degreesClockwise, PageRange? pages, string outputPath, string? password = null,
        OutputOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (degreesClockwise is not (90 or 180 or 270))
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Rotation must be 90, 180 or 270 degrees.");
        }

        outputPath = PrepareOutput(outputPath, options, [sourcePath]);
        DocumentInfo info = await GetInfoAsync(sourcePath, password, cancellationToken).ConfigureAwait(false);
        string range = (pages ?? PageRange.All).ToQpdfRange(info.PageCount);

        JsonObject job = NewJob(sourcePath, password);
        job["rotate"] = new JsonArray(string.Create(CultureInfo.InvariantCulture, $"+{degreesClockwise}:{range}"));
        return await RunToFileAsync(job, outputPath, options, info.PageCount, null, info.FileSizeBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Encrypts with AES-256 and applies the given restrictions.</summary>
    public async Task<PdfOperationResult> ProtectAsync(
        string sourcePath, ProtectOptions protection, string outputPath, string? currentPassword = null,
        OutputOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protection);
        if (string.IsNullOrEmpty(protection.UserPassword))
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Enter a password to protect the document.");
        }

        outputPath = PrepareOutput(outputPath, options, [sourcePath]);
        DocumentInfo info = await GetInfoAsync(sourcePath, currentPassword, cancellationToken).ConfigureAwait(false);

        JsonObject job = NewJob(sourcePath, currentPassword);
        job["encrypt"] = new JsonObject
        {
            ["userPassword"] = protection.UserPassword,
            ["ownerPassword"] = string.IsNullOrEmpty(protection.OwnerPassword) ? protection.UserPassword : protection.OwnerPassword,
            ["256bit"] = new JsonObject
            {
                ["print"] = protection.AllowPrinting ? "full" : "none",
                ["extract"] = protection.AllowCopying ? "y" : "n",
                ["modify"] = protection.AllowEditing ? "all" : "none",
            },
        };
        return await RunToFileAsync(
            job, outputPath, options, info.PageCount, protection.UserPassword, info.FileSizeBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Removes encryption. The password of the document is required; this is not a cracker.</summary>
    public async Task<PdfOperationResult> UnlockAsync(
        string sourcePath, string password, string outputPath, OutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        outputPath = PrepareOutput(outputPath, options, [sourcePath]);
        DocumentInfo info = await GetInfoAsync(sourcePath, password, cancellationToken).ConfigureAwait(false);

        JsonObject job = NewJob(sourcePath, password);
        job["decrypt"] = string.Empty;
        return await RunToFileAsync(job, outputPath, options, info.PageCount, null, info.FileSizeBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lossless size reduction: packs objects into compressed streams, recompresses at the highest
    /// level and drops unreferenced resources. Images are not resampled, so quality is unchanged.
    /// </summary>
    public async Task<PdfOperationResult> OptimizeAsync(
        string sourcePath, string outputPath, bool fastWebView = false, string? password = null,
        OutputOptions? options = null, CancellationToken cancellationToken = default)
    {
        outputPath = PrepareOutput(outputPath, options, [sourcePath]);
        DocumentInfo info = await GetInfoAsync(sourcePath, password, cancellationToken).ConfigureAwait(false);

        JsonObject job = NewJob(sourcePath, password);
        job["objectStreams"] = "generate";
        job["recompressFlate"] = string.Empty;
        job["compressionLevel"] = "9";
        job["removeUnreferencedResources"] = "yes";
        if (fastWebView)
        {
            job["linearize"] = string.Empty;
        }

        return await RunToFileAsync(
            job, outputPath, options, info.PageCount, info.IsEncrypted ? password : null, info.FileSizeBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds one PDF from images, one image per page, in the given order. JPEG photos are embedded
    /// unchanged; other formats are stored losslessly.
    /// </summary>
    public async Task<PdfOperationResult> FromImagesAsync(
        IReadOnlyList<string> imagePaths, string outputPath, ImagesToPdfOptions? imageOptions = null,
        OutputOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        if (imagePaths.Count == 0)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Choose at least one image.");
        }

        imageOptions ??= new ImagesToPdfOptions();
        if (imageOptions.MarginPt is < 0 or > 288)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "The margin must be between 0 and 288 points (4 inches).");
        }

        outputPath = PrepareOutput(outputPath, options, imagePaths);
        string[] sources = [.. imagePaths.Select(Path.GetFullPath)];
        foreach (string source in sources.Where(s => !File.Exists(s)))
        {
            throw new ModitException(ErrorCode.FileNotFound, $"File not found: {source}");
        }

        string tempPath = outputPath + ".modit-tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var request = new ImagesToPdfRequest(
                sources, tempPath, imageOptions.PageSize.ToString().ToLowerInvariant(), imageOptions.MarginPt, imageOptions.VectorDpi);
            await _engine.ImagesToPdfAsync(request, cancellationToken).ConfigureAwait(false);

            DocumentInfo written = await GetInfoAsync(tempPath, null, cancellationToken).ConfigureAwait(false);
            if (written.PageCount != sources.Length)
            {
                throw new ModitException(
                    ErrorCode.PdfStructureOperationFailed,
                    $"Verification failed: expected {sources.Length} pages but the output has {written.PageCount}.");
            }

            File.Move(tempPath, outputPath, options?.Overwrite == true);
            long inputBytes = sources.Sum(s => new FileInfo(s).Length);
            return new PdfOperationResult(outputPath, written.PageCount, [], inputBytes, written.FileSizeBytes);
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

    /// <summary>Renders the selected pages to PNG files named <c>&lt;name&gt;_p001.png</c> ...</summary>
    public async Task<PdfMultiResult> ExportImagesAsync(
        string sourcePath, PageRange? pages, double dpi, string outputDirectory, string? password = null,
        OutputOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (dpi is < 8 or > 1200)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Resolution must be between 8 and 1200 dpi.");
        }

        options ??= new OutputOptions();
        sourcePath = Path.GetFullPath(sourcePath);
        DocumentInfo info = await _engine.OpenDocumentAsync(sourcePath, password, cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<int> indexes = (pages ?? PageRange.All).Resolve(Math.Max(1, info.PageCount));
            int digits = Math.Max(3, info.PageCount.ToString(CultureInfo.InvariantCulture).Length);
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            Directory.CreateDirectory(outputDirectory);

            var outputs = new List<string>(indexes.Count);
            for (int i = 0; i < indexes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string number = (indexes[i] + 1).ToString("D" + digits, CultureInfo.InvariantCulture);
                string output = Path.GetFullPath(Path.Combine(outputDirectory, $"{baseName}_p{number}.png"));
                if (File.Exists(output) && !options.Overwrite)
                {
                    throw new ModitException(ErrorCode.OutputWriteFailed, $"The output file already exists: {output}");
                }

                using RenderedImage image = await _engine.RenderPageAsync(info, indexes[i], dpi / 72.0, cancellationToken)
                    .ConfigureAwait(false);
                string temp = output + ".modit-tmp";
                try
                {
                    using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        PngEncoder.WriteBgra(stream, image.Pixels, image.WidthPx, image.HeightPx, image.StrideBytes);
                    }

                    File.Move(temp, output, overwrite: true);
                }
                finally
                {
                    TryDelete(temp);
                }

                outputs.Add(output);
                progress?.Report((i + 1) / (double)indexes.Count);
            }

            return new PdfMultiResult(outputs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModitException(ErrorCode.OutputWriteFailed, $"Could not write to {outputDirectory}: {ex.Message}", ex);
        }
        finally
        {
            await _engine.CloseDocumentAsync(info.DocumentId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static JsonObject NewJob(string inputPath, string? password)
    {
        var job = new JsonObject { ["inputFile"] = Path.GetFullPath(inputPath) };
        if (!string.IsNullOrEmpty(password))
        {
            job["password"] = password;
        }

        return job;
    }

    /// <summary>Normalizes the output path and rejects overwriting an input or an existing file.</summary>
    private static string PrepareOutput(string outputPath, OutputOptions? options, IEnumerable<string> inputs)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Choose where to save the result.");
        }

        outputPath = Path.GetFullPath(outputPath);
        if (inputs.Any(i => string.Equals(Path.GetFullPath(i), outputPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ModitException(ErrorCode.InvalidArgument, "The output file cannot also be an input file.");
        }

        if (File.Exists(outputPath) && options?.Overwrite != true)
        {
            throw new ModitException(
                ErrorCode.OutputWriteFailed, $"The output file already exists: {outputPath}. Use overwrite to replace it.");
        }

        return outputPath;
    }

    private async Task<PdfOperationResult> RunToFileAsync(
        JsonObject job, string outputPath, OutputOptions? options, int expectedPages, string? outputPassword,
        long inputBytes, CancellationToken cancellationToken)
    {
        string tempPath = outputPath + ".modit-tmp";
        job["outputFile"] = tempPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            QpdfJobResult result = await _engine.RunQpdfJobAsync(job.ToJsonString(), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode is not (0 or QpdfExitWarning))
            {
                throw new ModitException(
                    ErrorCode.PdfStructureOperationFailed,
                    result.Errors.Count > 0 ? string.Join(" ", result.Errors) : "The operation failed.");
            }

            DocumentInfo written = await GetInfoAsync(tempPath, outputPassword, cancellationToken).ConfigureAwait(false);
            if (written.PageCount != expectedPages)
            {
                throw new ModitException(
                    ErrorCode.PdfStructureOperationFailed,
                    $"Verification failed: expected {expectedPages} pages but the output has {written.PageCount}.");
            }

            File.Move(tempPath, outputPath, options?.Overwrite == true);
            return new PdfOperationResult(
                outputPath, written.PageCount, [.. result.Warnings, .. result.Errors], inputBytes, written.FileSizeBytes);
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swept on next startup (docs/plan/07, "Disk I/O").
        }
    }
}
