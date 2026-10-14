using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Modit.App.ViewModels;
using Modit.Core.Settings;

namespace Modit.App.Views;

/// <summary>A tile on the Home screen that opens one tool.</summary>
public sealed record ToolTile(ToolKind Kind, string Glyph, string Title, string Description);

public sealed partial class HomePage : Page
{
    private readonly ISettingsService _settings;

    public HomePage()
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        IStringProvider strings = App.Services.GetRequiredService<IStringProvider>();

        ToolTile Tile(ToolKind k) => new(k, ToolCatalog.Glyph(k), strings.Get($"Tool{k}Title"), strings.Get($"Tool{k}Description"));
        EditTiles.ItemsSource = ToolCatalog.EditTools.Select(Tile).ToList();
        ToolTiles.ItemsSource = ToolCatalog.PdfTools.Select(Tile).ToList();
        ConvertTiles.ItemsSource = ToolCatalog.ConvertTools.Select(Tile).ToList();

        if (_settings.Current.FirstRunCompletedAt is null)
        {
            WelcomeTheme.SelectedIndex = (int)_settings.Current.Theme;
            WelcomeCard.Visibility = Visibility.Visible;
        }
    }

    /// <summary>The user asked to open a file (button or keyboard).</summary>
    public event EventHandler? OpenRequested;

    /// <summary>The user picked a tool tile.</summary>
    public event EventHandler<ToolKind>? ToolRequested;

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnToolTileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ToolTile tile)
        {
            ToolRequested?.Invoke(this, tile.Kind);
        }
    }

    private void OnWelcomeThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WelcomeTheme.SelectedIndex >= 0 && WelcomeCard.Visibility == Visibility.Visible)
        {
            _settings.Update(s => s with { Theme = (AppTheme)WelcomeTheme.SelectedIndex });
        }
    }

    private void OnWelcomeDoneClick(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s with { FirstRunCompletedAt = DateTimeOffset.UtcNow });
        WelcomeCard.Visibility = Visibility.Collapsed;
    }
}
