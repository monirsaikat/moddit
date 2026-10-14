using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Modit.Core.Pdf;
using Modit.Engine;
using Modit.Engine.Client;

namespace Modit.App.ViewModels;

/// <summary>A page in the Organize editor. <see cref="Rotation"/> is the extra clockwise turn the user added.</summary>
public sealed partial class OrganizePageItem : ObservableObject
{
    public OrganizePageItem(int sourceIndex, int pageIndex, double widthPt, double heightPt, string label)
    {
        SourceIndex = sourceIndex;
        PageIndex = pageIndex;
        WidthPt = widthPt;
        HeightPt = heightPt;
        Label = label;
    }

    public int SourceIndex { get; }

    public int PageIndex { get; }

    /// <summary>Size as the page is displayed, before the user's extra rotation.</summary>
    public double WidthPt { get; }

    public double HeightPt { get; }

    /// <summary>Where the page came from, such as "A 3" (document A, page 3) or "Blank".</summary>
    public string Label { get; }

    public bool IsBlank => SourceIndex == OrganizePage.BlankSource;

    [ObservableProperty]
    public partial int Rotation { get; set; }

    public OrganizePageItem Copy() => new(SourceIndex, PageIndex, WidthPt, HeightPt, Label) { Rotation = Rotation };
}

public sealed record OrganizeSourceInfo(string Path, string? Password, DocumentInfo Document, string Letter);

/// <summary>
/// State of the Organize editor: the pages of the result in order, plus the open source documents that
/// supply them. Nothing is written until <see cref="SaveAsync"/>; sources are never modified.
/// </summary>
public sealed partial class OrganizeViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxUndo = 50;

    private readonly EngineClient _engine;
    private readonly PdfService _service;
    private readonly IStringProvider _strings;
    private readonly List<OrganizeSourceInfo> _sources = [];
    private readonly Stack<List<(OrganizePageItem Item, int Rotation)>> _undo = new();

    public OrganizeViewModel(EngineClient engine, PdfService service, IStringProvider strings)
    {
        _engine = engine;
        _service = service;
        _strings = strings;
        Pages.CollectionChanged += (_, _) =>
        {
            IsDirty = true;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HasPages));
        };
    }

    public ObservableCollection<OrganizePageItem> Pages { get; } = [];

    public IReadOnlyList<OrganizeSourceInfo> Sources => _sources;

    public string Title => _sources.Count > 0 ? Path.GetFileName(_sources[0].Path) : _strings.Get("ToolOrganizeTitle");

    public bool HasPages => Pages.Count > 0;

    public bool CanUndo => _undo.Count > 0;

    public string StatusText => _strings.Format(Pages.Count == 1 ? "OrganizeStatusOne" : "OrganizeStatus", Pages.Count, _sources.Count);

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    /// <summary>A suggested file name for Save as: "report-organized.pdf".</summary>
    public string SuggestedFileName =>
        (_sources.Count > 0 ? Path.GetFileNameWithoutExtension(_sources[0].Path) : "document") + "-organized.pdf";

    public string? SuggestedFolder => _sources.Count > 0 ? Path.GetDirectoryName(_sources[0].Path) : null;

    /// <summary>
    /// Opens a PDF and inserts all its pages at <paramref name="insertAt"/> (the end when null). Throws
    /// <see cref="ModitException"/> with a password code so the view can ask and call again.
    /// </summary>
    public async Task AddDocumentAsync(string path, string? password, int? insertAt = null, CancellationToken cancellationToken = default)
    {
        DocumentInfo info = await _engine.OpenDocumentAsync(path, password, cancellationToken).ConfigureAwait(true);
        int sourceIndex = _sources.Count;
        string letter = LetterFor(sourceIndex);
        _sources.Add(new OrganizeSourceInfo(Path.GetFullPath(path), password, info, letter));
        OnPropertyChanged(nameof(Title));

        PushUndo();
        int at = Math.Clamp(insertAt ?? Pages.Count, 0, Pages.Count);
        foreach (PageInfo page in info.Pages)
        {
            string label = string.Create(CultureInfo.CurrentCulture, $"{letter} {page.Index + 1}");
            Pages.Insert(at++, new OrganizePageItem(sourceIndex, page.Index, page.WidthPt, page.HeightPt, label));
        }

        // Opening the first document is not an edit.
        if (sourceIndex == 0)
        {
            _undo.Clear();
            IsDirty = false;
            OnPropertyChanged(nameof(CanUndo));
        }
    }

    /// <summary>Inserts a blank page the size of its neighbor (A4 when there is none).</summary>
    public void InsertBlank(int at)
    {
        PushUndo();
        at = Math.Clamp(at, 0, Pages.Count);
        OrganizePageItem? neighbor = at > 0 ? Pages[at - 1] : Pages.FirstOrDefault();
        bool turned = neighbor is not null && neighbor.Rotation % 180 != 0;
        double width = neighbor is null ? 595.28 : turned ? neighbor.HeightPt : neighbor.WidthPt;
        double height = neighbor is null ? 841.89 : turned ? neighbor.WidthPt : neighbor.HeightPt;
        Pages.Insert(at, new OrganizePageItem(OrganizePage.BlankSource, 0, width, height, _strings.Get("OrganizeBlankLabel")));
    }

    public void Rotate(IReadOnlyList<OrganizePageItem> items, int degreesClockwise)
    {
        if (items.Count == 0)
        {
            return;
        }

        PushUndo();
        foreach (OrganizePageItem item in items)
        {
            item.Rotation = (((item.Rotation + degreesClockwise) % 360) + 360) % 360;
        }

        IsDirty = true;
    }

    public void Delete(IReadOnlyList<OrganizePageItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        PushUndo();
        foreach (OrganizePageItem item in items.ToList())
        {
            Pages.Remove(item);
        }
    }

    public void Duplicate(IReadOnlyList<OrganizePageItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        PushUndo();
        int at = items.Max(Pages.IndexOf) + 1;
        foreach (OrganizePageItem item in items.OrderBy(Pages.IndexOf).ToList())
        {
            Pages.Insert(at++, item.Copy());
        }
    }

    /// <summary>Moves the items one place earlier (-1) or later (+1), keeping their relative order.</summary>
    public void Move(IReadOnlyList<OrganizePageItem> items, int delta)
    {
        List<int> indexes = [.. items.Select(Pages.IndexOf).Where(i => i >= 0).Order()];
        if (indexes.Count == 0 || (delta < 0 && indexes[0] == 0) || (delta > 0 && indexes[^1] == Pages.Count - 1))
        {
            return;
        }

        PushUndo();
        IEnumerable<int> order = delta < 0 ? indexes : indexes.AsEnumerable().Reverse();
        foreach (int index in order)
        {
            Pages.Move(index, index + delta);
        }
    }

    /// <summary>Records the current arrangement; call before every change, including drag and drop.</summary>
    public void PushUndo()
    {
        _undo.Push([.. Pages.Select(p => (p, p.Rotation))]);
        if (_undo.Count > MaxUndo)
        {
            // Stack has no "drop oldest"; rebuild without the bottom entry.
            List<List<(OrganizePageItem, int)>> kept = [.. _undo.Take(MaxUndo)];
            _undo.Clear();
            for (int i = kept.Count - 1; i >= 0; i--)
            {
                _undo.Push(kept[i]);
            }
        }

        OnPropertyChanged(nameof(CanUndo));
    }

    public void Undo()
    {
        if (!_undo.TryPop(out List<(OrganizePageItem Item, int Rotation)>? snapshot))
        {
            return;
        }

        Pages.Clear();
        foreach ((OrganizePageItem item, int rotation) in snapshot)
        {
            item.Rotation = rotation;
            Pages.Add(item);
        }

        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Renders a thumbnail whose longer edge is <paramref name="maxEdgePx"/>. Blank pages return null.</summary>
    public async Task<RenderedImage?> RenderThumbnailAsync(OrganizePageItem item, double maxEdgePx, CancellationToken cancellationToken)
    {
        if (item.IsBlank)
        {
            return null;
        }

        DocumentInfo document = _sources[item.SourceIndex].Document;
        double scale = maxEdgePx / Math.Max(item.WidthPt, item.HeightPt);
        return await _engine.RenderPageAsync(document, item.PageIndex, scale, cancellationToken).ConfigureAwait(true);
    }

    public async Task<PdfOperationResult> SaveAsync(string outputPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        PdfOperationResult result = await _service.OrganizeAsync(
            [.. _sources.Select(s => new OrganizeSource(s.Path, s.Password))],
            [.. Pages.Select(p => p.IsBlank
                ? OrganizePage.Blank(p.WidthPt, p.HeightPt) with { RotationDegrees = p.Rotation }
                : new OrganizePage(p.SourceIndex, p.PageIndex, p.Rotation))],
            outputPath,
            new OutputOptions { Overwrite = overwrite },
            cancellationToken).ConfigureAwait(true);
        IsDirty = false;
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (OrganizeSourceInfo source in _sources)
        {
            try
            {
                await _engine.CloseDocumentAsync(source.Document.DocumentId).ConfigureAwait(false);
            }
            catch (ModitException)
            {
                // The host is gone; nothing left to release.
            }
        }

        _sources.Clear();
    }

    /// <summary>A, B, ... Z, AA, AB ...</summary>
    private static string LetterFor(int index) =>
        index < 26 ? ((char)('A' + index)).ToString() : LetterFor((index / 26) - 1) + (char)('A' + (index % 26));
}
