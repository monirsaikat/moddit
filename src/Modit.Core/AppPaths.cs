namespace Modit.Core;

/// <summary>
/// Where Modit keeps its state. An installed copy uses <c>%LOCALAPPDATA%\Modit</c>; a portable copy
/// (a <c>portable.flag</c> file next to the executable) keeps everything in <c>.\data</c> and never
/// touches the registry or the user profile (docs/plan/14, "Portable mode").
/// </summary>
public sealed class AppPaths
{
    public const string PortableFlagFileName = "portable.flag";

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
        return new AppPaths(Path.Combine(local, "Modit"), isPortable: false);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
    }
}
