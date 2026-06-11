using System.Reflection;
using Shiny.Maui.Controls.Desktop.TrayIcon;
using Shiny.Mediator;

namespace GitHubMon.Tray;

public sealed class TrayIconHost(ITrayIconFactory factory,
                                 IConfigStore config,
                                 SnapshotCache cache,
                                 IMediator mediator,
                                 IExternalBrowser browser,
                                 ILogger<TrayIconHost> logger)
    : IMauiInitializeService, IDisposable
{
    ITrayIcon? trayIcon;
    readonly List<IDisposable> subscriptions = new();
    int rebuildPending;

    public void Initialize(IServiceProvider services)
    {
        try
        {
            trayIcon = factory.Create();
            trayIcon.Tooltip = "GitHubMon";
            trayIcon.IsTemplateImage = true;
            trayIcon.SetIcon(() => OpenIconStream("trayicon.png"));
            RebuildMenu();

            // Accounts and the repo-sort preference both change the repo submenu;
            // snapshots change the per-repo counts, build state, and ordering.
            config.Changed += OnConfigChanged;
            config.PreferencesChanged += OnConfigChanged;
            subscriptions.Add(mediator.Subscribe<SnapshotUpdatedEvent>((_, _, _) => { RequestMenuRebuild(); return Task.CompletedTask; }));
            subscriptions.Add(mediator.Subscribe<InboxUpdatedEvent>((_, _, _) => { RequestMenuRebuild(); return Task.CompletedTask; }));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tray icon unavailable on this platform");
        }
    }

    void OnConfigChanged(object? sender, EventArgs e) => RebuildMenu();

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
            var muted = config.GetNotificationsMutedAsync().GetAwaiter().GetResult();
            var sort = config.GetRepoSortAsync().GetAwaiter().GetResult();
            var repos = OrderedRepos(sort);

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
                    foreach (var (account, repo, snap) in repos)
                    {
                        var url = $"https://github.com/{repo.FullName}";
                        sub.Item($"{StatusIcon(snap)}  {repo.FullName}  ·  {Metric(snap, sort)}", () => browser.Open(url));
                    }
                });
                menu.Separator();
                menu.Check("Mute notifications", muted, isMuted => _ = config.SetNotificationsMutedAsync(isMuted));
                menu.Separator();
                menu.Item("Quit", QuitApp);
            }));

            var anyFailed = cache.All.Values.SelectMany(s => s.RecentWorkflowRuns).Any(r => r.IsFailed);
            trayIcon.Tooltip = anyFailed ? "GitHubMon — failures detected" : "GitHubMon";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to rebuild tray menu");
        }
    }

    // Mirrors the dashboard sidebar ordering so the tray shows the same arrangement.
    List<(MonitoredAccount Account, MonitoredRepo Repo, RepoSnapshot? Snapshot)> OrderedRepos(RepoSort sort)
    {
        var items = config.Accounts
            .SelectMany(a => a.Repos.Select(r => (Account: a, Repo: r, Snapshot: cache.TryGet(a.Id, r))));

        var ordered = sort switch
        {
            RepoSort.Forks => items.OrderByDescending(x => x.Snapshot?.Forks ?? 0).ThenBy(x => x.Repo.FullName),
            RepoSort.OpenIssues => items.OrderByDescending(x => x.Snapshot?.OpenIssues ?? 0).ThenBy(x => x.Repo.FullName),
            RepoSort.OpenPullRequests => items.OrderByDescending(x => x.Snapshot?.OpenPullRequests.Count ?? 0).ThenBy(x => x.Repo.FullName),
            RepoSort.BuildState => items.OrderByDescending(x => BuildRank(x.Snapshot)).ThenByDescending(x => x.Snapshot?.Stars ?? 0).ThenBy(x => x.Repo.FullName),
            _ => items.OrderByDescending(x => x.Snapshot?.Stars ?? 0).ThenBy(x => x.Repo.FullName),
        };
        return ordered.ToList();
    }

    // Failing builds sort to the top, then running, passing, and unknown — matches the sidebar.
    static int BuildRank(RepoSnapshot? snap)
    {
        var head = snap?.RecentWorkflowRuns.FirstOrDefault();
        if (head is null) return 0;
        return head.IsFailed ? 3 : head.IsInProgress ? 2 : head.IsSuccess ? 1 : 0;
    }

    static string StatusIcon(RepoSnapshot? snap)
    {
        var head = snap?.RecentWorkflowRuns.FirstOrDefault();
        if (head is null) return "•";
        return head.IsFailed ? "✗" : head.IsInProgress ? "…" : head.IsSuccess ? "✓" : "•";
    }

    static string Metric(RepoSnapshot? snap, RepoSort sort) => sort switch
    {
        RepoSort.Forks => $"⑂ {snap?.Forks ?? 0}",
        RepoSort.OpenIssues => $"{snap?.OpenIssues ?? 0} issues",
        RepoSort.OpenPullRequests => $"{snap?.OpenPullRequests.Count ?? 0} PRs",
        RepoSort.BuildState => BuildStatusText(snap),
        _ => $"★ {snap?.Stars ?? 0}",
    };

    static string BuildStatusText(RepoSnapshot? snap)
    {
        var head = snap?.RecentWorkflowRuns.FirstOrDefault();
        if (head is null) return "no runs";
        return head.IsFailed ? "failing" : head.IsInProgress ? "running" : head.IsSuccess ? "passing" : "—";
    }

    static void QuitApp()
    {
#if MACOS
        // Application.Quit() only closes windows, and our app overrides
        // applicationShouldTerminateAfterLastWindowClosed: to return false so it
        // lives in the tray — so Quit() alone leaves the process running. Terminate
        // the native app directly to actually exit.
        AppKit.NSApplication.SharedApplication.Terminate(null);
#else
        Application.Current?.Quit();
#endif
    }

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
        config.Changed -= OnConfigChanged;
        config.PreferencesChanged -= OnConfigChanged;
        foreach (var sub in subscriptions) sub.Dispose();
        subscriptions.Clear();
    }
}
