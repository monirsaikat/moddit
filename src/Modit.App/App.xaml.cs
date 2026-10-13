using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Modit.App.Services;
using Modit.App.ViewModels;
using Modit.Core.Settings;
using Modit.Engine.Client;
using Modit.Update;
using Windows.ApplicationModel.Activation;

namespace Modit.App;

#pragma warning disable CA1001 // The App lives exactly as long as the process; the timer is disposed when the window closes.
public sealed partial class App : Application
#pragma warning restore CA1001
{
    private readonly string[] _startupArguments;
    private MainWindow? _window;
    private Timer? _updateTimer;

    public App(string[] startupArguments)
    {
        _startupArguments = startupArguments;
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static ServiceProvider Services { get; private set; } = null!;

    /// <summary>The single main window; pickers and dialogs need it as their owner.</summary>
    public static MainWindow MainWindow { get; private set; } = null!;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Services = AppHost.Build(dispatcherQueue);

        _window = new MainWindow();
        MainWindow = _window;
        _window.Closed += OnWindowClosed;
        _window.Activate();
        OpenFromArguments(_window, _startupArguments);

        // Later launches (another PDF double-clicked) arrive here from Program.RedirectedToRunningInstance.
        AppInstance.GetCurrent().Activated += (_, activation) =>
            dispatcherQueue.TryEnqueue(() => OnRedirectedActivation(activation));

        StartUpdateChecks();
    }

    private void OnRedirectedActivation(AppActivationArguments activation)
    {
        if (_window is null)
        {
            return;
        }

        if (activation.Kind == ExtendedActivationKind.Launch &&
            activation.Data is ILaunchActivatedEventArgs launch)
        {
            OpenFromArguments(_window, SplitCommandLine(launch.Arguments));
        }

        _window.BringToFront();
    }

    private void StartUpdateChecks()
    {
        ISettingsService settings = Services.GetRequiredService<ISettingsService>();
        IUpdateService updates = Services.GetRequiredService<IUpdateService>();

        bool checkNow = Environment.GetEnvironmentVariable("MODIT_UPDATE_CHECK_NOW") == "1";

        // Test hook for tools/release/verify-update-path.ps1: apply a downloaded update without a click.
        if (Environment.GetEnvironmentVariable("MODIT_UPDATE_AUTO_RESTART") == "1")
        {
            updates.StateChanged += (_, _) =>
            {
                if (updates.State.Status == UpdateStatus.ReadyToRestart)
                {
                    updates.RestartToApply();
                }
            };
        }
        TimeSpan firstCheck = checkNow ? TimeSpan.FromSeconds(1) : UpdateDefaults.StartupDelay;

        // Never during startup; then once a day while the app stays open.
        _updateTimer = new Timer(
            _ =>
            {
                if (checkNow || settings.Current.CheckForUpdatesAutomatically)
                {
                    _ = updates.CheckAsync();
                }
            },
            null,
            firstCheck,
            UpdateDefaults.CheckInterval);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _updateTimer?.Dispose();

        // A downloaded update is applied silently after we exit.
        Services.GetRequiredService<IUpdateService>().ApplyOnExit();

        // Kills the engine host with us; the Job Object would do it anyway, this is the orderly path.
        Services.GetRequiredService<EngineClient>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        Services.Dispose();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        ILogger<App>? logger = Services?.GetService<ILogger<App>>();
        if (logger is not null)
        {
            LogUnhandled(logger, e.Exception);
        }
    }

    /// <summary>
    /// <c>Modit.exe a.pdf b.pdf</c> opens the files for reading;
    /// <c>Modit.exe --tool merge a.pdf b.pdf</c> opens a tool with the files as its input.
    /// </summary>
    private static void OpenFromArguments(MainWindow window, IEnumerable<string> arguments)
    {
        List<string> args = [.. arguments];
        int flag = args.FindIndex(a => string.Equals(a, "--tool", StringComparison.OrdinalIgnoreCase));
        if (flag >= 0 && flag + 1 < args.Count &&
            Enum.TryParse(args[flag + 1], ignoreCase: true, out ToolKind kind) && Enum.IsDefined(kind))
        {
            args.RemoveRange(flag, 2);
            window.OpenTool(kind).ViewModel.AddFiles(FilesIn(args));
            return;
        }

        window.OpenFiles(FilesIn(args));
    }

    private static IEnumerable<string> FilesIn(IEnumerable<string> arguments) =>
        arguments.Where(a => !a.StartsWith('-') && File.Exists(a)).Select(Path.GetFullPath);

    /// <summary>Splits a raw command line into arguments, honoring double quotes.</summary>
    private static List<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Critical, Message = "Unhandled exception in the UI process.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
