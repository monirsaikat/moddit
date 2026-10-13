using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Modit.App.ViewModels;
using Modit.App.Views;
using Modit.Core.Pdf;
using Modit.Core.Settings;
using Modit.Engine.Client;
using Modit.Update;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;

namespace Modit.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 820;

    private readonly ISettingsService _settings;
    private readonly IUpdateService _updates;
    private readonly IStringProvider _strings;
    private readonly IUiDispatcher _dispatcher;
    private TabViewItem? _settingsTab;

    public MainWindow()
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _updates = App.Services.GetRequiredService<IUpdateService>();
        _strings = App.Services.GetRequiredService<IStringProvider>();
        _dispatcher = App.Services.GetRequiredService<IUiDispatcher>();

        Title = _strings.Get("AppTitle/Text");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "modit.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 720;
            presenter.PreferredMinimumHeight = 480;
        }

        RestorePlacement();
        ApplyTheme();
        AddHomeTab();

        _settings.Changed += OnSettingsChanged;
        _updates.StateChanged += OnUpdateStateChanged;
        Closed += OnClosed;
    }

    /// <summary>Opens each file in its own tab; a file that is already open is brought to the front.</summary>
    public void OpenFiles(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            TabViewItem? existing = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(
                t => t.Content is DocumentPage page &&
                     string.Equals(page.ViewModel.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Tabs.SelectedItem = existing;
                continue;
            }

            var viewModel = new DocumentViewModel(App.Services.GetRequiredService<EngineClient>(), _strings, path);
            var tab = new TabViewItem
            {
                Header = viewModel.Title,
                IconSource = new SymbolIconSource { Symbol = Symbol.Document },
                Content = new DocumentPage(viewModel),
            };
            ToolTipService.SetToolTip(tab, path);
            Tabs.TabItems.Add(tab);
            Tabs.SelectedItem = tab;
        }
    }

    /// <summary>Opens the tab of a tool, or selects it when it is already open.</summary>
    public ToolPage OpenTool(ToolKind kind)
    {
        TabViewItem? existing = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(
            t => t.Content is ToolPage page && page.ViewModel.Kind == kind);
        if (existing is not null)
        {
            Tabs.SelectedItem = existing;
            return (ToolPage)existing.Content;
        }

        var viewModel = new ToolViewModel(kind, App.Services.GetRequiredService<PdfService>(), _strings);
        var toolPage = new ToolPage(viewModel);
        var tab = new TabViewItem
        {
            Header = viewModel.Title,
            IconSource = new FontIconSource { Glyph = ToolCatalog.Glyph(kind) },
            Content = toolPage,
        };
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        return toolPage;
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    private void AddHomeTab()
    {
        var home = new HomePage();
        home.OpenRequested += async (_, _) => await PickAndOpenAsync();
        home.ToolRequested += (_, kind) => OpenTool(kind);
        Tabs.TabItems.Add(new TabViewItem
        {
            Header = _strings.Get("HomeTabHeader"),
            IconSource = new SymbolIconSource { Symbol = Symbol.Home },
            IsClosable = false,
            Content = home,
        });
        Tabs.SelectedIndex = 0;
    }

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".pdf");

        IReadOnlyList<PickFileResult> picked = await picker.PickMultipleFilesAsync();
        OpenFiles(picked.Select(p => p.Path));
    }

    private async void OnAddTabClick(TabView sender, object args) => await PickAndOpenAsync();

    private async void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PickAndOpenAsync();
    }

    private async void OnCloseTabAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Tabs.SelectedItem is TabViewItem { IsClosable: true } tab)
        {
            await CloseTabAsync(tab);
        }
    }

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) =>
        await CloseTabAsync(args.Tab);

    private async Task CloseTabAsync(TabViewItem tab)
    {
        Tabs.TabItems.Remove(tab);
        if (ReferenceEquals(tab, _settingsTab))
        {
            (tab.Content as SettingsPage)?.ViewModel.Dispose();
            _settingsTab = null;
        }

        if (tab.Content is ToolPage closedTool)
        {
            closedTool.ViewModel.Dispose();
        }

        if (tab.Content is DocumentPage page)
        {
            await page.ViewModel.DisposeAsync();
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsTab is null)
        {
            _settingsTab = new TabViewItem
            {
                Header = _strings.Get("SettingsTabHeader"),
                IconSource = new SymbolIconSource { Symbol = Symbol.Setting },
                Content = new SettingsPage(App.Services.GetRequiredService<SettingsViewModel>()),
            };
            Tabs.TabItems.Add(_settingsTab);
        }

        Tabs.SelectedItem = _settingsTab;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = _strings.Get("DropCaption");
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        List<string> paths =
        [
            .. items
                .OfType<StorageFile>()
                .Where(f => string.Equals(f.FileType, ".pdf", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Path),
        ];

        // Dropped on a tool, files become its input; anywhere else they open for reading.
        if (Tabs.SelectedItem is TabViewItem { Content: ToolPage toolPage })
        {
            toolPage.ViewModel.AddFiles(paths);
        }
        else
        {
            OpenFiles(paths);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _dispatcher.Post(ApplyTheme);

    private void ApplyTheme()
    {
        ElementTheme theme = _settings.Current.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        Root.RequestedTheme = theme;

        // Caption buttons (minimize, maximize, close) must follow the app theme, not only Windows.
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private void OnUpdateStateChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        UpdateState state = _updates.State;
        if (state.Status == UpdateStatus.ReadyToRestart)
        {
            UpdateBar.Title = _strings.Format("UpdateReadyTitle", state.AvailableVersion);
            UpdateBar.Message = _strings.Get("UpdateReadyMessage");
            UpdateBar.IsOpen = true;
        }
    });

    private void OnRestartToUpdateClick(object sender, RoutedEventArgs e) => _updates.RestartToApply();

    private void RestorePlacement()
    {
        WindowPlacement? saved = _settings.Current.MainWindow;
        if (saved is null || saved.Width < 400 || saved.Height < 300)
        {
            AppWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
            return;
        }

        // Only restore a position that is still on a connected display.
        var rect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
        DisplayArea area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
        if (area is null)
        {
            AppWindow.Resize(new SizeInt32(saved.Width, saved.Height));
        }
        else
        {
            AppWindow.MoveAndResize(rect);
        }

        if (saved.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.Changed -= OnSettingsChanged;
        _updates.StateChanged -= OnUpdateStateChanged;

        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        WindowPlacement? previous = _settings.Current.MainWindow;

        // While maximized, keep the last restored bounds so un-maximizing next time is sensible.
        WindowPlacement placement = maximized && previous is not null
            ? previous with { IsMaximized = true }
            : new WindowPlacement(
                AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, maximized);
        _settings.Update(s => s with { MainWindow = placement });
    }
}
