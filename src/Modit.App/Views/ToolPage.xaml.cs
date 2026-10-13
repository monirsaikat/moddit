using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Modit.App.ViewModels;

namespace Modit.App.Views;

/// <summary>One view for every PDF tool; <see cref="ToolViewModel"/> decides which options show.</summary>
public sealed partial class ToolPage : Page
{
    public ToolPage(ToolViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // The page and its view model live and die together, so this is never unsubscribed.
        // (Unloaded fires on every tab switch, which would drop results that arrive in the background.)
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public ToolViewModel ViewModel { get; }

    /// <summary>x:Bind function: negates a boolean for visibility bindings.</summary>
    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ToolViewModel.Progress):
                RunProgress.IsIndeterminate = ViewModel.Progress < 0;
                RunProgress.Value = Math.Max(0, ViewModel.Progress);
                break;

            case nameof(ToolViewModel.HasResult) when ViewModel.HasResult:
                ResultBar.Severity = ViewModel.ResultIsError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
                ResultBar.Title = ViewModel.ResultTitle;
                ResultBar.Message = ViewModel.ResultMessage;
                ResultOpenButton.Visibility = ViewModel.ResultPath is null ? Visibility.Collapsed : Visibility.Visible;
                ResultBar.IsOpen = true;

                // The result appears below the Run button; make sure it is not hidden under the fold.
                ResultBar.UpdateLayout();
                ResultBar.StartBringIntoView();
                break;

            case nameof(ToolViewModel.HasResult):
                ResultBar.IsOpen = false;
                break;
        }
    }

    private async void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".pdf");

        if (ViewModel.AllowsMultipleFiles)
        {
            IReadOnlyList<PickFileResult> picked = await picker.PickMultipleFilesAsync();
            ViewModel.AddFiles(picked.Select(p => p.Path));
        }
        else if (await picker.PickSingleFileAsync() is { } single)
        {
            ViewModel.AddFiles([single.Path]);
        }
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ProducesFolder)
        {
            var folderPicker = new FolderPicker(App.MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            if (await folderPicker.PickSingleFolderAsync() is { } folder)
            {
                ViewModel.SetOutputFromPicker(folder.Path, replaceConfirmedByDialog: false);
            }

            return;
        }

        var savePicker = new FileSavePicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(ViewModel.SuggestedFileName),
            DefaultFileExtension = ".pdf",
        };
        savePicker.FileTypeChoices.Add("PDF", [".pdf"]);
        if (await savePicker.PickSaveFileAsync() is { } file)
        {
            ViewModel.SetOutputFromPicker(file.Path, replaceConfirmedByDialog: true);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToolInputFile file)
        {
            ViewModel.Remove(file);
        }
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.DataContext is ToolInputFile file)
        {
            ViewModel.Move(file, delta);
        }
    }

    // Per-file page ranges and reordering only make sense when several files are combined.
    private void OnPerFilePagesLoaded(object sender, RoutedEventArgs e) =>
        ((FrameworkElement)sender).Visibility = ViewModel.ShowPerFilePages ? Visibility.Visible : Visibility.Collapsed;

    private void OnReorderButtonLoaded(object sender, RoutedEventArgs e) =>
        ((FrameworkElement)sender).Visibility = ViewModel.AllowsMultipleFiles ? Visibility.Visible : Visibility.Collapsed;

    private void OnOpenResultClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ResultPath is not { } path)
        {
            return;
        }

        if (File.Exists(path))
        {
            // A PDF result opens in a Modit tab; that is what the user just made.
            App.MainWindow.OpenFiles([path]);
        }
        else if (Directory.Exists(path))
        {
            using Process? explorer = Process.Start(new ProcessStartInfo("explorer.exe")
            {
                ArgumentList = { path },
                UseShellExecute = false,
            });
        }
    }
}
