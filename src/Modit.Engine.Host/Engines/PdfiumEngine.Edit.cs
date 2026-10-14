using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Modit.Native.Pdfium;
using static Modit.Native.Pdfium.NativeMethods;

namespace Modit.Engine.Host.Engines;

/// <summary>
/// Page editing with PDFium: text stamps (watermarks, page numbers, headers), image stamps (logos,
/// signatures), crop and flatten. All placement is done in "visual" coordinates, the page as the reader
/// sees it after /Rotate, so a stamp at the bottom of a rotated page is upright and at the bottom.
/// </summary>
internal sealed unsafe partial class PdfiumEngine
{
    /// <summary>Longest edge of a stamped picture. Every page gets its own copy, so it is kept modest.</summary>
    private const int MaxStampImageEdgePx = 1200;

    private const double MinVisiblePt = 10;

    public Task<PdfEditResult> EditAsync(PdfEditRequest request, CancellationToken cancellationToken)
    {
        ValidateEdit(request);
        if (request.Image is not { } image)
        {
            return _thread.RunAsync(() => Edit(request, null, cancellationToken), cancellationToken);
        }

        // Decoding the picture uses libvips and must not hold up the PDFium thread. A decode failure is
        // rethrown by GetResult inside the continuation, so it reaches the caller unchanged.
        return Task.Run(() => DecodeStamp(image.ImagePath), cancellationToken)
            .ContinueWith(
                decoded => _thread.RunAsync(() => Edit(request, decoded.GetAwaiter().GetResult(), cancellationToken), cancellationToken),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default)
            .Unwrap();
    }

    private static void ValidateEdit(PdfEditRequest request)
    {
        if (request.Text is null && request.Image is null && request.Crop is null && !request.Flatten)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Nothing to change: choose a stamp, a crop or flatten.");
        }

        if (request.Text is { } text)
        {
            if (string.IsNullOrWhiteSpace(text.Text))
            {
                throw new ModitException(ErrorCode.InvalidArgument, "Type the text to add.");
            }

            if (text.FontSizePt is < 4 or > 500 || text.Opacity is < 0 or > 1 || !StampPositions.All.Contains(text.Position))
            {
                throw new ModitException(ErrorCode.InvalidArgument, "Font size must be 4-500 pt, opacity 0-1, and the position one of the nine named spots.");
            }
        }

        if (request.Image is { } image && (image.WidthPt is < 1 or > 5000 || !StampPositions.All.Contains(image.Position)))
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Image width must be 1-5000 pt and the position one of the nine named spots.");
        }

        if (request.Crop is { } crop && (crop.LeftPt < 0 || crop.TopPt < 0 || crop.RightPt < 0 || crop.BottomPt < 0))
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Crop amounts cannot be negative.");
        }
    }

    private static StampBitmap DecodeStamp(string path)
    {
        (byte[] bgra, int width, int height) = ImageEngine.DecodeBgra(path, MaxStampImageEdgePx);
        return new StampBitmap(bgra, width, height);
    }

    private PdfEditResult Edit(PdfEditRequest request, StampBitmap? bitmap, CancellationToken cancellationToken)
    {
        byte[] bytes = ReadSource(request.InputPath);
        IntPtr document = IntPtr.Zero;
        IntPtr font = IntPtr.Zero;
        fixed (byte* data = bytes)
        {
            try
            {
                byte[]? password = request.Password is null ? null : Encoding.UTF8.GetBytes(request.Password + '\0');
                fixed (byte* passwordPtr = password)
                {
                    document = FPDF_LoadMemDocument64(data, (nuint)bytes.Length, passwordPtr);
                }

                if (document == IntPtr.Zero)
                {
                    throw MapLoadError(FPDF_GetLastError(), request.Password is not null);
                }

                int pageCount = FPDF_GetPageCount(document);
                bool encrypted = FPDF_GetSecurityHandlerRevision(document) != -1;
                IReadOnlyList<int> pages = request.PageIndexes ?? [.. Enumerable.Range(0, pageCount)];
                if (pages.Any(p => p < 0 || p >= pageCount))
                {
                    throw new ModitException(ErrorCode.PdfPageOutOfRange, $"The document has {pageCount} pages.");
                }

                bool embeddedFont = false;
                if (request.Text is { } text)
                {
                    font = LoadStampFont(document, text, out embeddedFont);
                }

                var warnings = new List<string>();
                int changed = 0;
                foreach (int index in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (EditPage(document, index, pageCount, request, font, bitmap, warnings))
                    {
                        changed++;
                    }
                }

                uint flags = PdfiumConstants.SaveNoIncremental;
                if (encrypted)
                {
                    flags |= PdfiumConstants.SaveRemoveSecurity;
                    warnings.Add("The source was password protected. The edited copy has no password; use Protect to add one.");
                }

                if (embeddedFont)
                {
                    flags |= PdfiumConstants.SaveSubsetNewFonts;
                }

                Save(document, request.OutputPath, flags);
                return new PdfEditResult(pageCount, changed, encrypted, warnings);
            }
            finally
            {
                if (font != IntPtr.Zero)
                {
                    FPDFFont_Close(font);
                }

                if (document != IntPtr.Zero)
                {
                    FPDF_CloseDocument(document);
                }
            }
        }
    }

    private static byte[] ReadSource(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ModitException(ErrorCode.FileNotFound, $"File not found: {path}", ex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new ModitException(ErrorCode.FileAccessDenied, $"Cannot open: {path}", ex);
        }
    }

    private static bool EditPage(
        IntPtr document, int index, int pageCount, PdfEditRequest request, IntPtr font, StampBitmap? bitmap, List<string> warnings)
    {
        IntPtr page = LoadPageOrThrow(document, index);
        bool changed = false;
        try
        {
            if (request.Flatten)
            {
                int result = FPDFPage_Flatten(page, PdfiumConstants.FlattenForPrint);
                if (result == PdfiumConstants.FlattenFail)
                {
                    warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Page {index + 1} could not be flattened."));
                }
                else if (result == 1)
                {
                    // Flattening rewrites the page; reload it before touching it again.
                    changed = true;
                    FPDF_ClosePage(page);
                    page = LoadPageOrThrow(document, index);
                }
            }

            if (request.Crop is { } crop)
            {
                ApplyCrop(page, crop, index);
                changed = true;
            }

            bool inserted = false;
            if (request.Text is { } text)
            {
                inserted |= AddText(document, page, font, text, index, pageCount);
            }

            if (request.Image is { } image && bitmap is not null)
            {
                AddImage(document, page, image, bitmap);
                inserted = true;
            }

            if (inserted && FPDFPage_GenerateContent(page) == 0)
            {
                throw new ModitException(ErrorCode.PdfStructureOperationFailed, $"Page {index + 1} could not be updated.");
            }

            return changed || inserted;
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    private static IntPtr LoadPageOrThrow(IntPtr document, int index)
    {
        IntPtr page = FPDF_LoadPage(document, index);
        return page != IntPtr.Zero
            ? page
            : throw new ModitException(ErrorCode.PdfCorrupt, $"Page {index + 1} could not be read.");
    }

    private static void ApplyCrop(IntPtr page, PdfCrop crop, int index)
    {
        PageFrame frame = PageFrame.Of(page);
        double left = crop.LeftPt;
        double bottom = crop.BottomPt;
        double right = frame.Width - crop.RightPt;
        double top = frame.Height - crop.TopPt;
        if (right - left < MinVisiblePt || top - bottom < MinVisiblePt)
        {
            throw new ModitException(
                ErrorCode.InvalidArgument, string.Create(CultureInfo.InvariantCulture, $"The crop would remove all of page {index + 1}."));
        }

        (double x1, double y1) = frame.ToUser.Apply(left, bottom);
        (double x2, double y2) = frame.ToUser.Apply(right, top);
        FPDFPage_SetCropBox(
            page, (float)Math.Min(x1, x2), (float)Math.Min(y1, y2), (float)Math.Max(x1, x2), (float)Math.Max(y1, y2));
    }

    private static bool AddText(IntPtr document, IntPtr page, IntPtr font, PdfTextStamp stamp, int index, int pageCount)
    {
        string text = ExpandTokens(stamp, index, pageCount);
        if (text.Length == 0)
        {
            return false;
        }

        IntPtr textObject = FPDFPageObj_CreateTextObj(document, font, (float)stamp.FontSizePt);
        if (textObject == IntPtr.Zero)
        {
            throw new ModitException(ErrorCode.PdfStructureOperationFailed, "The text could not be created.");
        }

        fixed (char* chars = text + "\0")
        {
            if (FPDFText_SetText(textObject, (ushort*)chars) == 0)
            {
                FPDFPageObj_Destroy(textObject);
                throw new ModitException(ErrorCode.PdfStructureOperationFailed, "The text could not be set.");
            }
        }

        (byte r, byte g, byte b) = ParseColor(stamp.Color);
        _ = FPDFPageObj_SetFillColor(textObject, r, g, b, (uint)Math.Round(Math.Clamp(stamp.Opacity, 0, 1) * 255));
        FPDFPageObj_GetBounds(textObject, out float left, out float bottom, out float right, out float top);

        PageFrame frame = PageFrame.Of(page);
        Matrix place = Place(frame, left, bottom, right, top, stamp.RotationDegrees, stamp.Position, stamp.MarginPt);
        Matrix final = place.Then(frame.ToUser);
        FPDFPageObj_Transform(textObject, final.A, final.B, final.C, final.D, final.E, final.F);
        FPDFPage_InsertObject(page, textObject);
        return true;
    }

    private static void AddImage(IntPtr document, IntPtr page, PdfImageStamp stamp, StampBitmap bitmap)
    {
        IntPtr imageObject = FPDFPageObj_NewImageObj(document);
        if (imageObject == IntPtr.Zero)
        {
            throw new ModitException(ErrorCode.PdfStructureOperationFailed, "The picture could not be created.");
        }

        fixed (byte* pixels = bitmap.Bgra)
        {
            IntPtr pdfBitmap = FPDFBitmap_CreateEx(bitmap.Width, bitmap.Height, PdfiumConstants.BitmapBgra, pixels, bitmap.Width * 4);
            try
            {
                IntPtr pageHandle = page;
                if (FPDFImageObj_SetBitmap(&pageHandle, 1, imageObject, pdfBitmap) == 0)
                {
                    FPDFPageObj_Destroy(imageObject);
                    throw new ModitException(ErrorCode.PdfStructureOperationFailed, "The picture could not be embedded.");
                }
            }
            finally
            {
                FPDFBitmap_Destroy(pdfBitmap);
            }
        }

        // An image object draws its pixels into the unit square; scale it to size, then place it.
        double width = stamp.WidthPt;
        double height = width * bitmap.Height / bitmap.Width;
        PageFrame frame = PageFrame.Of(page);
        Matrix final = Matrix.Scale(width, height)
            .Then(Place(frame, 0, 0, width, height, 0, stamp.Position, stamp.MarginPt))
            .Then(frame.ToUser);
        if (FPDFImageObj_SetMatrix(imageObject, final.A, final.B, final.C, final.D, final.E, final.F) == 0)
        {
            FPDFPageObj_Destroy(imageObject);
            throw new ModitException(ErrorCode.PdfStructureOperationFailed, "The picture could not be placed.");
        }

        FPDFPage_InsertObject(page, imageObject);
    }

    /// <summary>
    /// Moves an object whose untransformed bounds are (left, bottom, right, top) so that, rotated by
    /// <paramref name="degrees"/> about its center, it sits at <paramref name="position"/> on the visual page.
    /// </summary>
    private static Matrix Place(
        PageFrame frame, double left, double bottom, double right, double top, double degrees, string position, double margin)
    {
        double width = right - left;
        double height = top - bottom;
        double radians = degrees * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double turnedWidth = Math.Abs(width * cos) + Math.Abs(height * sin);
        double turnedHeight = Math.Abs(width * sin) + Math.Abs(height * cos);

        string[] parts = position == "center" ? ["middle", "center"] : position.Split('-');
        double centerX = parts[1] switch
        {
            "left" => margin + (turnedWidth / 2),
            "right" => frame.Width - margin - (turnedWidth / 2),
            _ => frame.Width / 2,
        };
        double centerY = parts[0] switch
        {
            "top" => frame.Height - margin - (turnedHeight / 2),
            "bottom" => margin + (turnedHeight / 2),
            _ => frame.Height / 2,
        };

        return Matrix.Translate(-(left + right) / 2, -(bottom + top) / 2)
            .Then(Matrix.Rotate(radians))
            .Then(Matrix.Translate(centerX, centerY));
    }

    private static string ExpandTokens(PdfTextStamp stamp, int index, int pageCount)
    {
        int first = stamp.StartNumber;
        return stamp.Text
            .Replace("{page}", (index + first).ToString(CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{pages}", (pageCount + first - 1).ToString(CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", stamp.DocumentName, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTime.Now.ToString("d", CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    /// <summary>
    /// ASCII text uses the built-in Helvetica (nothing embedded). Anything else embeds Arial from Windows,
    /// subset to the characters used, so accents, Greek, Cyrillic and similar scripts render correctly.
    /// </summary>
    private static IntPtr LoadStampFont(IntPtr document, PdfTextStamp stamp, out bool embedded)
    {
        string sample = stamp.Text + stamp.DocumentName + DateTime.Now.ToString("d", CultureInfo.CurrentCulture);
        if (sample.All(c => c < 0x80))
        {
            embedded = false;
            byte[] name = Encoding.ASCII.GetBytes((stamp.Bold ? "Helvetica-Bold" : "Helvetica") + "\0");
            fixed (byte* namePtr = name)
            {
                return FPDFText_LoadStandardFont(document, namePtr);
            }
        }

        string fontPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts), stamp.Bold ? "arialbd.ttf" : "arial.ttf");
        if (!File.Exists(fontPath))
        {
            throw new ModitException(ErrorCode.PdfStructureOperationFailed, $"The font {fontPath} is missing, so this text cannot be added.");
        }

        byte[] fontData = File.ReadAllBytes(fontPath);
        fixed (byte* fontPtr = fontData)
        {
            IntPtr font = FPDFText_LoadFont(document, fontPtr, (uint)fontData.Length, PdfiumConstants.FontTrueType, cid: 1);
            embedded = true;
            return font != IntPtr.Zero
                ? font
                : throw new ModitException(ErrorCode.PdfStructureOperationFailed, "The font could not be loaded.");
        }
    }

    private static (byte R, byte G, byte B) ParseColor(string color)
    {
        string hex = color.Trim().TrimStart('#');
        return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)
            ? ((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : ((byte)128, (byte)128, (byte)128);
    }

    private static void Save(IntPtr document, string outputPath, uint flags)
    {
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        GCHandle handle = GCHandle.Alloc(stream);
        try
        {
            var context = new WriteContext
            {
                Base = new FPDF_FILEWRITE { Version = 1, WriteBlock = &WriteBlock },
                Stream = GCHandle.ToIntPtr(handle),
            };
            if (FPDF_SaveAsCopy(document, &context.Base, flags) == 0)
            {
                throw new ModitException(ErrorCode.OutputWriteFailed, $"The edited document could not be written to {outputPath}.");
            }

            stream.Flush(flushToDisk: true);
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteBlock(FPDF_FILEWRITE* self, void* data, uint size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(((WriteContext*)self)->Stream).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            return 1;
        }
#pragma warning disable CA1031 // An exception must never cross into native code; returning 0 makes PDFium fail the save.
        catch (Exception)
#pragma warning restore CA1031
        {
            return 0;
        }
    }

    /// <summary>The FPDF_FILEWRITE PDFium sees, followed by our own context. PDFium passes the struct back.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WriteContext
    {
        public FPDF_FILEWRITE Base;
        public IntPtr Stream;
    }

    private sealed record StampBitmap(byte[] Bgra, int Width, int Height);

    /// <summary>
    /// The visible box of a page as the reader sees it, and the matrix from that visual space (origin at the
    /// visible bottom-left, in points) to PDF user space.
    /// </summary>
    private readonly record struct PageFrame(double Width, double Height, Matrix ToUser)
    {
        public static PageFrame Of(IntPtr page)
        {
            if (FPDFPage_GetCropBox(page, out float l, out float b, out float r, out float t) == 0 &&
                FPDFPage_GetMediaBox(page, out l, out b, out r, out t) == 0)
            {
                (l, b, r, t) = (0, 0, FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page));
            }

            double left = Math.Min(l, r);
            double bottom = Math.Min(b, t);
            double width = Math.Abs(r - l);
            double height = Math.Abs(t - b);

            // /Rotate turns the page clockwise for display.
            return (FPDFPage_GetRotation(page) & 3) switch
            {
                1 => new PageFrame(height, width, new Matrix(0, 1, -1, 0, left + width, bottom)),
                2 => new PageFrame(width, height, new Matrix(-1, 0, 0, -1, left + width, bottom + height)),
                3 => new PageFrame(height, width, new Matrix(0, -1, 1, 0, left, bottom + height)),
                _ => new PageFrame(width, height, new Matrix(1, 0, 0, 1, left, bottom)),
            };
        }
    }

    /// <summary>A PDF matrix [a b c d e f]: (x, y) maps to (a x + c y + e, b x + d y + f).</summary>
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static Matrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

        public static Matrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

        public static Matrix Rotate(double radians) =>
            new(Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), 0, 0);

        /// <summary>This transform followed by <paramref name="next"/>.</summary>
        public Matrix Then(Matrix next) => new(
            (A * next.A) + (B * next.C),
            (A * next.B) + (B * next.D),
            (C * next.A) + (D * next.C),
            (C * next.B) + (D * next.D),
            (E * next.A) + (F * next.C) + next.E,
            (E * next.B) + (F * next.D) + next.F);

        public (double X, double Y) Apply(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);
    }
}
