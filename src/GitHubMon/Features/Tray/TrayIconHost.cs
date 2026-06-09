using System.Reflection;
using Shiny.Maui.Controls.Desktop.TrayIcon;
using Shiny.Mediator;

namespace GitHubMon.Tray;

public sealed class TrayIconHost(ITrayIconFactory factory,
                                 IConfigStore config,
                                 SnapshotCache cache,
                                 IMediator mediator,
                                 ILogger<TrayIconHost> logger)
    : IMauiInitializeService, IDisposable
{
    ITrayIcon? trayIcon;
    readonly List<IDisposable> subscriptions = new();

    public void Initialize(IServiceProvider services)
    {
        try
        {
            trayIcon = factory.Create();
            trayIcon.Tooltip = "GitHubMon";
            trayIcon.IsTemplateImage = true;
            trayIcon.SetIcon(() => OpenIconStream("trayicon.png"));
            RebuildMenu();

            subscriptions.Add(mediator.Subscribe<SnapshotUpdatedEvent>((_, _, _) => { UpdateBadge(); return Task.CompletedTask; }));
            subscriptions.Add(mediator.Subscribe<InboxUpdatedEvent>((_, _, _) => { UpdateBadge(); return Task.CompletedTask; }));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tray icon unavailable on this platform");
        }
    }

    void RebuildMenu()
    {
        if (trayIcon is null) return;
        var muted = config.GetNotificationsMutedAsync().GetAwaiter().GetResult();
        trayIcon.SetMenu(TrayMenu.Build(menu =>
        {
            menu.Item("Open dashboard", ShowMainWindow);
            menu.Item("Refresh now", () => _ = mediator.Send(new RefreshAllCommand(true)));
            menu.Separator();
            menu.Check("Mute notifications", muted, async isMuted =>
            {
                await config.SetNotificationsMutedAsync(isMuted).ConfigureAwait(false);
                RebuildMenu();
            });
            menu.Separator();
            menu.Item("Quit", () => Application.Current?.Quit());
        }));
    }

    static void ShowMainWindow()
    {
        var window = Application.Current?.Windows?.FirstOrDefault();
        if (window is not null)
            Application.Current!.ActivateWindow(window);
    }

    static Stream OpenIconStream(string name)
    {
        // FileSystem.OpenAppPackageFileAsync isn't implemented in the maui-labs
        // macOS/Linux essentials yet, so load the icon as an embedded resource.
        var asm = typeof(TrayIconHost).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        return resourceName is null
            ? new MemoryStream()
            : asm.GetManifestResourceStream(resourceName) ?? new MemoryStream();
    }

    void UpdateBadge()
    {
        if (trayIcon is null) return;
        var anyFailed = cache.All.Values.SelectMany(s => s.RecentWorkflowRuns).Any(r => r.IsFailed);
        trayIcon.Tooltip = anyFailed ? "GitHubMon — failures detected" : "GitHubMon";
    }

    public void Dispose()
    {
        foreach (var sub in subscriptions) sub.Dispose();
        subscriptions.Clear();
    }
}
