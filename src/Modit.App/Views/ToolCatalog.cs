using Modit.App.ViewModels;

namespace Modit.App.Views;

/// <summary>The tools shown on Home, in display order, with their Segoe Fluent Icons glyph.</summary>
internal static class ToolCatalog
{
    public static IReadOnlyList<ToolKind> PdfTools { get; } =
    [
        ToolKind.Merge,
        ToolKind.Split,
        ToolKind.Extract,
        ToolKind.Rotate,
        ToolKind.Optimize,
        ToolKind.Protect,
        ToolKind.Unlock,
    ];

    public static IReadOnlyList<ToolKind> ConvertTools { get; } =
    [
        ToolKind.ConvertImages,
        ToolKind.ImagesToPdf,
        ToolKind.Images,
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
        _ => "\uE785",
    };
}
