namespace Modit.Update;

public enum UpdateStatus
{
    /// <summary>Updates cannot run in this copy; see <see cref="UpdateState.DisabledReason"/>.</summary>
    Disabled,
    Idle,
    Checking,
    UpToDate,
    Downloading,

    /// <summary>The new version is downloaded and verified; it is applied on restart or exit.</summary>
    ReadyToRestart,
    Failed,
}

public enum UpdateDisabledReason
{
    None,

    /// <summary>Portable copy: the user replaces the folder themselves.</summary>
    Portable,

    /// <summary>Not installed by the Modit installer (development run, MSI or MSIX flavor).</summary>
    NotInstalled,

    /// <summary>Turned off by administrator policy.</summary>
    Policy,
}

public sealed record UpdateState(
    UpdateStatus Status,
    string CurrentVersion,
    string? AvailableVersion = null,
    int ProgressPercent = 0,
    string? Message = null,
    string? ReleaseNotesMarkdown = null,
    UpdateDisabledReason DisabledReason = UpdateDisabledReason.None,
    DateTimeOffset? LastChecked = null);

public interface IUpdateService
{
    UpdateState State { get; }

    /// <summary>The feed this copy updates from, for display in About and diagnostics.</summary>
    string FeedDescription { get; }

    event EventHandler? StateChanged;

    /// <summary>
    /// Checks the feed and, when a newer version exists, downloads it in the background.
    /// Never throws for network or feed problems; the outcome is reported through <see cref="State"/>.
    /// </summary>
    Task CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the app, applies the downloaded update and starts the new version.</summary>
    void RestartToApply();

    /// <summary>Call while shutting down: applies a downloaded update after the process exits, without restarting.</summary>
    void ApplyOnExit();

    /// <summary>Stops offering the version that is currently available until a newer one appears.</summary>
    void SkipAvailableVersion();
}

/// <summary>Build-time defaults of the update system (docs/plan/15).</summary>
public static class UpdateDefaults
{
    /// <summary>Public repository whose GitHub Releases carry installers and update packages (docs/plan/13 Q16).</summary>
    public const string ReleasesRepositoryUrl = "https://github.com/modit-app/modit-releases";

    /// <summary>Overrides the feed with a folder or URL; used by tests and by tools/release/local-feed.ps1.</summary>
    public const string FeedEnvironmentVariable = "MODIT_UPDATE_FEED";

    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
}
