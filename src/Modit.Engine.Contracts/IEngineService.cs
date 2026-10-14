namespace Modit.Engine;

/// <summary>
/// RPC surface of <c>Modit.Engine.Host.exe</c>. Every call is idempotent so the client can re-issue it
/// after a host crash and respawn: documents are addressed by a client-chosen <see cref="Guid"/>, and
/// opening the same id twice returns the already open document.
/// Pixel data never travels through this channel; it goes through a named shared-memory buffer.
/// </summary>
public interface IEngineService
{
    Task<HostInfo> PingAsync(CancellationToken cancellationToken);

    Task<DocumentInfo> OpenDocumentAsync(OpenDocumentRequest request, CancellationToken cancellationToken);

    Task CloseDocumentAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>Renders a region of a page as BGRA into the shared buffer named in the request.</summary>
    Task<RenderResult> RenderPageAsync(RenderRequest request, CancellationToken cancellationToken);

    Task<string> GetPageTextAsync(Guid documentId, int pageIndex, CancellationToken cancellationToken);

    /// <summary>Runs a QPDF job described by QPDF job JSON (merge, split, encrypt, linearize, check).</summary>
    Task<QpdfJobResult> RunQpdfJobAsync(string jobJson, CancellationToken cancellationToken);

    /// <summary>Reads the header of an image file (any format the converters accept).</summary>
    Task<ImageInfoResult> GetImageInfoAsync(string path, CancellationToken cancellationToken);

    /// <summary>Converts one image with libvips (and the Windows codecs for BMP, ICO and HEIC).</summary>
    Task<ImageConvertResult> ConvertImageAsync(ImageConvertRequest request, CancellationToken cancellationToken);

    /// <summary>Writes a PDF with one image per page. JPEG files are embedded unchanged.</summary>
    Task<ImagesToPdfResult> ImagesToPdfAsync(ImagesToPdfRequest request, CancellationToken cancellationToken);

    /// <summary>Stamps, crops or flattens pages with PDFium and saves a full copy.</summary>
    Task<PdfEditResult> EditPdfAsync(PdfEditRequest request, CancellationToken cancellationToken);

    /// <summary>Test hook: terminates the host abruptly to exercise crash recovery.</summary>
    Task CrashAsync(CancellationToken cancellationToken);
}

/// <summary>Numeric error data attached to RPC failures so the client can rebuild a ModitException.</summary>
public static class EngineRpc
{
    /// <summary>JSON-RPC error codes at or above this value carry <c>code - Base</c> as an <see cref="ErrorCode"/>.</summary>
    public const int ErrorCodeBase = 20000;
}
