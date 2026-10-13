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
