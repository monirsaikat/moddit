namespace Modit.Core;

/// <summary>
/// Where Modit keeps its state. An installed copy uses <c>%LOCALAPPDATA%\ModitData</c>; a portable copy
/// (a <c>portable.flag</c> file next to the executable) keeps everything in <c>.\data</c> and never
/// touches the registry or the user profile (docs/plan/14, "Portable mode").
/// </summary>
public sealed class AppPaths
{
    public const string PortableFlagFileName = "portable.flag";

    /// <summary>Data folder of an installed copy. Kept outside the install root (<c>%LOCALAPPDATA%\Modit</c>),
    /// which setup, repair and uninstall replace or delete.</summary>
    public const string InstalledDataFolderName = "ModitData";

    public const string LegacyDataFolderName = "Modit";

    public AppPaths(string dataRoot, bool isPortable)
    {
        DataRoot = dataRoot;
        IsPortable = isPortable;
    }

    public string DataRoot { get; }

    public bool IsPortable { get; }

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    public string CacheDirectory => Path.Combine(DataRoot, "cache");

    public string RecoveryDirectory => Path.Combine(DataRoot, "recovery");

    public static AppPaths Detect()
    {
        string baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, PortableFlagFileName)))
        {
            return new AppPaths(Path.Combine(baseDir, "data"), isPortable: true);
        }

        string? overrideRoot = Environment.GetEnvironmentVariable("MODIT_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return new AppPaths(overrideRoot, isPortable: false);
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = new AppPaths(Path.Combine(local, InstalledDataFolderName), isPortable: false);
        paths.MigrateFrom(Path.Combine(local, LegacyDataFolderName));
        return paths;
    }

    /// <summary>
    /// Copies settings and recovery files from an older data root, once, when this root has no settings yet.
    /// Up to 0.4.3 data lived in the Velopack install root, which setup and uninstall delete. Never throws:
    /// a failed migration only means the app starts with defaults.
    /// </summary>
    public void MigrateFrom(string legacyRoot)
    {
        try
        {
            string legacySettings = Path.Combine(legacyRoot, "settings.json");
            if (File.Exists(SettingsFile) || !File.Exists(legacySettings))
            {
                return;
            }

            EnsureCreated();
            string legacyRecovery = Path.Combine(legacyRoot, "recovery");
            if (Directory.Exists(legacyRecovery))
            {
                foreach (string file in Directory.EnumerateFiles(legacyRecovery))
                {
                    File.Copy(file, Path.Combine(RecoveryDirectory, Path.GetFileName(file)), overwrite: false);
                }
            }

            // Settings last: their presence marks the migration as done.
            File.Copy(legacySettings, SettingsFile, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
    }
}
