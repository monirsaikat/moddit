using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Modit.Engine;
using Modit.Engine.Client;

namespace Modit.App.ViewModels;

/// <summary>One page of an open document as shown in the page list.</summary>
public sealed class PageItemViewModel(int index, float widthPt, float heightPt, string? label)
{
    public int Index { get; } = index;

    public int Number => Index + 1;

    public float WidthPt { get; } = widthPt;

    public float HeightPt { get; } = heightPt;

    public string DisplayLabel { get; } = label ?? (index + 1).ToString(CultureInfo.CurrentCulture);
}

/// <summary>A PDF open in a tab. Owns the engine-side document for the lifetime of the tab.</summary>
public sealed partial class DocumentViewModel : ObservableObject, IAsyncDisposable
{
    private readonly EngineClient _engine;
    private readonly IStringProvider _strings;
    private DocumentInfo? _info;

    public DocumentViewModel(EngineClient engine, IStringProvider strings, string path)
    {
        _engine = engine;
        _strings = strings;
        FilePath = path;
        Title = Path.GetFileName(path);
    }

    public string FilePath { get; }

    public string Title { get; }

    public IReadOnlyList<PageItemViewModel> Pages { get; private set; } = [];

    public DocumentInfo? Info => _info;

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEncrypted { get; set; }

    /// <summary>
    /// Opens the document. Throws <see cref="ModitException"/> with
    /// <see cref="ErrorCode.PdfEncryptedNeedsPassword"/> or <see cref="ErrorCode.PdfWrongPassword"/>
    /// so the view can prompt and call again with a password.
    /// </summary>
    public async Task OpenAsync(string? password, CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            DocumentInfo info = await _engine.OpenDocumentAsync(FilePath, password, cancellationToken).ConfigureAwait(true);
            _info = info;
            Pages = [.. info.Pages.Select(p => new PageItemViewModel(p.Index, p.WidthPt, p.HeightPt, p.Label))];
            IsEncrypted = info.IsEncrypted;
            StatusText = _strings.Format(
                info.PageCount == 1 ? "DocumentStatusOnePage" : "DocumentStatusPages",
                info.PageCount,
                info.PdfVersion,
                FormatSize(info.FileSizeBytes));
            OnPropertyChanged(nameof(Pages));
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task<RenderedImage> RenderPageAsync(int pageIndex, double scale, CancellationToken cancellationToken)
    {
        DocumentInfo info = _info ?? throw new InvalidOperationException("The document is not open.");
        return _engine.RenderPageAsync(info, pageIndex, scale, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_info is { } info)
        {
            _info = null;
            try
            {
                await _engine.CloseDocumentAsync(info.DocumentId).ConfigureAwait(false);
            }
            catch (ModitException)
            {
                // The host is gone; nothing left to release.
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }
}
