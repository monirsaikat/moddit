using Modit.Engine;
using Modit.Engine.Client;

namespace Modit.Core.Convert;

/// <summary>An image format the converters know. Format names are proper nouns and are not localized.</summary>
public sealed record ImageFormatInfo(
    string Id,
    string DisplayName,
    string Extension,
    IReadOnlyList<string> Extensions,
    bool CanWrite,
    bool HasQuality,
    bool HasLossless,
    bool KeepsTransparency,
    bool KeepsAnimation);

public static class ImageFormats
{
    public static IReadOnlyList<ImageFormatInfo> All { get; } =
    [
        new("png", "PNG", ".png", [".png"], CanWrite: true, HasQuality: false, HasLossless: false, KeepsTransparency: true, KeepsAnimation: false),
        new("jpeg", "JPEG", ".jpg", [".jpg", ".jpeg", ".jpe", ".jfif"], true, true, false, false, false),
        new("webp", "WebP", ".webp", [".webp"], true, true, true, true, true),
        new("avif", "AVIF", ".avif", [".avif"], true, true, true, true, false),
        new("tiff", "TIFF", ".tif", [".tif", ".tiff"], true, false, false, true, false),
        new("gif", "GIF", ".gif", [".gif"], true, false, false, true, true),
        new("bmp", "BMP", ".bmp", [".bmp", ".dib"], true, false, false, false, false),
        new("ico", "ICO", ".ico", [".ico"], true, false, false, true, false),
        new("heic", "HEIC", ".heic", [".heic", ".heif"], false, false, false, false, false),
        new("svg", "SVG", ".svg", [".svg"], false, false, false, true, false),
    ];

    public static IReadOnlyList<ImageFormatInfo> Writable { get; } = [.. All.Where(f => f.CanWrite)];

    /// <summary>Every extension the converters accept as input, with the leading dot.</summary>
    public static IReadOnlyList<string> InputExtensions { get; } = [.. All.SelectMany(f => f.Extensions)];

    /// <summary>Finds a format by id (<c>jpeg</c>) or by extension with or without the dot (<c>jpg</c>, <c>.tif</c>).</summary>
    public static ImageFormatInfo? Find(string idOrExtension)
    {
        string key = idOrExtension.Trim().ToLowerInvariant();
        string dotted = key.StartsWith('.') ? key : "." + key;
        return All.FirstOrDefault(f => f.Id == key || f.Extensions.Contains(dotted));
    }

    public static bool IsInput(string path) =>
        InputExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}

public enum CollisionPolicy
{
    /// <summary>Write <c>name (2).ext</c> next to an existing <c>name.ext</c>.</summary>
    Rename,

    Overwrite,

    Skip,
}

public sealed record ImageConvertOptions
{
    public required string TargetFormat { get; init; }

    /// <summary>1 to 100, for JPEG, WebP and AVIF.</summary>
    public int Quality { get; init; } = 85;

    /// <summary>WebP and AVIF only.</summary>
    public bool Lossless { get; init; }

    /// <summary>Shrink to fit this width; 0 keeps the size. SVG input is rendered to fit instead.</summary>
    public int MaxWidthPx { get; init; }

    public int MaxHeightPx { get; init; }

    /// <summary>Keep EXIF, XMP and ICC data. Turning this off also drops GPS locations from photos.</summary>
    public bool KeepMetadata { get; init; } = true;

    /// <summary>Fills transparent areas for formats without transparency (JPEG, BMP).</summary>
    public string Background { get; init; } = "#FFFFFF";

    /// <summary>Resolution SVG input is rendered at when no size limit is given.</summary>
    public double VectorDpi { get; init; } = 96;

    public bool KeepAnimation { get; init; } = true;

    /// <summary>Folder for the results; null writes each result next to its source.</summary>
    public string? OutputDirectory { get; init; }

    public CollisionPolicy Collision { get; init; } = CollisionPolicy.Rename;
}

public enum ConvertItemStatus
{
    Converted,
    Skipped,
    Failed,
}

public sealed record ConvertItemResult(
    string Input,
    string? Output,
    ConvertItemStatus Status,
    ErrorCode Error = ErrorCode.None,
    string? Message = null,
    int Width = 0,
    int Height = 0,
    long InputBytes = 0,
    long OutputBytes = 0,
    IReadOnlyList<string>? Warnings = null);

public sealed record ConvertBatchResult(IReadOnlyList<ConvertItemResult> Items)
{
    public int Converted => Items.Count(i => i.Status == ConvertItemStatus.Converted);

    public int Skipped => Items.Count(i => i.Status == ConvertItemStatus.Skipped);

    public int Failed => Items.Count(i => i.Status == ConvertItemStatus.Failed);
}

public sealed record ConvertProgress(int Done, int Total, string? Current);

/// <summary>
/// Converts images in a batch. One bad file never stops the batch: it is reported as failed and the rest
/// continue. Each result is written to a temporary file, verified by reading it back, then moved into place,
/// so a failure never leaves a half-written image under the final name and sources are never modified.
/// </summary>
public sealed class ImageConvertService
{
    private const string TempSuffix = ".modit-tmp";

    private readonly EngineClient _engine;

    public ImageConvertService(EngineClient engine)
    {
        _engine = engine;
    }

    public async Task<ConvertBatchResult> ConvertAsync(
        IReadOnlyList<string> inputs, ImageConvertOptions options, IProgress<ConvertProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);
        ImageFormatInfo target = Validate(inputs, options);

        var results = new List<ConvertItemResult>(inputs.Count);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ConvertProgress(i, inputs.Count, inputs[i]));
            results.Add(await ConvertOneAsync(inputs[i], target, options, reserved, cancellationToken).ConfigureAwait(false));
        }

        progress?.Report(new ConvertProgress(inputs.Count, inputs.Count, null));
        return new ConvertBatchResult(results);
    }

    /// <summary>
    /// The path a result would be written to, or null when <see cref="CollisionPolicy.Skip"/> applies. Paths in
    /// <paramref name="reserved"/> are treated as taken; the source itself is never a valid destination.
    /// </summary>
    internal static string? ResolveOutput(
        string source, string directory, string extension, CollisionPolicy policy, ISet<string> reserved)
    {
        string baseName = Path.GetFileNameWithoutExtension(source);
        string first = Path.GetFullPath(Path.Combine(directory, baseName + extension));
        bool IsTaken(string path) =>
            reserved.Contains(path) || File.Exists(path) || Directory.Exists(path) ||
            string.Equals(path, source, StringComparison.OrdinalIgnoreCase);

        if (!IsTaken(first))
        {
            return first;
        }

        bool replaceable = policy == CollisionPolicy.Overwrite && !reserved.Contains(first) &&
                           !string.Equals(first, source, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(first);
        if (replaceable)
        {
            return first;
        }

        if (policy == CollisionPolicy.Skip)
        {
            return null;
        }

        for (int n = 2; ; n++)
        {
            string candidate = Path.GetFullPath(Path.Combine(
                directory, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{baseName} ({n}){extension}")));
            if (!IsTaken(candidate))
            {
                return candidate;
            }
        }
    }

    private static ImageFormatInfo Validate(IReadOnlyList<string> inputs, ImageConvertOptions options)
    {
        if (inputs.Count == 0)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Choose at least one image.");
        }

        ImageFormatInfo target = ImageFormats.Find(options.TargetFormat) is { CanWrite: true } found
            ? found
            : throw new ModitException(
                ErrorCode.ConvertUnsupportedFormat,
                $"Modit cannot write '{options.TargetFormat}'. Choose one of: {string.Join(", ", ImageFormats.Writable.Select(f => f.Id))}.");

        if (options.Quality is < 1 or > 100)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Quality must be between 1 and 100.");
        }

        if (options.MaxWidthPx < 0 || options.MaxHeightPx < 0)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Width and height limits cannot be negative.");
        }

        if (options.VectorDpi is < 8 or > 2400)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "SVG resolution must be between 8 and 2400 dpi.");
        }

        return target;
    }

    private async Task<ConvertItemResult> ConvertOneAsync(
        string input, ImageFormatInfo target, ImageConvertOptions options, HashSet<string> reserved,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(input);
        if (!File.Exists(source))
        {
            return new ConvertItemResult(source, null, ConvertItemStatus.Failed, ErrorCode.FileNotFound, $"File not found: {source}");
        }

        long inputBytes = new FileInfo(source).Length;
        string directory = options.OutputDirectory is { Length: > 0 } folder
            ? Path.GetFullPath(folder)
            : Path.GetDirectoryName(source)!;
        string? output = ResolveOutput(source, directory, target.Extension, options.Collision, reserved);
        if (output is null)
        {
            return new ConvertItemResult(source, null, ConvertItemStatus.Skipped, Message: "A file with that name already exists.");
        }

        reserved.Add(output);
        string temp = output + TempSuffix;
        try
        {
            Directory.CreateDirectory(directory);
            var request = new ImageConvertRequest(
                source, temp, target.Id, options.Quality, options.Lossless, options.MaxWidthPx, options.MaxHeightPx,
                options.KeepMetadata, options.Background, options.VectorDpi, options.KeepAnimation);
            ImageConvertResult converted = await _engine.ConvertImageAsync(request, cancellationToken).ConfigureAwait(false);

            // Read the result back before it gets its real name.
            ImageInfoResult written = await _engine.GetImageInfoAsync(temp, cancellationToken).ConfigureAwait(false);
            if (written.Format != target.Id || written.Width <= 0 || written.Height <= 0)
            {
                throw new ModitException(
                    ErrorCode.ConvertFailed, $"Verification failed: the result reads back as {written.Format}, not {target.Id}.");
            }

            File.Move(temp, output, overwrite: options.Collision == CollisionPolicy.Overwrite);
            return new ConvertItemResult(
                source, output, ConvertItemStatus.Converted, Width: converted.Width, Height: converted.Height,
                InputBytes: inputBytes, OutputBytes: converted.OutputBytes, Warnings: converted.Warnings);
        }
        catch (ModitException ex) when (ex.Code != ErrorCode.Cancelled)
        {
            reserved.Remove(output);
            return new ConvertItemResult(source, null, ConvertItemStatus.Failed, ex.Code, ex.Message, InputBytes: inputBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reserved.Remove(output);
            return new ConvertItemResult(
                source, null, ConvertItemStatus.Failed, ErrorCode.OutputWriteFailed, $"Could not write {output}: {ex.Message}", InputBytes: inputBytes);
        }
        finally
        {
            TryDelete(temp);
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
            // Best effort: a leftover .modit-tmp file is harmless and never mistaken for a result.
        }
    }
}
