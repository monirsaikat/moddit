using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Modit.App.Services;
using Velopack;

namespace Modit.App;

public static class Program
{
    private const string SingleInstanceKey = "Modit.App.Main";

    /// <summary>True when the installer launched the app for the first time after installing.</summary>
    internal static bool IsFirstRunAfterInstall { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        // Must be first: when the installer or updater starts us with a hook argument
        // (install, update, uninstall), Velopack runs the callback and exits the process here.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => InstallHooks.Register())
            .OnAfterUpdateFastCallback(_ => InstallHooks.Register())
            .OnBeforeUninstallFastCallback(_ => InstallHooks.Unregister())
            .OnFirstRun(_ => IsFirstRunAfterInstall = true)
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectedToRunningInstance())
        {
            return 0;
        }

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new App(args);
            GC.KeepAlive(app);
            GC.KeepAlive(callbackParams);
        });
        return 0;
    }

    /// <summary>
    /// One Modit process per user session: a second launch (double-clicking another PDF) hands its
    /// arguments to the running instance, which opens a new tab, and exits immediately.
    /// </summary>
    private static bool RedirectedToRunningInstance()
    {
        AppInstance main = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
        if (main.IsCurrent)
        {
            return false;
        }

        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        // The redirect is asynchronous and must not be awaited on this STA thread.
        var redirect = new Thread(() => main.RedirectActivationToAsync(activation).AsTask().GetAwaiter().GetResult());
        redirect.Start();
        redirect.Join();
        return true;
    }
}
