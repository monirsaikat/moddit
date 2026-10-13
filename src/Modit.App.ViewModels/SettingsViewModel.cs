using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modit.Core;
using Modit.Core.Policy;
using Modit.Core.Settings;
using Modit.Update;

namespace Modit.App.ViewModels;

/// <summary>Marshals a callback onto the UI thread. Implemented by the app with its DispatcherQueue.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Localized text lookup, implemented by the app over its .resw resources.</summary>
public interface IStringProvider
{
    string Get(string key);

    string Format(string key, params object?[] arguments);
}

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IUpdateService _updates;
    private readonly IPolicyProvider _policy;
    private readonly IUiDispatcher _dispatcher;
    private readonly IStringProvider _strings;

    public SettingsViewModel(
        ISettingsService settings, IUpdateService updates, IPolicyProvider policy, AppPaths paths,
        IUiDispatcher dispatcher, IStringProvider strings)
    {
        _settings = settings;
        _updates = updates;
        _policy = policy;
        _dispatcher = dispatcher;
        _strings = strings;

        DataFolder = paths.DataRoot;
        LogsFolder = paths.LogsDirectory;
        IsPortable = paths.IsPortable;
        UpdateFeed = updates.FeedDescription;

        _updates.StateChanged += OnUpdateStateChanged;
        RefreshUpdateState();
    }

    public string DataFolder { get; }

    public string LogsFolder { get; }

    public bool IsPortable { get; }

    public string UpdateFeed { get; }

    /// <summary>0 = follow Windows, 1 = light, 2 = dark. Matches the ComboBox item order.</summary>
    public int ThemeIndex
    {
        get => (int)_settings.Current.Theme;
        set
        {
            if (value < 0 || value == ThemeIndex)
            {
                return;
            }

            _settings.Update(s => s with { Theme = (AppTheme)value });
            OnPropertyChanged();
        }
    }

    /// <summary>0 = stable, 1 = beta.</summary>
    public int UpdateChannelIndex
    {
        get => (int)UpdateRules.EffectiveChannel(_policy.GetString(PolicyNames.UpdateChannel), _settings.Current.UpdateChannel);
        set
        {
            if (value < 0 || value == UpdateChannelIndex || IsUpdateChannelManaged)
            {
                return;
            }

            _settings.Update(s => s with { UpdateChannel = (UpdateChannel)value });
            OnPropertyChanged();
        }
    }

    public bool IsUpdateChannelManaged => _policy.IsManaged(PolicyNames.UpdateChannel);

    public bool IsUpdateChannelEditable => !IsUpdateChannelManaged && UpdatesAvailableInThisCopy;

    public bool AreUpdatesManaged => _policy.IsManaged(PolicyNames.UpdateCheck);

    public bool CheckAutomatically
    {
        get => _settings.Current.CheckForUpdatesAutomatically;
        set
        {
            if (value != CheckAutomatically)
            {
                _settings.Update(s => s with { CheckForUpdatesAutomatically = value });
                OnPropertyChanged();
            }
        }
    }

    public bool InstallOnExit
    {
        get => _settings.Current.InstallUpdatesOnExit;
        set
        {
            if (value != InstallOnExit)
            {
                _settings.Update(s => s with { InstallUpdatesOnExit = value });
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    public partial string CurrentVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUpdateBusy { get; set; }

    [ObservableProperty]
    public partial int UpdateProgress { get; set; }

    [ObservableProperty]
    public partial bool IsRestartAvailable { get; set; }

    [ObservableProperty]
    public partial bool UpdatesAvailableInThisCopy { get; set; } = true;

    [RelayCommand]
    private Task CheckForUpdatesAsync() => _updates.CheckAsync();

    [RelayCommand]
    private void RestartToUpdate() => _updates.RestartToApply();

    public void Dispose() => _updates.StateChanged -= OnUpdateStateChanged;

    private void OnUpdateStateChanged(object? sender, EventArgs e) => _dispatcher.Post(RefreshUpdateState);

    private void RefreshUpdateState()
    {
        UpdateState state = _updates.State;
        CurrentVersion = state.CurrentVersion;
        IsUpdateBusy = state.Status is UpdateStatus.Checking or UpdateStatus.Downloading;
        UpdateProgress = state.ProgressPercent;
        IsRestartAvailable = state.Status == UpdateStatus.ReadyToRestart;
        UpdatesAvailableInThisCopy = state.Status != UpdateStatus.Disabled;
        OnPropertyChanged(nameof(IsUpdateChannelEditable));

        UpdateStatusText = state.Status switch
        {
            UpdateStatus.Disabled => state.DisabledReason switch
            {
                UpdateDisabledReason.Portable => _strings.Get("UpdateStatusPortable"),
                UpdateDisabledReason.Policy => _strings.Get("UpdateStatusPolicy"),
                _ => _strings.Get("UpdateStatusNotInstalled"),
            },
            UpdateStatus.Checking => _strings.Get("UpdateStatusChecking"),
            UpdateStatus.UpToDate => _strings.Get("UpdateStatusUpToDate"),
            UpdateStatus.Downloading => _strings.Format("UpdateStatusDownloading", state.AvailableVersion, state.ProgressPercent),
            UpdateStatus.ReadyToRestart => _strings.Format("UpdateStatusReady", state.AvailableVersion),
            UpdateStatus.Failed => _strings.Format("UpdateStatusFailed", state.Message),
            _ => _strings.Get("UpdateStatusIdle"),
        };
    }
}
