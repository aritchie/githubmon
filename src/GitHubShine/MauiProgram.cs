using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.Blazor.Controls.Toast;
using Shiny.DocumentDb;
using Shiny.Jobs;
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

        // Shiny's platform services — IPlatform and the concrete per-head platform type that
        // Shiny's own JobManager takes — come from Shiny itself rather than being hand-registered
        // here. On the heads Shiny.Hosting.Maui supports, UseShiny() is the entry point; the labs
        // macOS/Linux heads aren't MAUI heads it knows about (its only non-platform asset is an
        // empty net10.0 one), so they call Shiny.Core's registration directly.
#if ANDROID || IOS || WINDOWS
        builder.UseShiny();
#else
        Shiny.Infrastructure.ShinyInfrastructureExtensions.AddShinyCoreServices(builder.Services);
#endif
        // The delegate is what runs when the user taps a notification — see
        // NotificationEntryDelegate.
        builder.Services.AddNotifications<NotificationEntryDelegate>();

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
            options.MapTypeToTable<SeenWorkflowState>();
            options.MapTypeToTable<DashboardPrefs>();
            options.MapTypeToTable<NotificationPrefs>();
            options.MapTypeToTable<SyncMapping>();
            options.MapTypeToTable<AutoSyncPrefs>();
            options.MapTypeToTable<ClonePrefs>();
            options.MapTypeToTable<PollState>();
            options.MapTypeToTable<FollowedPerson>();
        });

        // [Singleton]-attributed services (ConfigStore, SecureTokenVault,
        // GitProviderFactory, SnapshotCache) via the Shiny DI source generator.
        builder.Services.AddGeneratedServices();

#if !MOBILE
        // System-tray / menu-bar host is desktop-only (Shiny.Maui.Controls.Desktop).
        builder.Services.AddSingletonAsImplementedInterfaces<TrayIconHost>();
#endif

        // JobManager constructor-injects IBattery and IConnectivity (it evaluates the
        // WithInternet/WithBatteryNotLow constraints before each run). On iOS/Android the
        // platform-specific Shiny.Jobs build registers them itself, but the desktop heads resolve
        // Shiny.Jobs' plain net10.0 fallback, which compiles against Shiny.Core's net10.0 asset —
        // and that asset declares only the interfaces, so its AddJob has no implementations to
        // wire up. Register them per-head, exactly as with Shiny.IPlatform above.
        // Symptom when missing: "Unable to resolve service for type 'Shiny.Power.IBattery' while
        // attempting to activate 'Shiny.Jobs.JobManager'" at startup.
#if MACOS || WINDOWS || LINUX
        builder.Services.AddBattery();
        builder.Services.AddConnectivity();
#endif

        // Background work runs as Shiny jobs rather than hand-rolled Task.Run loops. The reason is
        // mobile: a loop only lives as long as the process, so once iOS suspended the app polling
        // stopped and every notification with it, with no way back. AddJob binds the native
        // schedulers on iOS/Android (BGTaskScheduler / WorkManager) and Shiny's in-process
        // JobManager on the desktop heads.
        //
        // WithForeground keeps them running while the app is open too (the JobLifecycleTask
        // driver, ~1min), which is what replaces the old loops' foreground behaviour. Note this is
        // NOT an Android foreground service — it just means "also run while foregrounded".
        //
        // Both jobs are invoked far more often than they should do work and gate themselves on a
        // stored timestamp: IPollSchedule for the refresh, AutoSyncPrefs.LastRunUtc for the sync.
        builder.Services.AddJob<RepoPollJob>(r => r
            .WithForeground()
            .WithInternet(InternetAccess.Any)
        );

#if !MOBILE
        // Auto-sync stays desktop-only for the same reason the Sync nav item is: it's a two-account
        // workflow with a mapping list, which the phone UI doesn't offer. (Git itself is in-process
        // now, so this is a UI decision rather than a capability one.)
        builder.Services.AddJob<AutoSyncJob>(r => r
            .WithForeground()
            .WithInternet(InternetAccess.Any)
        );
#endif

#if !(ANDROID || IOS || WINDOWS)
        // Starts every IShinyStartupTask, which UseShiny() does for us on the heads it supports.
        // Registered ahead of JobStartupService because it is what starts the job manager, and
        // ahead of anything else that assumes Shiny's platform lifecycle is live.
        builder.Services.AddSingletonAsImplementedInterfaces<ShinyStartupTaskService>();
#endif

        // Constructs the job manager and reports whether the OS will run it — nothing else
        // resolves IJobManager, and DI is lazy, so without this the jobs above are registered but
        // never invoked.
        builder.Services.AddSingletonAsImplementedInterfaces<JobStartupService>();

        // Startup paint + refresh-on-config-change; the periodic refresh is RepoPollJob's.
        builder.Services.AddSingletonAsImplementedInterfaces<PollStartupService>();
        builder.Services.AddSingletonAsImplementedInterfaces<NotificationAccessInitializer>();
        builder.Services.AddSingletonAsImplementedInterfaces<NotificationPrefsInitializer>();

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
