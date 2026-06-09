using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;
using Shiny.Mediator;
#if MACOS
using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Hosting;
#endif
#if LINUX
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
#elif LINUX
            .UseMauiAppLinuxGtk4<App>()
            .AddLinuxGtk4Essentials()
#else
            .UseMauiApp<App>()
#endif
            .UseShinyControls()
            .UseTrayIcon()
            .UseShinyShell(x => x.AddGeneratedMaps())
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddNotifications();

        builder.Services.AddShinyMediator(cfg => cfg
            .AddMediatorRegistry()
            .UseMaui()
            .AddMauiPersistentCache()
            .PreventEventExceptions()
        );

        builder.Services.AddSingleton<IDocumentStore>(_ =>
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GitHubMon");
            Directory.CreateDirectory(appData);
            var dbPath = Path.Combine(appData, "githubmon.db");
            var options = new DocumentStoreOptions
            {
                DatabaseProvider = new SqliteDatabaseProvider($"Data Source={dbPath}"),
                JsonSerializerOptions = new JsonSerializerOptions
                {
                    TypeInfoResolver = JsonTypeInfoResolver.Combine(
                        AccountsJsonContext.Default,
                        DashboardJsonContext.Default)
                }
            };
            options.MapTypeToTable<MonitoredAccount>();
            options.MapTypeToTable<StoredToken>();
            options.MapTypeToTable<SeenFailedRun>();
            options.MapTypeToTable<SeenInboxItem>();
            return new SqliteDocumentStore(options);
        });

        builder.Services.AddSingleton<ITokenVault, SecureTokenVault>();
        builder.Services.AddSingleton<IConfigStore, ConfigStore>();
        builder.Services.AddSingleton<IGitHubClientFactory, GitHubClientFactory>();
        builder.Services.AddSingleton<SnapshotCache>();

        builder.Services.AddSingleton<TrayIconHost>();
        builder.Services.AddSingleton<IMauiInitializeService>(sp => sp.GetRequiredService<TrayIconHost>());

        builder.Services.AddSingleton<PollerInitializer>();
        builder.Services.AddSingleton<IMauiInitializeService>(sp => sp.GetRequiredService<PollerInitializer>());

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        return app;
    }
}
