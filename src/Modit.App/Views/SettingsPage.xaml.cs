using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Modit.App.ViewModels;

namespace Modit.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ViewModel.LogsFolder);
        using Process? explorer = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            ArgumentList = { ViewModel.LogsFolder },
            UseShellExecute = false,
        });
    }
}
