using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.Blazor.Controls.Toast;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;
using Shiny.Mediator;
#if MOBILE
using Microsoft.Maui.ApplicationModel;
#endif
#if WINDOWS
using Microsoft.Maui.LifecycleEvents;
#endif
#if MACOS
using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Hosting;
#endif
#if LINUX
using Microsoft.Maui.Platforms.Linux.Gtk4.BlazorWebView;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Hosting;
using Microsoft.Maui.Platforms.Linux.Gtk4.Hosting;
#endif
#if DEBUG
// DevFlow's GTK build exposes the same two extension methods under .Gtk namespaces, so only the
// usings differ per head — the call site below is shared.
#if LINUX
using Microsoft.Maui.DevFlow.Agent.Gtk;
using Microsoft.Maui.DevFlow.Blazor.Gtk;
#else
using Microsoft.Maui.DevFlow.Agent;
using Microsoft.Maui.DevFlow.Blazor;
#endif
#endif

namespace GitHubShine;

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
#if !MOBILE
            .UseTrayIcon()
#endif
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if WINDOWS
        // Close/minimize hides the window to the tray instead of exiting; re-shown
        // via the tray icon (see MainWindowLauncher). Applied to every window head,
        // including ones re-created after a close.
        builder.ConfigureLifecycleEvents(events =>
            events.AddWindows(windows =>
                windows.OnWindowCreated(GitHubShine.Platforms.Windows.TrayWindowBehavior.HookToTray)));
#endif

#if MACOS
        // Microsoft.Maui.Platforms.MacOS.BlazorWebView's AddMacOSBlazorWebView only registers the
        // handler — the core Blazor services (NavigationManager, IJSRuntime, ...) still
        // need to be registered or AttachToPage fails at runtime.
        builder.Services.AddBlazorWebView();
#else
        builder.Services.AddMauiBlazorWebView();
#endif
#if LINUX
        builder.Services.AddLinuxGtk4BlazorWebView();

        // Without this the GTK head dies during Build(): ConfigStore takes a keyed
        // IKeyValueStore (StoreKeys.Default) that the other heads get from their platform
        // hosting, and nothing on Linux registers one — "No keyed service for type
        // 'Shiny.Extensions.Stores.IKeyValueStore'". Found by actually running the GTK head.
        builder.Services.AddShinyStores();
#endif
        builder.Services.AddShinyToast();

        // Surface compile-time platform facts to the shared Blazor UI (e.g. to hide
        // desktop-only nav items on phones). Always registered — MainLayout injects it
        // on every head — with the value fixed by the compile-time MOBILE constant.
#if MOBILE
        builder.Services.AddSingleton(new AppPlatformInfo(IsMobile: true));
#else
        builder.Services.AddSingleton(new AppPlatformInfo(IsMobile: false));
#endif
        builder.Services.AddSingleton(Browser.Default);

        // Shiny resolves INotificationManager through Shiny.IPlatform. That binding normally comes
        // from Shiny.Hosting.Maui's UseShiny(), but that package ships no net10.0-macos asset — on
        // this app's macOS head it resolves the platform-less net10.0 build and registers nothing,
        // so INotificationManager failed to construct and notifications never worked. Register the
        // per-head platform type directly; each one is a parameterless public type.
#if MACOS
        builder.Services.AddSingleton<Shiny.IPlatform, Shiny.MacPlatform>();
#elif IOS
        builder.Services.AddSingleton<Shiny.IPlatform, Shiny.IosPlatform>();
#elif ANDROID
        builder.Services.AddSingleton<Shiny.IPlatform, Shiny.AndroidPlatform>();
#elif WINDOWS
        builder.Services.AddSingleton<Shiny.IPlatform, Shiny.WindowsPlatform>();
#endif
        builder.Services.AddNotifications();

        builder.Services.AddShinyMediator(cfg => cfg
            .AddMediatorRegistry()
            .UseMaui()
            .AddMauiPersistentCache()
            .PreventEventExceptions()
        );

        // The single GitHubShineJsonContext is wired here ONCE — IDocumentStore call
        // sites resolve type info from it and never pass JsonTypeInfo themselves.
        builder.Services.AddDocumentStore(options =>
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            options.DatabaseProvider = new SqliteDatabaseProvider($"Data Source={AppPaths.DatabasePath}");
            options.JsonSerializerOptions = new JsonSerializerOptions
            {
                TypeInfoResolver = GitHubShineJsonContext.Default
            };
            options.MapTypeToTable<MonitoredAccount>();
            options.MapTypeToTable<StoredToken>();
            options.MapTypeToTable<SeenFailedRun>();
            options.MapTypeToTable<SeenInboxItem>();
            options.MapTypeToTable<DashboardPrefs>();
            options.MapTypeToTable<SyncMapping>();
        });

        // [Singleton]-attributed services (ConfigStore, SecureTokenVault,
        // GitProviderFactory, SnapshotCache) via the Shiny DI source generator.
        builder.Services.AddGeneratedServices();

#if !MOBILE
        // System-tray / menu-bar host is desktop-only (Shiny.Maui.Controls.Desktop).
        builder.Services.AddSingletonAsImplementedInterfaces<TrayIconHost>();
#endif
        builder.Services.AddSingletonAsImplementedInterfaces<PollerInitializer>();
        builder.Services.AddSingletonAsImplementedInterfaces<NotificationAccessInitializer>();

#if MACOS
        builder.Services.AddSingleton<IFileDialogs, GitHubShine.Platforms.MacOS.MacFileDialogs>();
#elif WINDOWS
        builder.Services.AddSingleton<IFileDialogs, GitHubShine.Platforms.Windows.WindowsFileDialogs>();
#elif MOBILE
        builder.Services.AddSingleton<IFileDialogs, MobileFileDialogs>();
#else
        builder.Services.AddSingleton<IFileDialogs, DownloadsFolderFileDialogs>();
#endif
        
#if DEBUG
        builder.Logging.AddDebug();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        // MAUI DevFlow — in-app agent the `maui devflow` CLI drives for visual-tree inspection,
        // taps and screenshots, plus the Blazor tools that reach the WebView contents over CDP.
        // Debug-only by design: it opens a local HTTP server for the CLI to connect to.
        builder.AddMauiDevFlowAgent();
        builder.AddMauiBlazorDevFlowTools();
#endif

        var app = builder.Build();
        return app;
    }
}
