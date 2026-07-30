using Shiny.Notifications;

namespace GitHubShine.Sync;

/// <summary>
/// Runs the configured syncs in the background on a long interval (3 hours and up — see
/// <see cref="AutoSyncPrefs"/>).
///
/// Timing is anchored to <see cref="AutoSyncPrefs.LastRunUtc"/> in the database rather than to
/// process uptime, so an app that gets opened and closed several times a day still syncs on its
/// schedule instead of either firing on every launch or never firing at all.
///
/// Modelled on <c>PollerInitializer</c>: an <see cref="IMauiInitializeService"/> owning one loop
/// task, woken early when the settings change. Desktop-only — sync shells out to the git CLI,
/// which mobile doesn't have — so it's registered under <c>!MOBILE</c> in MauiProgram.
/// </summary>
public sealed class AutoSyncRunner : IMauiInitializeService, IDisposable
{
    /// <summary>Long enough after launch for the poller to have filled the snapshot cache with
    /// the push times the "behind" check reads, so the first pass rarely needs its own requests.</summary>
    static readonly TimeSpan StartupSettle = TimeSpan.FromMinutes(2);

    /// <summary>Something transient stopped a due pass (the user was mid-run) — try again soon.</summary>
    static readonly TimeSpan ShortRetry = TimeSpan.FromMinutes(10);

    /// <summary>Something durable stopped it (no git, no accounts) — don't spin on it.</summary>
    static readonly TimeSpan LongRetry = TimeSpan.FromHours(1);

    /// <summary>Fallback re-check while auto-sync is switched off; the wake signal normally beats it.</summary>
    static readonly TimeSpan DisabledIdle = TimeSpan.FromHours(1);

    readonly IConfigStore config;
    readonly ISyncStore syncStore;
    readonly IRepoSyncEngine engine;
    readonly IGitCli git;
    readonly IGitProviderFactory factory;
    readonly SnapshotCache snapshots;
    readonly SyncGate gate;
    readonly INotificationHub notifications;
    readonly ILogger<AutoSyncRunner> logger;

    CancellationTokenSource? cts;
    Task? loop;
    DateTimeOffset? retryAfterUtc;
    volatile TaskCompletionSource wakeSignal = NewSignal();
    static int notificationIdSeed = 2000;

    public AutoSyncRunner(
        IConfigStore config,
        ISyncStore syncStore,
        IRepoSyncEngine engine,
        IGitCli git,
        IGitProviderFactory factory,
        SnapshotCache snapshots,
        SyncGate gate,
        INotificationHub notifications,
        ILogger<AutoSyncRunner> logger)
    {
        this.config = config;
        this.syncStore = syncStore;
        this.engine = engine;
        this.git = git;
        this.factory = factory;
        this.snapshots = snapshots;
        this.gate = gate;
        this.notifications = notifications;
        this.logger = logger;
    }

    static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Initialize(IServiceProvider services)
    {
        this.cts = new CancellationTokenSource();
        this.loop = Task.Run(() => this.RunAsync(this.cts.Token));
    }

    // Interval changed, or auto-sync was switched on — re-time without waiting out the sleep
    // that was already in flight (which could be hours).
    void OnAutoSyncChanged(object? sender, EventArgs e)
        => this.wakeSignal.TrySetResult();

    async Task RunAsync(CancellationToken ct)
    {
        this.syncStore.AutoSyncChanged += this.OnAutoSyncChanged;

        try { await Task.Delay(StartupSettle, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            // Arm the wake signal BEFORE reading the settings, so a change that lands while this
            // pass is running still shortens the sleep that follows it.
            this.wakeSignal = NewSignal();
            var wake = this.wakeSignal.Task;

            AutoSyncPrefs prefs;
            try
            {
                prefs = await this.syncStore.GetAutoSyncAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "[AutoSync] couldn't read the auto-sync settings");
                prefs = AutoSyncPrefs.Default with { Enabled = false };
            }

            var wait = this.NextDelay(prefs);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.WhenAny(Task.Delay(wait, ct), wake).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await this.RunPassAsync(prefs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Never let one bad pass end the loop — back off and try again later.
                this.logger.LogError(ex, "[AutoSync] pass failed");
                this.retryAfterUtc = DateTimeOffset.UtcNow + LongRetry;
            }
        }

        this.syncStore.AutoSyncChanged -= this.OnAutoSyncChanged;
    }

    /// <summary>How long until the next pass is due — zero when it's due now.</summary>
    TimeSpan NextDelay(AutoSyncPrefs prefs)
    {
        if (!prefs.Enabled)
            return DisabledIdle;

        var now = DateTimeOffset.UtcNow;
        var due = prefs.LastRunUtc is { } last ? last + prefs.Interval - now : TimeSpan.Zero;
        var retry = this.retryAfterUtc is { } at ? at - now : TimeSpan.Zero;
        var wait = due > retry ? due : retry;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    async Task RunPassAsync(AutoSyncPrefs prefs, CancellationToken ct)
    {
        try
        {
            await this.git.EnsureAvailableAsync(ct).ConfigureAwait(false);
        }
        catch (GitUnavailableException ex)
        {
            // Installing git isn't going to happen in the next few minutes — check back in an hour.
            this.logger.LogWarning("[AutoSync] skipped — {Message}", ex.Message);
            this.retryAfterUtc = DateTimeOffset.UtcNow + LongRetry;
            return;
        }

        if (this.config.Accounts.Count == 0)
            await this.config.ReloadAsync(ct).ConfigureAwait(false);
        await this.syncStore.ReloadAsync(ct).ConfigureAwait(false);

        var runnable = this.syncStore.Mappings
            .Where(m => SyncStatus.BlockingIssue(m, this.config.Accounts) is null)
            .ToList();

        if (runnable.Count == 0)
        {
            this.logger.LogDebug("[AutoSync] nothing runnable is configured");
            await this.CompletePassAsync(ct).ConfigureAwait(false);
            return;
        }

        var targets = prefs.SyncAll
            ? runnable
            : await this.BehindAsync(runnable, ct).ConfigureAwait(false);

        if (targets.Count == 0)
        {
            this.logger.LogDebug("[AutoSync] all {Count} sync(s) are up to date", runnable.Count);
            await this.CompletePassAsync(ct).ConfigureAwait(false);
            return;
        }

        // The user's own run owns the gate while it lasts; come back shortly rather than
        // interleaving two passes over the same mappings.
        using var lease = this.gate.TryAcquire(SyncRunKind.Automatic);
        if (lease is null)
        {
            this.logger.LogDebug("[AutoSync] a sync is already running — retrying in {Minutes}m", ShortRetry.TotalMinutes);
            this.retryAfterUtc = DateTimeOffset.UtcNow + ShortRetry;
            return;
        }

        this.logger.LogInformation(
            "[AutoSync] starting: {Count} of {Total} sync(s), mode={Mode}",
            targets.Count, runnable.Count, prefs.SyncAll ? "all" : "behind");

        var failures = new List<string>();
        // Sequential, like the page's batch run: syncs are network- and disk-heavy, and two
        // sharing a source would contend on the same local cache repo.
        foreach (var mapping in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await this.engine.SyncAsync(mapping, progress: null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The engine has already recorded the failure on the mapping, so the page shows it.
                this.logger.LogWarning(ex, "[AutoSync] {Source} -> {Target} failed", mapping.Source.FullName, mapping.TargetFullName);
                failures.Add(mapping.Source.FullName);
            }
        }

        this.logger.LogInformation("[AutoSync] finished: {Ok} synced, {Failed} failed", targets.Count - failures.Count, failures.Count);
        await this.NotifyFailuresAsync(failures, targets.Count).ConfigureAwait(false);
        await this.CompletePassAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Narrows to the mappings whose source has moved since their last run. Push times come from
    /// the dashboard poller's cache when it has them (free), and from one request per remaining
    /// distinct source repo otherwise — mappings sharing a source share that one request.
    /// </summary>
    async Task<List<SyncMapping>> BehindAsync(IReadOnlyList<SyncMapping> runnable, CancellationToken ct)
    {
        var behind = new List<SyncMapping>();
        var pushedAt = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in runnable)
        {
            ct.ThrowIfCancellationRequested();

            // A never-run sync doesn't need a push time to know it has work to do.
            if (mapping.LastSyncedUtc is null)
            {
                behind.Add(mapping);
                continue;
            }

            var key = $"{mapping.SourceAccountId}|{mapping.Source.FullName}";
            if (!pushedAt.TryGetValue(key, out var pushed))
            {
                pushed = this.snapshots.TryGet(mapping.SourceAccountId, mapping.Source)?.PushedAt
                    ?? await this.ReadPushedAtAsync(mapping, ct).ConfigureAwait(false);
                pushedAt[key] = pushed;
            }

            // Unknown means the repo couldn't be read at all — a sync would almost certainly fail
            // the same way, so leave it for the next pass (or for "Sync everything", which
            // doesn't consult push times).
            if (SyncStatus.Evaluate(mapping.LastSyncedUtc, pushed) == SyncFreshness.Behind)
                behind.Add(mapping);
        }

        return behind;
    }

    async Task<DateTimeOffset?> ReadPushedAtAsync(SyncMapping mapping, CancellationToken ct)
    {
        try
        {
            var account = this.config.Accounts.FirstOrDefault(a => a.Id == mapping.SourceAccountId);
            if (account is null)
                return null;

            var provider = await this.factory.CreateAsync(account, ct).ConfigureAwait(false);
            if (provider is null)
                return null;

            var info = await provider.GetRepoInfoAsync(mapping.Source, ct).ConfigureAwait(false);
            return info?.PushedAt;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One unreachable repo shouldn't abandon the pass — it just stays unknown.
            this.logger.LogDebug(ex, "[AutoSync] couldn't read {Repo}", mapping.Source.FullName);
            return null;
        }
    }

    async Task CompletePassAsync(CancellationToken ct)
    {
        this.retryAfterUtc = null;
        try
        {
            await this.syncStore.MarkAutoSyncRunAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Bookkeeping only — but without it the loop would think a pass is still due, so back
            // off explicitly rather than re-running immediately.
            this.logger.LogWarning(ex, "[AutoSync] couldn't record the run time");
            this.retryAfterUtc = DateTimeOffset.UtcNow + LongRetry;
        }
    }

    /// <summary>
    /// Only failures are worth interrupting for: a background backup that quietly stopped working
    /// is the case the user needs to hear about. Successes stay in the run log on the sync page.
    /// </summary>
    async Task NotifyFailuresAsync(IReadOnlyList<string> failures, int total)
    {
        if (failures.Count == 0)
            return;

        try
        {
            if (await this.config.GetNotificationsMutedAsync().ConfigureAwait(false))
                return;

            var detail = failures.Count <= 3
                ? string.Join(", ", failures)
                : $"{string.Join(", ", failures.Take(3))} and {failures.Count - 3} more";

            await this.notifications.SendAsync(new Notification
            {
                Id = Interlocked.Increment(ref notificationIdSeed),
                Title = $"Auto-sync: {failures.Count} of {total} failed",
                Message = detail
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "[AutoSync] couldn't post the failure notification");
        }
    }

    public void Dispose()
    {
        this.syncStore.AutoSyncChanged -= this.OnAutoSyncChanged;
        this.cts?.Cancel();
        this.cts?.Dispose();
    }
}
