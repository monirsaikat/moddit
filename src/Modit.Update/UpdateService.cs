using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Modit.Core;
using Modit.Core.Policy;
using Modit.Core.Settings;
using Velopack;
using Velopack.Sources;

namespace Modit.Update;

/// <summary>
/// Self-update for the per-user (Velopack) install flavor, fed by GitHub Releases
/// (docs/plan/15, "In-app updater"). Checking and downloading happen in the background; nothing is
/// applied while the app is running unless the user asks for a restart.
/// </summary>
public sealed partial class UpdateService : IUpdateService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IPolicyProvider _policy;
    private readonly AppPaths _paths;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _currentVersion;
    private UpdateManager? _manager;
    private UpdateInfo? _pending;
    private UpdateState _state;

    public UpdateService(ISettingsService settings, IPolicyProvider policy, AppPaths paths, ILogger<UpdateService> logger)
    {
        _settings = settings;
        _policy = policy;
        _paths = paths;
        _logger = logger;
        _currentVersion = ReadAssemblyVersion();
        _state = new UpdateState(UpdateStatus.Idle, _currentVersion);
        FeedDescription = DescribeFeed();
    }

    public event EventHandler? StateChanged;

    public UpdateState State => _state;

    public string FeedDescription { get; }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return; // A check is already running.
        }

        try
        {
            if (DisabledReason() is var reason and not UpdateDisabledReason.None)
            {
                SetState(new UpdateState(UpdateStatus.Disabled, _currentVersion, DisabledReason: reason));
                return;
            }

            UpdateManager manager = _manager ??= CreateManager();
            if (!manager.IsInstalled)
            {
                SetState(new UpdateState(
                    UpdateStatus.Disabled, _currentVersion, DisabledReason: UpdateDisabledReason.NotInstalled));
                return;
            }

            string current = manager.CurrentVersion?.ToString() ?? _currentVersion;
            SetState(new UpdateState(UpdateStatus.Checking, current));

            UpdateInfo? info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (info is null)
            {
                SetState(new UpdateState(UpdateStatus.UpToDate, current, LastChecked: now));
                return;
            }

            string available = info.TargetFullRelease.Version.ToString();
            if (UpdateRules.IsSkipped(available, _settings.Current.SkippedVersions))
            {
                LogSkipped(_logger, available);
                SetState(new UpdateState(UpdateStatus.UpToDate, current, LastChecked: now));
                return;
            }

            string? notes = info.TargetFullRelease.NotesMarkdown;
            SetState(new UpdateState(UpdateStatus.Downloading, current, available, 0, ReleaseNotesMarkdown: notes));
            await manager.DownloadUpdatesAsync(
                info,
                percent => SetState(_state with { ProgressPercent = percent }),
                cancellationToken).ConfigureAwait(false);

            _pending = info;
            SetState(new UpdateState(
                UpdateStatus.ReadyToRestart, current, available, 100, ReleaseNotesMarkdown: notes, LastChecked: now));
            LogReady(_logger, available);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(new UpdateState(UpdateStatus.Idle, _currentVersion));
        }
#pragma warning disable CA1031 // The updater must never take the app down: every failure becomes a visible state.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(_logger, ex);
            SetState(new UpdateState(UpdateStatus.Failed, _currentVersion, Message: ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void RestartToApply()
    {
        if (_manager is { } manager && _pending is { } pending)
        {
            manager.ApplyUpdatesAndRestart(pending.TargetFullRelease);
        }
    }

    public void ApplyOnExit()
    {
        if (_manager is { } manager && _pending is { } pending && _settings.Current.InstallUpdatesOnExit)
        {
            // Update.exe waits for this process to exit, swaps the app directory and does not relaunch.
            manager.WaitExitThenApplyUpdates(pending.TargetFullRelease, silent: true, restart: false);
        }
    }

    public void SkipAvailableVersion()
    {
        if (_state.AvailableVersion is not { } version)
        {
            return;
        }

        _settings.Update(s => s with { SkippedVersions = [.. s.SkippedVersions.Append(version).Distinct()] });
        _pending = null;
        SetState(new UpdateState(UpdateStatus.UpToDate, _state.CurrentVersion, LastChecked: _state.LastChecked));
    }

    public void Dispose() => _gate.Dispose();

    private static string ReadAssemblyVersion()
    {
        string? informational = (Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Strip the "+<commit>" build metadata that the SDK appends.
        return informational?.Split('+')[0] ?? "0.0.0";
    }

    private UpdateDisabledReason DisabledReason()
    {
        if (_paths.IsPortable)
        {
            return UpdateDisabledReason.Portable;
        }

        if (_policy.GetBoolean(PolicyNames.UpdateCheck) == false)
        {
            return UpdateDisabledReason.Policy;
        }

        return UpdateDisabledReason.None;
    }

    private string? FeedOverride() =>
        _policy.GetString(PolicyNames.UpdateUrl)
        ?? Environment.GetEnvironmentVariable(UpdateDefaults.FeedEnvironmentVariable);

    private string DescribeFeed() => FeedOverride() ?? UpdateDefaults.ReleasesRepositoryUrl;

    private UpdateManager CreateManager()
    {
        UpdateChannel channel = UpdateRules.EffectiveChannel(
            _policy.GetString(PolicyNames.UpdateChannel), _settings.Current.UpdateChannel);

        IUpdateSource source = FeedOverride() switch
        {
            { } url when url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => new SimpleWebSource(url),
            { } directory => new SimpleFileSource(new DirectoryInfo(directory)),

            // Anonymous access to the public releases repository; pre-releases are the beta track.
            null => new GithubSource(
                UpdateDefaults.ReleasesRepositoryUrl, accessToken: null, prerelease: channel == UpdateChannel.Beta),
        };

        var options = new UpdateOptions
        {
            ExplicitChannel = UpdateRules.ChannelName(RuntimeInformation.ProcessArchitecture, channel),
            AllowVersionDowngrade = false,
        };
        return new UpdateManager(source, options);
    }

    private void SetState(UpdateState state)
    {
        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Update {Version} downloaded and ready to apply.")]
    private static partial void LogReady(ILogger logger, string version);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Update {Version} is skipped by the user.")]
    private static partial void LogSkipped(ILogger logger, string version);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning, Message = "Update check failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
