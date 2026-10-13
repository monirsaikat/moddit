using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.ApplicationModel.Resources;
using Modit.App.ViewModels;
using Modit.Core;
using Modit.Core.Logging;
using Modit.Core.Pdf;
using Modit.Core.Policy;
using Modit.Core.Settings;
using Modit.Engine.Client;
using Modit.Update;

namespace Modit.App.Services;

/// <summary>Composition root: builds the service container used by the whole app.</summary>
internal static class AppHost
{
    public static ServiceProvider Build(DispatcherQueue dispatcherQueue)
    {
        AppPaths paths = AppPaths.Detect();
        paths.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddProvider(new FileLoggerProvider(paths.LogsDirectory));
        });
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IPolicyProvider>(_ => new RegistryPolicyProvider());
        services.AddSingleton<IUpdateService, UpdateService>();

        // The host process starts lazily on the first document, never during app startup.
        services.AddSingleton(_ => new EngineClient());
        services.AddSingleton<PdfService>();
        services.AddSingleton<IUiDispatcher>(new UiDispatcher(dispatcherQueue));
        services.AddSingleton<IStringProvider, ResourceStrings>();
        services.AddTransient<SettingsViewModel>();
        return services.BuildServiceProvider();
    }
}

internal sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            queue.TryEnqueue(() => action());
        }
    }
}

/// <summary>Strings from Strings/&lt;language&gt;/Resources.resw.</summary>
internal sealed class ResourceStrings : IStringProvider
{
    private readonly ResourceLoader _loader = new();

    public string Get(string key)
    {
        string value = _loader.GetString(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), arguments);
}
