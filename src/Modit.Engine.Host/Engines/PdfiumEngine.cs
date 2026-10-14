using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Modit.Native.Pdfium;
using static Modit.Native.Pdfium.NativeMethods;

namespace Modit.Engine.Host.Engines;

/// <summary>
/// Owns the PDFium library and all open documents of this host. Public methods marshal onto the
/// PDFium thread; private methods assume they already run there.
/// </summary>
internal sealed unsafe partial class PdfiumEngine : IDisposable
{
    /// <summary>Largest edge of a single render request, in device pixels.</summary>
    public const int MaxRenderEdgePx = 16384;

    private static readonly string[] MetadataTags =
        ["Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate"];

    private readonly PdfiumThread _thread;
    private readonly Dictionary<Guid, OpenPdf> _documents = [];

    public PdfiumEngine(PdfiumThread thread)
    {
        _thread = thread;
        _thread.RunAsync(() =>
        {
            var config = new FPDF_LIBRARY_CONFIG { Version = 2 };
            FPDF_InitLibraryWithConfig(&config);
        }).GetAwaiter().GetResult();
    }

    public int OpenDocumentCount => _documents.Count;

    public Task<DocumentInfo> OpenAsync(OpenDocumentRequest request, CancellationToken cancellationToken) =>
        _thread.RunAsync(() => Open(request), cancellationToken);

    public Task CloseAsync(Guid documentId, CancellationToken cancellationToken) =>
        _thread.RunAsync(
            () =>
            {
                if (_documents.Remove(documentId, out OpenPdf? pdf))
                {
                    pdf.Dispose();
                }
            },
            cancellationToken);

    public Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken) =>
        _thread.RunAsync(() => Render(request), cancellationToken);

    public Task<string> GetPageTextAsync(Guid documentId, int pageIndex, CancellationToken cancellationToken) =>
        _thread.RunAsync(() => GetPageText(documentId, pageIndex), cancellationToken);

    public void Dispose()
    {
        _thread.RunAsync(() =>
        {
            foreach (OpenPdf pdf in _documents.Values)
            {
                pdf.Dispose();
            }

            _documents.Clear();
            FPDF_DestroyLibrary();
        }).GetAwaiter().GetResult();
    }

    private DocumentInfo Open(OpenDocumentRequest request)
    {
        if (_documents.TryGetValue(request.DocumentId, out OpenPdf? existing))
        {
            return existing.Info;
        }

        var file = new FileInfo(request.Path);
        if (!file.Exists)
        {
            throw new ModitException(ErrorCode.FileNotFound, $"File not found: {request.Path}");
        }

        if (file.Length == 0)
        {
            throw new ModitException(ErrorCode.PdfCorrupt, "The file is empty.");
        }

        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        byte* pointer = null;
        try
        {
            try
            {
                // Mapping (instead of reading) keeps a 500 MB scan out of the managed heap.
                var stream = new FileStream(
                    request.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                map = MemoryMappedFile.CreateFromFile(
                    stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ModitException(ErrorCode.FileAccessDenied, $"Access denied: {request.Path}", ex);
            }
            catch (IOException ex)
            {
                throw new ModitException(ErrorCode.FileAccessDenied, $"Cannot open: {request.Path}", ex);
            }

            view = map.CreateViewAccessor(0, file.Length, MemoryMappedFileAccess.Read);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            pointer += view.PointerOffset;

            IntPtr document;
            byte[]? password = request.Password is null ? null : Encoding.UTF8.GetBytes(request.Password + '\0');
            fixed (byte* passwordPtr = password)
            {
                document = FPDF_LoadMemDocument64(pointer, (nuint)file.Length, passwordPtr);
            }

            if (document == IntPtr.Zero)
            {
                throw MapLoadError(FPDF_GetLastError(), request.Password is not null);
            }

            DocumentInfo info = ReadInfo(request.DocumentId, document, file.Length);
            _documents[request.DocumentId] = new OpenPdf(document, map, view, info);
            map = null;
            view = null;
            return info;
        }
        finally
        {
            if (view is not null)
            {
                if (pointer != null)
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                }

                view.Dispose();
            }

            map?.Dispose();
        }
    }

    private static ModitException MapLoadError(uint error, bool passwordSupplied) => error switch
    {
        PdfiumConstants.ErrPassword when passwordSupplied =>
            new ModitException(ErrorCode.PdfWrongPassword, "The password is incorrect."),
        PdfiumConstants.ErrPassword =>
            new ModitException(ErrorCode.PdfEncryptedNeedsPassword, "This document requires a password."),
        PdfiumConstants.ErrSecurity =>
            new ModitException(ErrorCode.PdfUnsupportedSecurity, "The document uses an unsupported security scheme."),
        PdfiumConstants.ErrFile =>
            new ModitException(ErrorCode.FileNotFound, "The file could not be opened."),
        _ => new ModitException(ErrorCode.PdfCorrupt, "The file is not a PDF or is damaged."),
    };

    private static DocumentInfo ReadInfo(Guid id, IntPtr document, long fileSize)
    {
        int pageCount = FPDF_GetPageCount(document);
        var pages = new PageInfo[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            _ = FPDF_GetPageSizeByIndexF(document, i, out FS_SIZEF size);
            pages[i] = new PageInfo(i, size.Width, size.Height, ReadPageLabel(document, i));
        }

        string version = FPDF_GetFileVersion(document, out int v) != 0 ? $"{v / 10}.{v % 10}" : "unknown";

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string tag in MetadataTags)
        {
            string value = ReadMetaText(document, tag);
            if (value.Length > 0)
            {
                metadata[tag] = value;
            }
        }

        return new DocumentInfo(
            id,
            pageCount,
            pages,
            version,
            IsEncrypted: FPDF_GetSecurityHandlerRevision(document) != -1,
            Permissions: FPDF_GetDocPermissions(document),
            fileSize,
            metadata);
    }

    private static string ReadMetaText(IntPtr document, string tag)
    {
        byte[] tagBytes = Encoding.ASCII.GetBytes(tag + '\0');
        fixed (byte* tagPtr = tagBytes)
        {
            uint bytes = FPDF_GetMetaText(document, tagPtr, null, 0);
            if (bytes <= 2)
            {
                return string.Empty;
            }

            byte[] buffer = new byte[bytes];
            fixed (byte* bufferPtr = buffer)
            {
                _ = FPDF_GetMetaText(document, tagPtr, bufferPtr, bytes);
            }

            return Encoding.Unicode.GetString(buffer, 0, (int)bytes - 2);
        }
    }

    private static string? ReadPageLabel(IntPtr document, int pageIndex)
    {
        uint bytes = FPDF_GetPageLabel(document, pageIndex, null, 0);
        if (bytes <= 2)
        {
            return null;
        }

        byte[] buffer = new byte[bytes];
        fixed (byte* bufferPtr = buffer)
        {
            _ = FPDF_GetPageLabel(document, pageIndex, bufferPtr, bytes);
        }

        return Encoding.Unicode.GetString(buffer, 0, (int)bytes - 2);
    }

    private RenderResult Render(RenderRequest request)
    {
        OpenPdf pdf = Get(request.DocumentId);
        ValidatePage(pdf, request.PageIndex);

        if (request.WidthPx <= 0 || request.HeightPx <= 0 ||
            request.WidthPx > MaxRenderEdgePx || request.HeightPx > MaxRenderEdgePx ||
            !(request.Scale > 0) || double.IsInfinity(request.Scale))
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Invalid render size or scale.");
        }

        long timestamp = Stopwatch.GetTimestamp();
        int stride = request.WidthPx * 4;
        long required = (long)stride * request.HeightPx;

        using SharedBuffer buffer = SharedBuffer.Open(request.SharedBufferName, required);
        IntPtr page = FPDF_LoadPage(pdf.Handle, request.PageIndex);
        if (page == IntPtr.Zero)
        {
            throw new ModitException(ErrorCode.PdfCorrupt, $"Page {request.PageIndex + 1} could not be loaded.");
        }

        try
        {
            IntPtr bitmap = FPDFBitmap_CreateEx(
                request.WidthPx, request.HeightPx, PdfiumConstants.BitmapBgra, buffer.Data, stride);
            if (bitmap == IntPtr.Zero)
            {
                throw new ModitException(ErrorCode.EngineMemoryLimit, "The render bitmap could not be created.");
            }

            try
            {
                _ = FPDFBitmap_FillRect(bitmap, 0, 0, request.WidthPx, request.HeightPx, 0xFFFFFFFF);

                // PDFium applies this matrix to the page laid out at 1 px per point with a top-left origin.
                float scale = (float)request.Scale;
                var matrix = new FS_MATRIX
                {
                    A = scale,
                    D = scale,
                    E = -request.SourceXPx,
                    F = -request.SourceYPx,
                };
                var clip = new FS_RECTF { Left = 0, Top = 0, Right = request.WidthPx, Bottom = request.HeightPx };
                int flags = request.RenderAnnotations ? PdfiumConstants.RenderAnnotations : 0;
                FPDF_RenderPageBitmapWithMatrix(bitmap, page, &matrix, &clip, flags);
            }
            finally
            {
                FPDFBitmap_Destroy(bitmap);
            }
        }
        finally
        {
            FPDF_ClosePage(page);
        }

        return new RenderResult(
            request.WidthPx, request.HeightPx, stride, Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
    }

    private string GetPageText(Guid documentId, int pageIndex)
    {
        OpenPdf pdf = Get(documentId);
        ValidatePage(pdf, pageIndex);

        IntPtr page = FPDF_LoadPage(pdf.Handle, pageIndex);
        if (page == IntPtr.Zero)
        {
            throw new ModitException(ErrorCode.PdfCorrupt, $"Page {pageIndex + 1} could not be loaded.");
        }

        try
        {
            IntPtr textPage = FPDFText_LoadPage(page);
            if (textPage == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                int count = FPDFText_CountChars(textPage);
                if (count <= 0)
                {
                    return string.Empty;
                }

                ushort[] buffer = new ushort[count + 1];
                int written;
                fixed (ushort* bufferPtr = buffer)
                {
                    written = FPDFText_GetText(textPage, 0, count, bufferPtr);
                }

                // "written" includes the terminating NUL.
                int length = Math.Max(0, written - 1);
                return new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, length)));
            }
            finally
            {
                FPDFText_ClosePage(textPage);
            }
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    private OpenPdf Get(Guid documentId) =>
        _documents.TryGetValue(documentId, out OpenPdf? pdf)
            ? pdf
            : throw new ModitException(ErrorCode.InvalidArgument, "The document is not open in this host.");

    private static void ValidatePage(OpenPdf pdf, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= pdf.Info.PageCount)
        {
            throw new ModitException(
                ErrorCode.PdfPageOutOfRange,
                $"Page {pageIndex + 1} is out of range (1-{pdf.Info.PageCount}).");
        }
    }

    private sealed class OpenPdf(
        IntPtr handle, MemoryMappedFile map, MemoryMappedViewAccessor view, DocumentInfo info) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public DocumentInfo Info { get; } = info;

        public void Dispose()
        {
            // The mapping must outlive the PDFium document that reads from it.
            FPDF_CloseDocument(Handle);
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            view.Dispose();
            map.Dispose();
        }
    }
}
