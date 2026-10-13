using System.Reflection;
using Microsoft.Win32;

namespace Modit.App.Services;

/// <summary>
/// Per-user shell registration performed by the installer hooks (docs/plan/14, "Velopack installer
/// flow"). Everything is under HKCU, is idempotent, and is removed again on uninstall. The hooks run
/// without UI and must finish within seconds.
/// </summary>
internal static class InstallHooks
{
    private const string ProgId = "Modit.Pdf";
    private const string ExeName = "Modit.App.exe";
    private const string InstallKey = @"Software\Modit\Install";

    public static void Register() => Register(Registry.CurrentUser, Environment.ProcessPath ?? string.Empty);

    public static void Unregister() => Unregister(Registry.CurrentUser);

    internal static void Register(RegistryKey root, string exePath)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        string command = $"\"{exePath}\" \"%1\"";

        // A ProgID plus OpenWithProgids makes Modit appear under "Open with" for PDFs without
        // taking over the default handler (which only the user may change on Windows 10/11).
        using (RegistryKey progId = root.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(null, "PDF Document");
            progId.SetValue("FriendlyTypeName", "PDF Document");
            using RegistryKey icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exePath}\",0");
            using RegistryKey open = progId.CreateSubKey(@"shell\open\command");
            open.SetValue(null, command);
        }

        using (RegistryKey openWith = root.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
        {
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (RegistryKey application = root.CreateSubKey($@"Software\Classes\Applications\{ExeName}"))
        {
            application.SetValue("FriendlyAppName", "Modit");
            using RegistryKey types = application.CreateSubKey("SupportedTypes");
            types.SetValue(".pdf", string.Empty);
            using RegistryKey open = application.CreateSubKey(@"shell\open\command");
            open.SetValue(null, command);
        }

        using RegistryKey install = root.CreateSubKey(InstallKey);
        install.SetValue("Flavor", "Velopack");
        install.SetValue("Path", Path.GetDirectoryName(exePath) ?? string.Empty);
        install.SetValue(
            "Version",
            typeof(InstallHooks).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");
    }

    internal static void Unregister(RegistryKey root)
    {
        root.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        root.DeleteSubKeyTree($@"Software\Classes\Applications\{ExeName}", throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(InstallKey, throwOnMissingSubKey: false);

        using (RegistryKey? openWith = root.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", writable: true))
        {
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        // Remove the now-empty parent so nothing of ours is left behind.
        using RegistryKey? modit = root.OpenSubKey(@"Software\Modit", writable: true);
        if (modit is { SubKeyCount: 0, ValueCount: 0 })
        {
            modit.Dispose();
            root.DeleteSubKey(@"Software\Modit", throwOnMissingSubKey: false);
        }
    }
}
