// Desktop-only: the system tray / menu-bar host depends on Shiny.Maui.Controls.Desktop,
// which isn't referenced on iOS/Android. Compile the whole file out on mobile heads.

#if !MOBILE
using System.Reflection;
using Microsoft.Maui.ApplicationModel;
using Shiny.Maui.Controls.Desktop.TrayIcon;
using Shiny.Mediator;

namespace GitHubShine.Tray;

public sealed class TrayIconHost(
    ITrayIconFactory factory,
    IConfigStore config,
    INotificationPrefsStore notifyPrefs,
    SnapshotCache cache,
    IMediator mediator,
    IBrowser browser,
    ILogger<TrayIconHost> logger
) : IMauiInitializeService, IDisposable
{
    ITrayIcon? trayIcon;
    readonly List<IDisposable> subscriptions = new();

    int rebuildPending;

    // The rendered content of the last menu we pushed. Snapshot/inbox events fire
    // every poll cycle even when the displayed counts/statuses are identical, so we
    // skip SetMenu when nothing visible changed — each SetMenu builds a fresh native
    // menu tree, and rebuilding one every minute for the life of the app is a heavy,
    // unbounded source of native allocations.
    string? lastMenuSignature;
    string? lastTooltip;

    // Cached so RebuildMenu can stay synchronous. The grid sort lives in the document
    // store, so reading it inline would mean blocking on SQLite from whatever thread the
    // rebuild came in on — including the UI thread, via the config-changed event. Same
    // reasoning as the cached mute flag below.
    RepoGridSort currentSort = RepoGridSort.Default;

    public void Initialize(IServiceProvider services)
    {
        try
        {
            trayIcon = factory.Create();
            trayIcon.Tooltip = "GitHub Shine";
            trayIcon.IsTemplateImage = true;
            trayIcon.SetIcon(() => OpenIconStream("trayicon.png"));
            // Left/primary click opens the dashboard; the menu (set in RebuildMenu) is
            // reserved for right/secondary click.
            trayIcon.PrimaryClick += OnPrimaryClick;
            // Paint immediately with the default sort so a right-click always has a menu,
            // then fill in the stored sort and redraw if it differs.
            RebuildMenu();
            _ = ReloadSortAndRebuildAsync();

            // Accounts and the grid-sort preference both change the repo submenu;
            // snapshots change the per-repo counts, build state, and ordering.
            config.Changed += OnConfigChanged;
            config.PreferencesChanged += OnConfigChanged;
            notifyPrefs.Changed += OnConfigChanged;
            subscriptions.Add(mediator.Subscribe<SnapshotUpdatedEvent>((_, _, _) =>
            {
                RequestMenuRebuild();
                return Task.CompletedTask;
            }));
            subscriptions.Add(mediator.Subscribe<InboxUpdatedEvent>((_, _, _) =>
            {
                RequestMenuRebuild();
                return Task.CompletedTask;
            }));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tray icon unavailable on this platform");
        }
    }

    // A config/preference change can alter the menu (accounts, sort, mute) without a
    // matching snapshot event, so force the next build past the unchanged-content guard.
    void OnConfigChanged(object? sender, EventArgs e)
    {
        lastMenuSignature = null;
        _ = ReloadSortAndRebuildAsync();
    }

    // The grid sort is one of the things a preference change can alter, so refresh the
    // cached copy off-thread before redrawing.
    async Task ReloadSortAndRebuildAsync()
    {
        try
        {
            currentSort = await config.GetRepoGridSortAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read the repositories grid sort; keeping the previous one");
        }
        RebuildMenu();
    }

    // Snapshot/inbox events fire once per repo per poll cycle — coalesce the bursts
    // into a single rebuild so the menu isn't torn down dozens of times in a row.
    void RequestMenuRebuild()
    {
        if (Interlocked.CompareExchange(ref rebuildPending, 1, 0) != 0)
            return;
        _ = Task.Delay(750).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref rebuildPending, 0);
            RebuildMenu();
        });
    }

    void RebuildMenu()
    {
        if (trayIcon is null) return;
        try
        {
            // Cached, so this stays a synchronous read now that mute lives in the document store.
            var muted = notifyPrefs.Current.Muted;
            var sort = currentSort;
            var repos = sort.Order(Cards());

            // Everything the menu renders, in order — if it matches the last push,
            // there's nothing to redraw and we avoid leaking another native menu tree.
            var signature = string.Join("\n",
                repos.Select(c => $"{StatusIcon(c)}|{c.Title}|{Metric(c, sort)}")
                    .Prepend($"muted={muted};sort={sort.ColumnId}:{sort.Descending};count={repos.Count}"));
            if (signature == lastMenuSignature)
                return;
            lastMenuSignature = signature;

            trayIcon.SetMenu(TrayMenu.Build(menu =>
            {
                menu.Item("Open dashboard", ShowMainWindow);
                menu.Item("Refresh now", () => _ = mediator.Send(new RefreshAllCommand(true)));
                menu.Separator();
                menu.Submenu("Repositories", sub =>
                {
                    if (repos.Count == 0)
                    {
                        sub.Item("No repositories", ShowMainWindow);
                        return;
                    }

                    foreach (var card in repos)
                    {
                        var url = card.RepoUrl;
                        var metric = Metric(card, sort);
                        var label = metric.Length == 0
                            ? $"{StatusIcon(card)}  {card.Title}"
                            : $"{StatusIcon(card)}  {card.Title}  ·  {metric}";
                        sub.Item(label, () => _ = browser.OpenAsync(url, BrowserLaunchMode.External));
                    }
                });
                menu.Separator();
                menu.Check("Mute notifications", muted,
                    isMuted => _ = notifyPrefs.SaveAsync(notifyPrefs.Current with { Muted = isMuted }));
                menu.Separator();
                menu.Item("Quit", QuitApp);
            }));

            var anyFailed = cache.All.Values.SelectMany(s => s.RecentWorkflowRuns).Any(r => r.IsFailed);
            var tooltip = anyFailed ? "GitHub Shine — failures detected" : "GitHub Shine";
            if (tooltip != lastTooltip)
            {
                trayIcon.Tooltip = tooltip;
                lastTooltip = tooltip;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to rebuild tray menu");
        }
    }

    // The same card models the Repositories page builds, so the menu's ordering and per-repo
    // metric come out of exactly one implementation (RepoGridSort.Order) rather than a
    // parallel copy that can drift. Rebuilds are debounced and content-guarded above, so the
    // per-rebuild allocation is negligible.
    List<RepoCardModel> Cards()
    {
        var cards = new List<RepoCardModel>();
        foreach (var account in config.Accounts)
        {
            foreach (var repo in account.Repos)
            {
                var card = new RepoCardModel(account, repo);
                var snap = cache.TryGet(account.Id, repo);
                if (snap is not null)
                    card.Apply(snap);
                cards.Add(card);
            }
        }
        return cards;
    }

    static string StatusIcon(RepoCardModel card)
        => card.LatestRunFailed ? "✗"
         : card.LatestRunStatus == "Running" ? "…"
         : card.LatestRunSucceeded ? "✓"
         : "•";

    // The value the menu is ordered by, so the arrangement reads as deliberate. Sorting by
    // repository name needs no metric — the name is already the label.
    static string Metric(RepoCardModel card, RepoGridSort sort) => sort.ColumnId switch
    {
        RepoGridColumns.Repository => "",
        RepoGridColumns.Account => card.AccountLabel,
        RepoGridColumns.Forks => $"⑂ {card.Forks}",
        RepoGridColumns.Watchers => $"👁 {card.Watchers}",
        RepoGridColumns.OpenIssues => $"{card.OpenIssues} issues",
        RepoGridColumns.OpenPullRequests => $"{card.OpenPullRequests} PRs",
        RepoGridColumns.Build => BuildStatusText(card),
        _ => $"★ {card.Stars}",
    };

    static string BuildStatusText(RepoCardModel card)
        => card.LatestRunFailed ? "failing"
         : card.LatestRunStatus == "Running" ? "running"
         : card.LatestRunSucceeded ? "passing"
         : "no runs";

    static void QuitApp()
    {
#if MACOS
        // Application.Quit() only closes windows, and our app overrides
        // applicationShouldTerminateAfterLastWindowClosed: to return false so it
        // lives in the tray — so Quit() alone leaves the process running. Terminate
        // the native app directly to actually exit.
        AppKit.NSApplication.SharedApplication.Terminate(null);
#elif WINDOWS
        // Close is intercepted to hide-to-tray; flag a real exit first so the window's
        // Closing handler lets it through instead of hiding again.
        GitHubShine.Platforms.Windows.TrayWindowBehavior.ForceClose = true;
        Application.Current?.Quit();
#else
        Application.Current?.Quit();
#endif
    }

    void OnPrimaryClick(object? sender, TrayClickEventArgs e)
        => ShowMainWindow();

    static void ShowMainWindow()
        => MainWindowLauncher.ShowOrCreate();

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

    public void Dispose()
    {
        if (trayIcon != null)
            trayIcon.PrimaryClick -= OnPrimaryClick;
        config.Changed -= OnConfigChanged;
        config.PreferencesChanged -= OnConfigChanged;
        notifyPrefs.Changed -= OnConfigChanged;
        foreach (var sub in subscriptions) sub.Dispose();
        subscriptions.Clear();
    }
}
#endif