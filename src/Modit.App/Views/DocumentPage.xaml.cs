using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Modit.App.ViewModels;
using Modit.Engine.Client;

namespace Modit.App.Views;

public sealed partial class DocumentPage : Page
{
    /// <summary>Device-independent pixels per PDF point at 100 % zoom (96 DIP per inch / 72 pt per inch).</summary>
    private const double DipPerPoint = 96.0 / 72.0;

    private const double MinZoom = 0.25;
    private const double MaxZoom = 4.0;

    /// <summary>Upper bound for one page bitmap; beyond it the image is stretched instead of re-rendered.</summary>
    private const double MaxRenderPixels = 16_000_000;

    private static readonly double[] ZoomStops = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.25, 1.5, 2, 3, 4];

    private readonly IStringProvider _strings;
    private bool _opened;
    private double _zoom = 1.0;

    public DocumentPage(DocumentViewModel viewModel)
    {
        ViewModel = viewModel;
        _strings = App.Services.GetRequiredService<IStringProvider>();
        InitializeComponent();
        Loaded += OnLoaded;

        // The "=" and "-" keys next to Backspace have no named VirtualKey.
        AddZoomKey((Windows.System.VirtualKey)187, OnZoomInAccelerator);
        AddZoomKey((Windows.System.VirtualKey)189, OnZoomOutAccelerator);

        // handledEventsToo: the list's ScrollViewer marks wheel input handled before it bubbles here.
        PageList.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheel), handledEventsToo: true);
        ToolsButton.Flyout = BuildToolsMenu();
    }

    /// <summary>Every tool that works on one PDF, opened with this document as its input.</summary>
    private MenuFlyout BuildToolsMenu()
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight };
        foreach (ToolKind kind in ToolCatalog.DocumentTools)
        {
            var item = new MenuFlyoutItem
            {
                Text = _strings.Get($"Tool{kind}Title"),
                Icon = new FontIcon { Glyph = ToolCatalog.Glyph(kind) },
            };
            item.Click += async (_, _) => await App.MainWindow.OpenToolWithFileAsync(kind, ViewModel.FilePath);
            menu.Items.Add(item);
            if (kind == ToolKind.Rotate)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }
        }

        return menu;
    }

    public DocumentViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_opened)
        {
            return;
        }

        _opened = true;
        string? password = null;
        while (true)
        {
            try
            {
                await ViewModel.OpenAsync(password);
                return;
            }
            catch (ModitException ex) when (
                ex.Code is ErrorCode.PdfEncryptedNeedsPassword or ErrorCode.PdfWrongPassword)
            {
                password = await AskPasswordAsync(wrongPassword: ex.Code == ErrorCode.PdfWrongPassword);
                if (password is null)
                {
                    ShowError(_strings.Get("ErrorPasswordCancelled"));
                    return;
                }
            }
            catch (ModitException ex)
            {
                ShowError(DescribeError(ex));
                return;
            }
        }
    }

    private string DescribeError(ModitException ex) => ex.Code switch
    {
        ErrorCode.PdfCorrupt => _strings.Get("ErrorPdfCorrupt"),
        ErrorCode.FileNotFound => _strings.Get("ErrorFileNotFound"),
        ErrorCode.FileAccessDenied => _strings.Get("ErrorFileAccessDenied"),
        ErrorCode.PdfUnsupportedSecurity => _strings.Get("ErrorUnsupportedSecurity"),
        ErrorCode.EngineCrashed or ErrorCode.EngineUnavailable or ErrorCode.EngineTimeout => _strings.Get("ErrorEngine"),
        _ => ex.Message,
    };

    private void ShowError(string message)
    {
        ErrorBar.Title = _strings.Get("ErrorOpenTitle");
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private async Task<string?> AskPasswordAsync(bool wrongPassword)
    {
        var box = new PasswordBox { PlaceholderText = _strings.Get("PasswordPlaceholder") };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = _strings.Get(wrongPassword ? "PasswordWrongMessage" : "PasswordNeededMessage"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.Title,
            Content = panel,
            PrimaryButtonText = _strings.Get("PasswordOpenButton"),
            CloseButtonText = _strings.Get("CancelButton"),
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not Border { Child: Image image } border)
        {
            return;
        }

        // Each container carries the cancellation of its in-flight render so recycling stops wasted work.
        (border.Tag as CancellationTokenSource)?.Cancel();
        border.Tag = null;

        if (args.InRecycleQueue || args.Item is not PageItemViewModel page)
        {
            image.Source = null;
            return;
        }

        image.Source = null;
        Present(border, image, page);
    }

    /// <summary>Sizes a page container for the current zoom and starts its render.</summary>
    private void Present(Border border, Image image, PageItemViewModel page)
    {
        (border.Tag as CancellationTokenSource)?.Cancel();
        border.Width = (page.WidthPt * DipPerPoint * _zoom) + 2;
        border.Height = (page.HeightPt * DipPerPoint * _zoom) + 2;

        var cancellation = new CancellationTokenSource();
        border.Tag = cancellation;
        _ = RenderIntoAsync(image, page, cancellation.Token);
    }

    private void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 0.001)
        {
            return;
        }

        _zoom = zoom;
        ZoomLabel.Content = zoom.ToString("P0", CultureInfo.CurrentCulture);

        // Only realized containers exist; the rest pick up the new zoom when they scroll into view.
        // The old bitmap stays stretched on screen until the sharp one arrives.
        for (int i = 0; i < ViewModel.Pages.Count; i++)
        {
            if (PageList.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: Border { Child: Image image } border })
            {
                Present(border, image, ViewModel.Pages[i]);
            }
        }
    }

    private void ZoomIn() => SetZoom(ZoomStops.FirstOrDefault(s => s > _zoom + 0.001, MaxZoom));

    private void ZoomOut() => SetZoom(ZoomStops.LastOrDefault(s => s < _zoom - 0.001, MinZoom));

    private void ZoomToFitWidth()
    {
        if (ViewModel.Pages.Count == 0 || PageList.ActualWidth <= 0)
        {
            return;
        }

        // Leave room for the scroll bar and a little air on both sides.
        double available = PageList.ActualWidth - 48;
        double widest = ViewModel.Pages.Max(p => p.WidthPt) * DipPerPoint;
        SetZoom(available / widest);
    }

    private void AddZoomKey(
        Windows.System.VirtualKey key,
        Windows.Foundation.TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        accelerator.Invoked += handler;
        KeyboardAccelerators.Add(accelerator);
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomOut();

    private void OnZoomResetClick(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void OnZoomFitClick(object sender, RoutedEventArgs e) => ZoomToFitWidth();

    private void OnZoomInAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ZoomIn();
    }

    private void OnZoomOutAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ZoomOut();
    }

    private void OnZoomResetAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetZoom(1.0);
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            return;
        }

        int delta = e.GetCurrentPoint(PageList).Properties.MouseWheelDelta;
        if (delta > 0)
        {
            ZoomIn();
        }
        else if (delta < 0)
        {
            ZoomOut();
        }

        e.Handled = true;
    }

    private async Task RenderIntoAsync(Image image, PageItemViewModel page, CancellationToken cancellationToken)
    {
        try
        {
            // Render at the display's real pixel density so text is crisp on 125 %, 150 % and 200 % screens.
            double scale = DipPerPoint * _zoom * (XamlRoot?.RasterizationScale ?? 1.0);
            double pixelCount = page.WidthPt * scale * page.HeightPt * scale;
            if (pixelCount > MaxRenderPixels)
            {
                scale *= Math.Sqrt(MaxRenderPixels / pixelCount);
            }

            using RenderedImage rendered = await ViewModel.RenderPageAsync(page.Index, scale, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
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
            // Scrolled away before the render finished.
        }
        catch (ModitException ex)
        {
            ShowError(DescribeError(ex));
        }
    }
}
