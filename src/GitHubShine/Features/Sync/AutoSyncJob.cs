using Shiny.Jobs;
using Shiny.Notifications;

namespace GitHubShine.Sync;

/// <summary>
/// Runs the configured git mirrors in the background — one pass of what <c>AutoSyncRunner</c>
/// used to loop.
///
/// Timing was already anchored to <see cref="AutoSyncPrefs.LastRunUtc"/> in the database rather
/// than to process uptime, so the move from a loop to a job barely changes it: the job is invoked
/// on the platform's cadence and this pass decides whether one is actually due. What did have to
/// change is the retry back-off, which used to be a field — see
/// <see cref="AutoSyncPrefs.RetryAfterUtc"/>.
///
/// Still desktop-only, though no longer for want of git: syncing is a desktop workflow (two
/// accounts and a mapping list, none of which the phone UI offers), so it stays registered under
/// <c>!MOBILE</c> in MauiProgram and on a phone this job doesn't exist.
/// </summary>
public sealed class AutoSyncJob(
    IConfigStore config,
    ISyncStore syncStore,
    IRepoSyncEngine engine,
    IGitRuntime git,
    IGitProviderFactory factory,
    SnapshotCache snapshots,
    SyncGate gate,
    INotificationHub notifications,
    INotificationPrefsStore notifyPrefs,
    ILogger<AutoSyncJob> logger) : IJob
{
    /// <summary>Something transient stopped a due pass (the user was mid-run) — try again soon.</summary>
    static readonly TimeSpan ShortRetry = TimeSpan.FromMinutes(10);

    /// <summary>Something durable stopped it (no git, no accounts) — don't spin on it.</summary>
    static readonly TimeSpan LongRetry = TimeSpan.FromHours(1);

    static int notificationIdSeed = 2000;

    public async Task Run(CancellationToken cancelToken)
    {
        var prefs = await syncStore.GetAutoSyncAsync(cancelToken).ConfigureAwait(false);

        if (!prefs.Enabled)
        {
            logger.LogDebug("[AutoSync] skipped — disabled");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (prefs.RetryAfterUtc is { } retryAt && retryAt > now)
        {
            logger.LogDebug("[AutoSync] skipped — backing off until {At:u}", retryAt);
            return;
        }

        if (prefs.LastRunUtc is { } last)
        {
            var elapsed = now - last;
            // Negative means the clock moved backwards; treat it as due rather than wedging.
            if (elapsed >= TimeSpan.Zero && elapsed < prefs.Interval)
            {
                logger.LogDebug(
                    "[AutoSync] skipped — last run {Hours:F1}h ago, interval {Interval:F0}h",
                    elapsed.TotalHours, prefs.Interval.TotalHours);
                return;
            }
        }

        try
        {
            await this.RunPassAsync(prefs, cancelToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let one bad pass poison the schedule — back off and try again later.
            logger.LogError(ex, "[AutoSync] pass failed");
            await this.SetRetryAsync(LongRetry, cancelToken).ConfigureAwait(false);
        }
    }

    async Task RunPassAsync(AutoSyncPrefs prefs, CancellationToken ct)
    {
        try
        {
            await git.EnsureAvailableAsync(ct).ConfigureAwait(false);
        }
        catch (GitUnavailableException ex)
        {
            // A native that won't load won't start loading in the next few minutes — check back in
            // an hour rather than spinning on it.
            logger.LogWarning("[AutoSync] skipped — {Message}", ex.Message);
            await this.SetRetryAsync(LongRetry, ct).ConfigureAwait(false);
            return;
        }

        if (config.Accounts.Count == 0)
            await config.ReloadAsync(ct).ConfigureAwait(false);
        await syncStore.ReloadAsync(ct).ConfigureAwait(false);

        // Per-mapping opt-out first: a sync with auto-sync unticked is still perfectly runnable,
        // it just never runs unattended — the Sync page's buttons remain the only way to move it.
        var runnable = syncStore.Mappings
            .Where(m => m.AutoSync && SyncStatus.BlockingIssue(m, config.Accounts) is null)
            .ToList();

        if (runnable.Count == 0)
        {
            logger.LogDebug("[AutoSync] nothing runnable is configured");
            await this.CompletePassAsync(ct).ConfigureAwait(false);
            return;
        }

        var targets = prefs.SyncAll
            ? runnable
            : await this.BehindAsync(runnable, ct).ConfigureAwait(false);

        if (targets.Count == 0)
        {
            logger.LogDebug("[AutoSync] all {Count} sync(s) are up to date", runnable.Count);
            await this.CompletePassAsync(ct).ConfigureAwait(false);
            return;
        }

        // The user's own run owns the gate while it lasts; come back shortly rather than
        // interleaving two passes over the same mappings.
        using var lease = gate.TryAcquire(SyncRunKind.Automatic);
        if (lease is null)
        {
            logger.LogDebug("[AutoSync] a sync is already running — retrying in {Minutes}m", ShortRetry.TotalMinutes);
            await this.SetRetryAsync(ShortRetry, ct).ConfigureAwait(false);
            return;
        }

        logger.LogInformation(
            "[AutoSync] starting: {Count} of {Total} sync(s), mode={Mode}",
            targets.Count, runnable.Count, prefs.SyncAll ? "all" : "behind");

        var failures = new List<string>();
        // Sequential, like the page's batch run: syncs are network- and disk-heavy, and two
        // sharing a source would contend on the same local cache repo.
        var done = 0;
        foreach (var mapping in targets)
        {
            ct.ThrowIfCancellationRequested();

            // Re-read the switch between mappings so "Pause auto-sync" stops a pass that's
            // already under way, instead of only skipping the next one. One indexed row read per
            // mapping, against a sync that just did a fetch and a push — not worth optimising.
            if (!await this.StillEnabledAsync(ct).ConfigureAwait(false))
            {
                logger.LogInformation("[AutoSync] paused mid-pass — stopping after {Done} of {Total}", done, targets.Count);
                break;
            }

            try
            {
                await engine.SyncAsync(mapping, progress: null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The engine has already recorded the failure on the mapping, so the page shows it.
                logger.LogWarning(ex, "[AutoSync] {Source} -> {Target} failed", mapping.Source.FullName, mapping.TargetFullName);
                failures.Add(mapping.Source.FullName);
            }
            done++;
        }

        logger.LogInformation("[AutoSync] finished: {Ok} synced, {Failed} failed", done - failures.Count, failures.Count);
        await this.NotifyFailuresAsync(failures, done).ConfigureAwait(false);
        await this.CompletePassAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether auto-sync is still switched on. Failing open (true) on a read error: a hiccup
    /// reading one row shouldn't silently abandon a pass that's already doing useful work.
    /// </summary>
    async Task<bool> StillEnabledAsync(CancellationToken ct)
    {
        try
        {
            return (await syncStore.GetAutoSyncAsync(ct).ConfigureAwait(false)).Enabled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[AutoSync] couldn't re-read the enabled flag mid-pass");
            return true;
        }
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
                pushed = snapshots.TryGet(mapping.SourceAccountId, mapping.Source)?.PushedAt
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
            var account = config.Accounts.FirstOrDefault(a => a.Id == mapping.SourceAccountId);
            if (account is null)
                return null;

            var provider = await factory.CreateAsync(account, ct).ConfigureAwait(false);
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
            logger.LogDebug(ex, "[AutoSync] couldn't read {Repo}", mapping.Source.FullName);
            return null;
        }
    }

    async Task CompletePassAsync(CancellationToken ct)
    {
        try
        {
            await syncStore.MarkAutoSyncRunAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            await this.SetRetryAsync(null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Bookkeeping only — but without it the next invocation would think a pass is still
            // due, so back off explicitly rather than re-running immediately.
            logger.LogWarning(ex, "[AutoSync] couldn't record the run time");
            await this.SetRetryAsync(LongRetry, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Persists (or clears) the back-off deadline.</summary>
    async Task SetRetryAsync(TimeSpan? delay, CancellationToken ct)
    {
        try
        {
            // Re-read rather than reusing the pass's copy: MarkAutoSyncRunAsync may have written
            // LastRunUtc in between, and this must not roll that back.
            var current = await syncStore.GetAutoSyncAsync(ct).ConfigureAwait(false);
            var at = delay is { } d ? DateTimeOffset.UtcNow + d : (DateTimeOffset?)null;
            await syncStore
                .SaveAutoSyncAsync(current with { RetryAfterUtc = at }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[AutoSync] couldn't record the retry deadline");
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
            // Auto-sync failures have no category of their own — they're the one alert that
            // isn't about GitHub activity — so they honour the global mute and nothing else.
            if ((await notifyPrefs.GetAsync().ConfigureAwait(false)).Muted)
                return;

            var detail = failures.Count <= 3
                ? string.Join(", ", failures)
                : $"{string.Join(", ", failures.Take(3))} and {failures.Count - 3} more";

            await notifications.SendAsync(new Notification
            {
                Id = Interlocked.Increment(ref notificationIdSeed),
                Title = $"Auto-sync: {failures.Count} of {total} failed",
                Message = detail
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[AutoSync] couldn't post the failure notification");
        }
    }
}
