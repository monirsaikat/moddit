using System.Globalization;
using NetVips;

namespace Modit.Engine.Host.Engines;

/// <summary>
/// Raster and vector image conversion on libvips (ADR-005). libvips reads PNG, JPEG, WebP, AVIF, HEIF, GIF,
/// TIFF and SVG; BMP and ICO (and HEIC when libvips lacks the HEVC decoder) are decoded by the Windows
/// Imaging Component. BMP and ICO are written by small writers of our own because the libvips bundle has none.
/// </summary>
internal static class ImageEngine
{
    /// <summary>Formats <see cref="Convert"/> can write, by canonical id.</summary>
    public static readonly IReadOnlySet<string> WritableFormats =
        new HashSet<string>(StringComparer.Ordinal) { "png", "jpeg", "webp", "avif", "tiff", "gif", "bmp", "ico" };

    /// <summary>Formats that can be read, by canonical id (see <see cref="FormatSniffer"/>).</summary>
    public static readonly IReadOnlySet<string> ReadableFormats =
        new HashSet<string>(StringComparer.Ordinal) { "png", "jpeg", "webp", "avif", "heic", "tiff", "gif", "bmp", "ico", "svg" };

    public const double DefaultVectorDpi = 96;

    private const int NoLimit = 10_000_000;
    private const int MaxIconSize = 256;
    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256];

    static ImageEngine()
    {
        // Operation caching would keep input files open (and locked on Windows) after a conversion.
        Cache.Max = 0;
        Cache.MaxFiles = 0;
        Cache.MaxMem = 64L * 1024 * 1024;
        NetVips.NetVips.Concurrency = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    }

    public static ImageInfoResult GetInfo(string path)
    {
        string format = SniffReadable(path);
        try
        {
            if (UsesWindowsCodec(format))
            {
                return WicDecoder.GetInfo(path, format);
            }

            using Image image = Image.NewFromFile(path, access: Enums.Access.Sequential);
            return new ImageInfoResult(
                format, image.Width, PageHeight(image), image.Bands, image.HasAlpha(), FrameCount(image), DpiOf(image));
        }
        catch (VipsException) when (format == "heic")
        {
            return WicDecoder.GetInfo(path, format);
        }
        catch (VipsException ex)
        {
            throw InputError(ex, path);
        }
    }

    public static ImageConvertResult Convert(ImageConvertRequest request, CancellationToken cancellationToken)
    {
        string target = request.TargetFormat.ToLowerInvariant();
        if (!WritableFormats.Contains(target))
        {
            throw new ModitException(ErrorCode.ConvertUnsupportedFormat, $"Modit cannot write '{request.TargetFormat}' images.");
        }

        string format = SniffReadable(request.InputPath);
        var warnings = new List<string>();
        bool keepAnimation = request.KeepAnimation && target is "gif" or "webp";
        int maxWidth = request.MaxWidthPx;
        int maxHeight = request.MaxHeightPx;
        if (target == "ico")
        {
            // The largest icon entry is 256 x 256; anything bigger is wasted decoding.
            maxWidth = maxWidth > 0 ? Math.Min(maxWidth, MaxIconSize) : MaxIconSize;
            maxHeight = maxHeight > 0 ? Math.Min(maxHeight, MaxIconSize) : MaxIconSize;
        }

        using var owned = new ImageScope();
        Loaded loaded;
        try
        {
            loaded = Load(owned, request.InputPath, format, maxWidth, maxHeight, request.VectorDpi, keepAnimation);
        }
        catch (VipsException ex)
        {
            throw InputError(ex, request.InputPath);
        }

        if (loaded.SourceFrames > 1 && loaded.Frames == 1)
        {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The source has {loaded.SourceFrames} frames or pages; only the first was converted."));
        }

        try
        {
            Image ready = Prepare(owned, loaded.Image, target, request.Background);
            ready.SetProgress(NoProgress.Instance, cancellationToken);
            Save(owned, ready, target, request);
            cancellationToken.ThrowIfCancellationRequested();
            return new ImageConvertResult(
                ready.Width, loaded.Frames > 1 ? PageHeight(ready) : ready.Height, loaded.Frames,
                new FileInfo(request.OutputPath).Length, warnings);
        }
        catch (VipsException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The conversion was cancelled.", ex, cancellationToken);
        }
        catch (VipsException ex)
        {
            throw new ModitException(ErrorCode.ConvertFailed, $"The image could not be written: {Describe(ex.Message)}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModitException(ErrorCode.OutputWriteFailed, $"Could not write {request.OutputPath}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Decodes an image for embedding in a PDF: upright, 8-bit, gray or sRGB, no alpha (flattened on white).
    /// </summary>
    internal static (byte[] Pixels, int Width, int Height, int Bands, double Dpi) DecodeForPdf(string path, double vectorDpi)
    {
        string format = SniffReadable(path);
        using var owned = new ImageScope();
        try
        {
            Loaded loaded = Load(owned, path, format, 0, 0, vectorDpi, keepAnimation: false);
            Image image = Prepare(owned, loaded.Image, "jpeg", "#FFFFFF");
            if (image.Bands == 2 || image.Bands > 3)
            {
                image = owned.Add(image.Colourspace(Enums.Interpretation.Srgb));
            }

            double dpi = format == "svg" ? EffectiveVectorDpi(vectorDpi) : DpiOf(loaded.Image);
            return (image.WriteToMemory<byte>(), image.Width, image.Height, image.Bands, dpi);
        }
        catch (VipsException ex)
        {
            throw InputError(ex, path);
        }
    }

    internal static string SniffReadable(string path)
    {
        string? format;
        try
        {
            format = FormatSniffer.Detect(path);
        }
        catch (FileNotFoundException ex)
        {
            throw new ModitException(ErrorCode.FileNotFound, $"File not found: {path}", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new ModitException(ErrorCode.FileNotFound, $"File not found: {path}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ModitException(ErrorCode.FileAccessDenied, $"Access to {path} was denied.", ex);
        }

        if (format == "pdf")
        {
            throw new ModitException(
                ErrorCode.ConvertUnsupportedFormat, $"{Path.GetFileName(path)} is a PDF. Use the PDF tools to turn PDF pages into images.");
        }

        if (format is null || !ReadableFormats.Contains(format))
        {
            throw new ModitException(
                ErrorCode.ConvertUnsupportedFormat, $"{Path.GetFileName(path)} is not an image format Modit can read.");
        }

        return format;
    }

    internal static double DpiOf(Image image)
    {
        // libvips stores resolution in pixels per millimetre and reports 72 dpi when the file has none.
        double dpi = image.Xres * 25.4;
        return dpi is > 1 and < 100_000 ? dpi : 72;
    }

    private static double EffectiveVectorDpi(double requested) => requested > 0 ? requested : DefaultVectorDpi;

    private static bool UsesWindowsCodec(string format) => format is "bmp" or "ico";

    private static Loaded Load(
        ImageScope owned, string path, string format, int maxWidth, int maxHeight, double vectorDpi, bool keepAnimation)
    {
        if (format == "svg")
        {
            return new Loaded(owned.Add(LoadSvg(path, maxWidth, maxHeight, vectorDpi)), 1, 1);
        }

        if (UsesWindowsCodec(format))
        {
            return new Loaded(Downscale(owned, owned.Add(WicDecoder.Decode(path, format)), maxWidth, maxHeight), 1, 1);
        }

        int sourceFrames;
        try
        {
            using Image header = Image.NewFromFile(path, access: Enums.Access.Sequential);
            sourceFrames = FrameCount(header);
        }
        catch (VipsException) when (format == "heic")
        {
            // libvips here has no HEVC decoder; Windows does when the HEIF/HEVC extensions are installed.
            return new Loaded(Downscale(owned, owned.Add(WicDecoder.Decode(path, format)), maxWidth, maxHeight), 1, 1);
        }

        bool animated = keepAnimation && sourceFrames > 1;

        // thumbnail = shrink-on-load + EXIF auto-rotation; with no limits it decodes at full size.
        Image image = Image.Thumbnail(
            animated ? path + "[n=-1]" : path,
            maxWidth > 0 ? maxWidth : NoLimit,
            height: maxHeight > 0 ? maxHeight : NoLimit,
            size: Enums.Size.Down);
        return new Loaded(owned.Add(image), sourceFrames, animated ? sourceFrames : 1);
    }

    private static Image LoadSvg(string path, int maxWidth, int maxHeight, double vectorDpi)
    {
        // libvips renders SVG user units 1:1 at scale 1, which is the CSS reference of 96 dpi.
        double scale = EffectiveVectorDpi(vectorDpi) / DefaultVectorDpi;
        if (maxWidth > 0 || maxHeight > 0)
        {
            // Vector input is re-rendered at the requested size, up or down, instead of being resampled.
            using Image natural = Image.Svgload(path);
            double byWidth = maxWidth > 0 ? maxWidth / (double)natural.Width : double.MaxValue;
            double byHeight = maxHeight > 0 ? maxHeight / (double)natural.Height : double.MaxValue;
            scale = Math.Min(byWidth, byHeight);
        }

        return Image.Svgload(path, scale: scale);
    }

    private static Image Downscale(ImageScope owned, Image image, int maxWidth, int maxHeight)
    {
        if ((maxWidth <= 0 || image.Width <= maxWidth) && (maxHeight <= 0 || image.Height <= maxHeight))
        {
            return image;
        }

        return owned.Add(image.ThumbnailImage(
            maxWidth > 0 ? maxWidth : NoLimit, height: maxHeight > 0 ? maxHeight : NoLimit, size: Enums.Size.Down));
    }

    /// <summary>Brings an image into a shape the target format can store: color space, depth, alpha.</summary>
    private static Image Prepare(ImageScope owned, Image image, string target, string background)
    {
        Enums.Interpretation interpretation = image.Interpretation;
        bool keepsHighDepth = target is "png" or "tiff";
        bool gray = interpretation is Enums.Interpretation.Bw or Enums.Interpretation.Grey16 && image.Bands <= 2;

        if (interpretation is not (Enums.Interpretation.Srgb or Enums.Interpretation.Bw
            or Enums.Interpretation.Rgb16 or Enums.Interpretation.Grey16))
        {
            image = owned.Add(image.Colourspace(Enums.Interpretation.Srgb));
            gray = false;
        }

        if (image.Format != Enums.BandFormat.Uchar && !(keepsHighDepth && image.Format == Enums.BandFormat.Ushort))
        {
            image = owned.Add(image.Colourspace(gray ? Enums.Interpretation.Bw : Enums.Interpretation.Srgb));
            if (image.Format != Enums.BandFormat.Uchar)
            {
                image = owned.Add(image.Cast(Enums.BandFormat.Uchar));
            }
        }

        if (image.HasAlpha() && target is "jpeg" or "bmp")
        {
            double[] rgb = ParseColor(background);
            double[] fill = image.Bands - 1 == 1 ? [(rgb[0] + rgb[1] + rgb[2]) / 3] : rgb;
            image = owned.Add(image.Flatten(background: fill));
        }

        if (target is "bmp" or "ico" or "gif" && image.Bands <= 2)
        {
            // These writers expect color channels.
            image = owned.Add(image.Colourspace(Enums.Interpretation.Srgb));
        }

        return image;
    }

    private static void Save(ImageScope owned, Image image, string target, ImageConvertRequest request)
    {
        Enums.ForeignKeep keep = request.KeepMetadata ? Enums.ForeignKeep.All : Enums.ForeignKeep.None;
        int quality = Math.Clamp(request.Quality <= 0 ? 85 : request.Quality, 1, 100);
        string output = request.OutputPath;
        switch (target)
        {
            case "png":
                image.Pngsave(output, compression: 6, keep: keep);
                break;
            case "jpeg":
                image.Jpegsave(output, q: quality, optimizeCoding: true, interlace: true, keep: keep);
                break;
            case "webp":
                image.Webpsave(output, q: quality, lossless: request.Lossless, effort: 4, keep: keep);
                break;
            case "avif":
                image.Heifsave(
                    output, q: quality, lossless: request.Lossless, compression: Enums.ForeignHeifCompression.Av1, effort: 4, keep: keep);
                break;
            case "tiff":
                image.Tiffsave(output, compression: Enums.ForeignTiffCompression.Lzw, keep: keep);
                break;
            case "gif":
                image.Gifsave(output, keep: keep);
                break;
            case "bmp":
                BmpWriter.Write(output, image.Bands == 4 ? owned.Add(image.ExtractBand(0, n: 3)) : image);
                break;
            default:
                SaveIco(owned, image, output);
                break;
        }
    }

    private static void SaveIco(ImageScope owned, Image image, string output)
    {
        // Every size is resampled from the same source, so decode it once into memory (it is at most 256 px).
        Image rgba = owned.Add((image.HasAlpha() ? image : owned.Add(image.BandjoinConst([255]))).CopyMemory());
        int largest = Math.Max(rgba.Width, rgba.Height);
        int[] sizes = [.. IconSizes.Where(s => s <= Math.Max(largest, IconSizes[0]))];

        var entries = new List<IcoWriter.Entry>(sizes.Length);
        foreach (int size in sizes)
        {
            using Image scaled = rgba.ThumbnailImage(size, height: size, size: Enums.Size.Both);
            using Image square = scaled.Gravity(
                Enums.CompassDirection.Centre, size, size, extend: Enums.Extend.Background, background: [0, 0, 0, 0]);
            entries.Add(new IcoWriter.Entry(size, square.PngsaveBuffer(compression: 9)));
        }

        IcoWriter.Write(output, entries);
    }

    private static int FrameCount(Image image) =>
        image.Contains("n-pages") ? Math.Max(1, (int)image.Get("n-pages")) : 1;

    private static int PageHeight(Image image) => image.PageHeight;

    private static double[] ParseColor(string? color)
    {
        string hex = (color ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
        {
            return [(value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF];
        }

        return [255, 255, 255];
    }

    private static ModitException InputError(VipsException ex, string path) =>
        ex.Message.Contains("not a known", StringComparison.OrdinalIgnoreCase)
            ? new ModitException(ErrorCode.ConvertUnsupportedFormat, $"{Path.GetFileName(path)} is not an image format Modit can read.", ex)
            : new ModitException(ErrorCode.ConvertInputCorrupt, $"{Path.GetFileName(path)} could not be read: {Describe(ex.Message)}", ex);

    private static string Describe(string message)
    {
        // libvips reports a chain such as "unable to call pngsave_buffer" followed by the real cause.
        string[] lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? message : string.Join(" ", lines);
    }

    private sealed record Loaded(Image Image, int SourceFrames, int Frames);

    /// <summary>Owns the intermediate images of one operation and releases them together.</summary>
    private sealed class ImageScope : IDisposable
    {
        private readonly List<Image> _images = [];

        public Image Add(Image image)
        {
            _images.Add(image);
            return image;
        }

        public void Dispose()
        {
            foreach (Image image in _images)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>libvips only checks for cancellation when a progress receiver is attached.</summary>
    private sealed class NoProgress : IProgress<int>
    {
        public static readonly NoProgress Instance = new();

        public void Report(int value)
        {
        }
    }
}
