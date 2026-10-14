using MessagePack;

namespace Modit.Engine;

[MessagePackObject]
public sealed record HostInfo(
    [property: Key(0)] int ProcessId,
    [property: Key(1)] string Architecture,
    [property: Key(2)] int OpenDocuments,
    [property: Key(3)] long WorkingSetBytes);

[MessagePackObject]
public sealed record OpenDocumentRequest(
    [property: Key(0)] Guid DocumentId,
    [property: Key(1)] string Path,
    [property: Key(2)] string? Password);

[MessagePackObject]
public sealed record PageInfo(
    [property: Key(0)] int Index,
    [property: Key(1)] float WidthPt,
    [property: Key(2)] float HeightPt,
    [property: Key(3)] string? Label);

[MessagePackObject]
public sealed record DocumentInfo(
    [property: Key(0)] Guid DocumentId,
    [property: Key(1)] int PageCount,
    [property: Key(2)] IReadOnlyList<PageInfo> Pages,
    [property: Key(3)] string PdfVersion,
    [property: Key(4)] bool IsEncrypted,
    [property: Key(5)] uint Permissions,
    [property: Key(6)] long FileSizeBytes,
    [property: Key(7)] IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// Renders the rectangle (<see cref="SourceXPx"/>, <see cref="SourceYPx"/>, <see cref="WidthPx"/>,
/// <see cref="HeightPx"/>) of the page as it would appear when the whole page is scaled by
/// <see cref="Scale"/> device pixels per PDF point. A full page is the special case origin 0,0.
/// </summary>
[MessagePackObject]
public sealed record RenderRequest(
    [property: Key(0)] Guid DocumentId,
    [property: Key(1)] int PageIndex,
    [property: Key(2)] double Scale,
    [property: Key(3)] int SourceXPx,
    [property: Key(4)] int SourceYPx,
    [property: Key(5)] int WidthPx,
    [property: Key(6)] int HeightPx,
    [property: Key(7)] string SharedBufferName,
    [property: Key(8)] bool RenderAnnotations);

[MessagePackObject]
public sealed record RenderResult(
    [property: Key(0)] int WidthPx,
    [property: Key(1)] int HeightPx,
    [property: Key(2)] int StrideBytes,
    [property: Key(3)] double ElapsedMilliseconds);

[MessagePackObject]
public sealed record QpdfJobResult(
    [property: Key(0)] int ExitCode,
    [property: Key(1)] IReadOnlyList<string> Warnings,
    [property: Key(2)] IReadOnlyList<string> Errors,
    [property: Key(3)] string Output);

[MessagePackObject]
public sealed record ImageInfoResult(
    [property: Key(0)] string Format,
    [property: Key(1)] int Width,
    [property: Key(2)] int Height,
    [property: Key(3)] int Bands,
    [property: Key(4)] bool HasAlpha,
    [property: Key(5)] int Frames,
    [property: Key(6)] double Dpi);

/// <summary>
/// Converts one image file. <see cref="OutputPath"/> is written as-is; the caller owns naming, collisions and
/// the temporary-file dance. Sizes are in pixels; 0 means "no limit". Images are only ever made smaller,
/// except vector input, which is rendered at whatever size fits the limits.
/// </summary>
[MessagePackObject]
public sealed record ImageConvertRequest(
    [property: Key(0)] string InputPath,
    [property: Key(1)] string OutputPath,
    [property: Key(2)] string TargetFormat,
    [property: Key(3)] int Quality,
    [property: Key(4)] bool Lossless,
    [property: Key(5)] int MaxWidthPx,
    [property: Key(6)] int MaxHeightPx,
    [property: Key(7)] bool KeepMetadata,
    [property: Key(8)] string Background,
    [property: Key(9)] double VectorDpi,
    [property: Key(10)] bool KeepAnimation);

[MessagePackObject]
public sealed record ImageConvertResult(
    [property: Key(0)] int Width,
    [property: Key(1)] int Height,
    [property: Key(2)] int Frames,
    [property: Key(3)] long OutputBytes,
    [property: Key(4)] IReadOnlyList<string> Warnings);

/// <summary>Builds a PDF with one image per page. <see cref="PageSize"/>: <c>image</c>, <c>a4</c> or <c>letter</c>.</summary>
[MessagePackObject]
public sealed record ImagesToPdfRequest(
    [property: Key(0)] IReadOnlyList<string> InputPaths,
    [property: Key(1)] string OutputPath,
    [property: Key(2)] string PageSize,
    [property: Key(3)] double MarginPt,
    [property: Key(4)] double VectorDpi);

[MessagePackObject]
public sealed record ImagesToPdfResult(
    [property: Key(0)] int PageCount,
    [property: Key(1)] long OutputBytes,
    [property: Key(2)] int PassthroughJpegs);

/// <summary>
/// Where a stamp sits on the page as the reader sees it (after page rotation):
/// <c>top-left</c>, <c>top-center</c>, <c>top-right</c>, <c>middle-left</c>, <c>center</c>, <c>middle-right</c>,
/// <c>bottom-left</c>, <c>bottom-center</c>, <c>bottom-right</c>.
/// </summary>
public static class StampPositions
{
    public static readonly IReadOnlyList<string> All =
    [
        "top-left", "top-center", "top-right",
        "middle-left", "center", "middle-right",
        "bottom-left", "bottom-center", "bottom-right",
    ];
}

/// <summary>
/// Text drawn on pages. <see cref="Text"/> may contain <c>{page}</c>, <c>{pages}</c>, <c>{name}</c> and
/// <c>{date}</c>. Page numbers are <c>page index + StartNumber</c>.
/// </summary>
[MessagePackObject]
public sealed record PdfTextStamp(
    [property: Key(0)] string Text,
    [property: Key(1)] double FontSizePt,
    [property: Key(2)] string Color,
    [property: Key(3)] double Opacity,
    [property: Key(4)] double RotationDegrees,
    [property: Key(5)] string Position,
    [property: Key(6)] double MarginPt,
    [property: Key(7)] int StartNumber,
    [property: Key(8)] string DocumentName,
    [property: Key(9)] bool Bold);

/// <summary>A picture (logo, signature, stamp) drawn on pages, <see cref="WidthPt"/> wide, aspect kept.</summary>
[MessagePackObject]
public sealed record PdfImageStamp(
    [property: Key(0)] string ImagePath,
    [property: Key(1)] double WidthPt,
    [property: Key(2)] string Position,
    [property: Key(3)] double MarginPt);

/// <summary>Trims the visible page area by these amounts, measured on the page as the reader sees it.</summary>
[MessagePackObject]
public sealed record PdfCrop(
    [property: Key(0)] double LeftPt,
    [property: Key(1)] double TopPt,
    [property: Key(2)] double RightPt,
    [property: Key(3)] double BottomPt);

/// <summary>
/// Changes page content with PDFium and writes the whole document to <see cref="OutputPath"/>.
/// <see cref="PageIndexes"/> null means every page. Flattening runs first, then crop, then stamps.
/// </summary>
[MessagePackObject]
public sealed record PdfEditRequest(
    [property: Key(0)] string InputPath,
    [property: Key(1)] string? Password,
    [property: Key(2)] string OutputPath,
    [property: Key(3)] IReadOnlyList<int>? PageIndexes,
    [property: Key(4)] PdfTextStamp? Text,
    [property: Key(5)] PdfImageStamp? Image,
    [property: Key(6)] PdfCrop? Crop,
    [property: Key(7)] bool Flatten);

[MessagePackObject]
public sealed record PdfEditResult(
    [property: Key(0)] int PageCount,
    [property: Key(1)] int PagesChanged,
    [property: Key(2)] bool SecurityRemoved,
    [property: Key(3)] IReadOnlyList<string> Warnings);
