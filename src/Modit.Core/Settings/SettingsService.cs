using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modit.Core.Settings;

public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

public enum UpdateChannel
{
    Stable = 0,
    Beta = 1,
}

/// <summary>User settings. Add fields with defaults; bump <see cref="CurrentSchemaVersion"/> only for breaking shape changes.</summary>
public sealed record AppSettings
{
    // Properties use "set", not "init": the JSON source generator assigns every init-only property
    // when it constructs the object, which would replace these defaults with null/false for any field
    // missing from the file. Treat instances as immutable and change them with "with" expressions.
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool UseMicaBackdrop { get; set; } = true;

    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    public bool CheckForUpdatesAutomatically { get; set; } = true;

    public bool InstallUpdatesOnExit { get; set; } = true;

    public IReadOnlyList<string> SkippedVersions { get; set; } = [];

    public DateTimeOffset? FirstRunCompletedAt { get; set; }

    public string? LastRunVersion { get; set; }

    public WindowPlacement? MainWindow { get; set; }
}

public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool IsMaximized);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler? Changed;

    /// <summary>Applies a change and persists it atomically.</summary>
    void Update(Func<AppSettings, AppSettings> change);
}

/// <summary>
/// JSON settings with atomic writes. A corrupt file never blocks startup: it is set aside as
/// <c>settings.corrupt.&lt;timestamp&gt;.json</c> and defaults are used (docs/plan/14, "Repair and self-healing").
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private AppSettings _current;

    public SettingsService(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.SettingsFile;
        _current = Load();
    }

    public event EventHandler? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Set when the settings file existed but could not be read; holds the path of the backup.</summary>
    public string? CorruptBackupPath { get; private set; }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            AppSettings updated = change(_current) with { SchemaVersion = AppSettings.CurrentSchemaVersion };
            Save(updated);
            _current = updated;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            using FileStream stream = File.OpenRead(_path);
            AppSettings? loaded = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings);
            if (loaded is null)
            {
                return new AppSettings();
            }

            // A newer build wrote this file (rollback case): unknown fields were ignored, keep what we understand.
            // Fields that are absent or null in the file (older version, hand-edited) fall back to defaults:
            // the deserializer does not apply property initializers for them.
            return loaded with
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                SkippedVersions = loaded.SkippedVersions ?? [],
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            CorruptBackupPath = TrySetAside();
            return new AppSettings();
        }
    }

    private string? TrySetAside()
    {
        try
        {
            string backup = Path.Combine(
                Path.GetDirectoryName(_path)!,
                $"settings.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            File.Move(_path, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: true);
    }
}
