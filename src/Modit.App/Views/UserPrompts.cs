using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Modit.App.ViewModels;

namespace Modit.App.Views;

/// <summary>Dialogs and error texts shared by the views that open PDFs.</summary>
internal static class UserPrompts
{
    /// <summary>Asks for a document password. Returns null when the user cancels.</summary>
    public static async Task<string?> AskPasswordAsync(XamlRoot root, string documentName, bool wrongPassword, IStringProvider strings)
    {
        var box = new PasswordBox { PlaceholderText = strings.Get("PasswordPlaceholder") };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = strings.Get(wrongPassword ? "PasswordWrongMessage" : "PasswordNeededMessage"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = documentName,
            Content = panel,
            PrimaryButtonText = strings.Get("PasswordOpenButton"),
            CloseButtonText = strings.Get("CancelButton"),
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }

    /// <summary>Asks whether to throw away unsaved changes. True means discard.</summary>
    public static async Task<bool> ConfirmDiscardAsync(XamlRoot root, string documentName, IStringProvider strings)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = strings.Format("DiscardTitle", documentName),
            Content = strings.Get("DiscardMessage"),
            PrimaryButtonText = strings.Get("DiscardButton"),
            CloseButtonText = strings.Get("CancelButton"),
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static string Describe(ModitException ex, IStringProvider strings) => ex.Code switch
    {
        ErrorCode.PdfCorrupt => strings.Get("ErrorPdfCorrupt"),
        ErrorCode.FileNotFound => strings.Get("ErrorFileNotFound"),
        ErrorCode.FileAccessDenied => strings.Get("ErrorFileAccessDenied"),
        ErrorCode.PdfUnsupportedSecurity => strings.Get("ErrorUnsupportedSecurity"),
        ErrorCode.EngineCrashed or ErrorCode.EngineUnavailable or ErrorCode.EngineTimeout => strings.Get("ErrorEngine"),
        _ => ex.Message,
    };

    public static bool NeedsPassword(ModitException ex) =>
        ex.Code is ErrorCode.PdfEncryptedNeedsPassword or ErrorCode.PdfWrongPassword;
}
