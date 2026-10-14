using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Modit.App.ViewModels;
using Modit.Core.Pdf;
using Modit.Engine.Client;

namespace Modit.App.Views;

/// <summary>
/// The Organize editor: page thumbnails that can be dragged into a new order, turned, duplicated, deleted,
/// mixed with pages of other PDFs and blank pages, then saved as a new file.
/// </summary>
public sealed partial class OrganizerPage : Page
{
    /// <summary>Longest edge of a thumbnail in device-independent pixels.</summary>
    private const double ThumbnailEdge = 140;

    private readonly IStringProvider _strings;
    private readonly IReadOnlyList<string> _initialFiles;
    private bool _loaded;
    private string? _savedPath;

    public OrganizerPage(OrganizeViewModel viewModel, IReadOnlyList<string> initialFiles)
    {
        ViewModel = viewModel;
        _initialFiles = initialFiles;
        _strings = App.Services.GetRequiredService<IStringProvider>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public OrganizeViewModel ViewModel { get; }

    /// <summary>x:Bind function: the extra rotation as a transform angle.</summary>
    public static double Angle(int rotation) => rotation;

    /// <summary>Adds PDFs after the selection (or at the end), asking for passwords as needed.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        int? insertAt = SelectedItems() is { Count: > 0 } selected ? selected.Max(ViewModel.Pages.IndexOf) + 1 : null;
        foreach (string path in paths)
        {
            int before = ViewModel.Pages.Count;
            string? password = null;
            while (true)
            {
                try
                {
                    Busy.IsActive = true;
                    await ViewModel.AddDocumentAsync(path, password, insertAt);
                    if (insertAt is int at)
                    {
                        insertAt = at + (ViewModel.Pages.Count - before);
                    }

                    break;
                }
                catch (ModitException ex) when (UserPrompts.NeedsPassword(ex))
                {
                    Busy.IsActive = false;
                    password = await UserPrompts.AskPasswordAsync(
                        XamlRoot, Path.GetFileName(path), ex.Code == ErrorCode.PdfWrongPassword, _strings);
                    if (password is null)
                    {
                        break;
                    }
                }
                catch (ModitException ex)
                {
                    ShowResult(InfoBarSeverity.Error, _strings.Get("ErrorOpenTitle"), UserPrompts.Describe(ex, _strings), null);
                    break;
                }
                finally
                {
                    Busy.IsActive = false;
                }
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await AddFilesAsync(_initialFiles);
    }

    private List<OrganizePageItem> SelectedItems() => [.. PageGrid.SelectedItems.OfType<OrganizePageItem>()];

    private int InsertionIndex() =>
        SelectedItems() is { Count: > 0 } selected ? selected.Max(ViewModel.Pages.IndexOf) + 1 : ViewModel.Pages.Count;

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not StackPanel { Children: [Grid { Children: [Border { Child: Image image } paper] }, ..] })
        {
            return;
        }

        (paper.Tag as CancellationTokenSource)?.Cancel();
        paper.Tag = null;
        image.Source = null;
        if (args.InRecycleQueue || args.Item is not OrganizePageItem item)
        {
            return;
        }

        double scale = ThumbnailEdge / Math.Max(item.WidthPt, item.HeightPt);
        paper.Width = Math.Max(8, item.WidthPt * scale);
        paper.Height = Math.Max(8, item.HeightPt * scale);
        if (item.IsBlank)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        paper.Tag = cancellation;
        _ = RenderThumbnailAsync(image, item, cancellation.Token);
    }

    private async Task RenderThumbnailAsync(Image image, OrganizePageItem item, CancellationToken cancellationToken)
    {
        try
        {
            double edge = ThumbnailEdge * (XamlRoot?.RasterizationScale ?? 1.0);
            using RenderedImage? rendered = await ViewModel.RenderThumbnailAsync(item, edge, cancellationToken);
            if (rendered is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var bitmap = new WriteableBitmap(rendered.WidthPx, rendered.HeightPx);
            using (Stream pixels = bitmap.PixelBuffer.AsStream())
            {
                pixels.Write(rendered.Pixels);
            }

            bitmap.Invalidate();
            image.Source = bitmap;
        }
        catch (OperationCanceledException)
        {
            // Scrolled away or recycled.
        }
        catch (ModitException)
        {
            // A thumbnail that fails to render stays blank; saving reports real problems.
        }
    }

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e) => ViewModel.PushUndo();

    private async void OnAddPdfClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id) { ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(".pdf");
        IReadOnlyList<PickFileResult> picked = await picker.PickMultipleFilesAsync();
        await AddFilesAsync(picked.Select(p => p.Path));
    }

    private void OnInsertBlankClick(object sender, RoutedEventArgs e)
    {
        int at = InsertionIndex();
        ViewModel.InsertBlank(at);
        PageGrid.SelectedIndex = at;
    }

    private void OnRotateLeftClick(object sender, RoutedEventArgs e) => ViewModel.Rotate(SelectedOrAll(), -90);

    private void OnRotateRightClick(object sender, RoutedEventArgs e) => ViewModel.Rotate(SelectedOrAll(), 90);

    /// <summary>Rotate with nothing selected turns every page, which is what "rotate the document" means.</summary>
    private List<OrganizePageItem> SelectedOrAll() => SelectedItems() is { Count: > 0 } selected ? selected : [.. ViewModel.Pages];

    private void OnMoveEarlierClick(object sender, RoutedEventArgs e) => MoveSelection(-1);

    private void OnMoveLaterClick(object sender, RoutedEventArgs e) => MoveSelection(+1);

    private void MoveSelection(int delta)
    {
        List<OrganizePageItem> selected = SelectedItems();
        ViewModel.Move(selected, delta);

        // Moving rebuilds the containers; put the selection back.
        foreach (OrganizePageItem item in selected)
        {
            PageGrid.SelectedItems.Add(item);
        }
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e) => ViewModel.Duplicate(SelectedItems());

    private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel.Delete(SelectedItems());

    private void OnUndoClick(object sender, RoutedEventArgs e) => ViewModel.Undo();

    private void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.Delete(SelectedItems());
    }

    private void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.Undo();
    }

    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OnSaveClick(sender, new RoutedEventArgs());
    }

    private void OnRotateRightAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.Rotate(SelectedOrAll(), 90);
    }

    private void OnRotateLeftAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.Rotate(SelectedOrAll(), -90);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasPages)
        {
            ShowResult(InfoBarSeverity.Warning, _strings.Get("ToolResultFailed"), _strings.Get("OrganizeNoPages"), null);
            return;
        }

        var picker = new FileSavePicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(ViewModel.SuggestedFileName),
            DefaultFileExtension = ".pdf",
        };
        picker.FileTypeChoices.Add("PDF", [".pdf"]);
        if (await picker.PickSaveFileAsync() is not { } file)
        {
            return;
        }

        if (ViewModel.Sources.Any(s => string.Equals(s.Path, Path.GetFullPath(file.Path), StringComparison.OrdinalIgnoreCase)))
        {
            ShowResult(InfoBarSeverity.Error, _strings.Get("ToolResultFailed"), _strings.Get("OrganizeSaveOverSource"), null);
            return;
        }

        SaveButton.IsEnabled = false;
        Busy.IsActive = true;
        try
        {
            // The Windows save dialog already asked before replacing an existing file.
            PdfOperationResult result = await ViewModel.SaveAsync(file.Path, overwrite: true);
            ShowResult(
                InfoBarSeverity.Success, _strings.Get("ToolResultDone"), _strings.Format("ToolPagesDone", result.PageCount), result.OutputPath);
        }
        catch (ModitException ex)
        {
            ShowResult(InfoBarSeverity.Error, _strings.Get("ToolResultFailed"), UserPrompts.Describe(ex, _strings), null);
        }
        finally
        {
            SaveButton.IsEnabled = true;
            Busy.IsActive = false;
        }
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message, string? path)
    {
        _savedPath = path;
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultOpenButton.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
        ResultBar.IsOpen = true;
    }

    private void OnOpenResultClick(object sender, RoutedEventArgs e)
    {
        if (_savedPath is not null && File.Exists(_savedPath))
        {
            App.MainWindow.OpenFiles([_savedPath]);
        }
    }
}
