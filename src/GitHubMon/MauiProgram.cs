using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.Blazor.Controls.Toast;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;
using Shiny.Mediator;
#if MACOS
using Microsoft.Maui.Essentials.MacOS;
using Microsoft.Maui.Platform.MacOS.Hosting;
#endif
#if LINUX
using Platform.Maui.Linux.Gtk4.BlazorWebView;
using Platform.Maui.Linux.Gtk4.Essentials;
using Platform.Maui.Linux.Gtk4.Hosting;
#endif

namespace GitHubMon;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
#if MACOS
            .UseMauiAppMacOS<App>()
            .AddMacOSEssentials()
            .AddMacOSBlazorWebView()
#elif LINUX
            .UseMauiAppLinuxGtk4<App>()
            .AddLinuxGtk4Essentials()
#else
            .UseMauiApp<App>()
#endif
            .UseTrayIcon()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if MACOS
        // Platform.Maui.MacOS.BlazorWebView's AddMacOSBlazorWebView only registers the
        // handler — the core Blazor services (NavigationManager, IJSRuntime, ...) still
        // need to be registered or AttachToPage fails at runtime.
        builder.Services.AddBlazorWebView();
#else
        builder.Services.AddMauiBlazorWebView();
#endif
#if LINUX
        builder.Services.AddLinuxGtk4BlazorWebView();
#endif
        builder.Services.AddShinyToast();

        builder.Services.AddNotifications();

        builder.Services.AddShinyMediator(cfg => cfg
            .AddMediatorRegistry()
            .UseMaui()
            .AddMauiPersistentCache()
            .PreventEventExceptions()
        );

        // The single GitHubMonJsonContext is wired here ONCE — IDocumentStore call
        // sites resolve type info from it and never pass JsonTypeInfo themselves.
        builder.Services.AddDocumentStore(options =>
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            options.DatabaseProvider = new SqliteDatabaseProvider($"Data Source={AppPaths.DatabasePath}");
            options.JsonSerializerOptions = new JsonSerializerOptions
            {
                TypeInfoResolver = GitHubMonJsonContext.Default
            };
            options.MapTypeToTable<MonitoredAccount>();
            options.MapTypeToTable<StoredToken>();
            options.MapTypeToTable<SeenFailedRun>();
            options.MapTypeToTable<SeenInboxItem>();
            options.MapTypeToTable<DashboardPrefs>();
        });

        // [Singleton]-attributed services (ConfigStore, SecureTokenVault,
        // GitHubClientFactory, SnapshotCache) via the Shiny DI source generator.
        builder.Services.AddGeneratedServices();

        builder.Services.AddSingletonAsImplementedInterfaces<TrayIconHost>();
        builder.Services.AddSingletonAsImplementedInterfaces<PollerInitializer>();

#if MACOS
        builder.Services.AddSingleton<IFileDialogs, Platforms.MacOS.MacFileDialogs>();
#else
        builder.Services.AddSingleton<IFileDialogs, DownloadsFolderFileDialogs>();
#endif

#if DEBUG
        builder.Logging.AddDebug();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

        var app = builder.Build();
        return app;
    }
}
