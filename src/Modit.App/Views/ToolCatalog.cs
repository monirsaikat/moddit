using Modit.App.ViewModels;

namespace Modit.App.Views;

/// <summary>The tools shown on Home, in display order, with their Segoe Fluent Icons glyph.</summary>
internal static class ToolCatalog
{
    public static IReadOnlyList<ToolKind> EditTools { get; } =
    [
        ToolKind.Organize,
        ToolKind.Watermark,
        ToolKind.PageNumbers,
        ToolKind.AddImage,
        ToolKind.Crop,
        ToolKind.RemovePages,
        ToolKind.Rotate,
    ];

    public static IReadOnlyList<ToolKind> PdfTools { get; } =
    [
        ToolKind.Merge,
        ToolKind.Split,
        ToolKind.Extract,
        ToolKind.Optimize,
        ToolKind.Protect,
        ToolKind.Unlock,
        ToolKind.Flatten,
    ];

    public static IReadOnlyList<ToolKind> ConvertTools { get; } =
    [
        ToolKind.ConvertImages,
        ToolKind.ImagesToPdf,
        ToolKind.Images,
        ToolKind.ToText,
    ];

    /// <summary>Tools offered for an open document, in the viewer's Tools menu.</summary>
    public static IReadOnlyList<ToolKind> DocumentTools { get; } =
    [
        .. EditTools,
        .. PdfTools.Where(k => k != ToolKind.Merge),
        ToolKind.Images,
        ToolKind.ToText,
    ];

    public static string Glyph(ToolKind kind) => kind switch
    {
        ToolKind.Merge => "\uE8F4",
        ToolKind.Split => "\uE8C6",
        ToolKind.Extract => "\uE8B3",
        ToolKind.Rotate => "\uE7AD",
        ToolKind.Optimize => "\uE73F",
        ToolKind.Images => "\uEB9F",
        ToolKind.Protect => "\uE72E",
        ToolKind.ConvertImages => "\uE8AB",
        ToolKind.ImagesToPdf => "\uE8A5",
        ToolKind.Organize => "\uE8A9",
        ToolKind.Watermark => "\uE8D2",
        ToolKind.PageNumbers => "\uE8FD",
        ToolKind.AddImage => "\uEE56",
        ToolKind.Crop => "\uE7A8",
        ToolKind.Flatten => "\uF5ED",
        ToolKind.RemovePages => "\uE74D",
        ToolKind.ToText => "\uEA37",
        _ => "\uE785",
    };
}
