using System.Collections.Concurrent;
using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

[MediatorSingleton]
public sealed class RefreshAllHandler(IConfigStore config, SnapshotCache cache, ILogger<RefreshAllHandler> logger)
    : ICommandHandler<RefreshAllCommand>
{
    // Force the snapshot cache to refresh (skip the read, run the handler, write the fresh result).
    static readonly Action<IMediatorContext> ForceRefresh = ctx => ctx.ForceCacheRefresh();

    // Repo keys that have had a *live* baseline established this session. New-PR / new-issue
    // notifications only fire once a key has a live baseline, so a cold-start paint (from the
    // persistent cache) or the very first live fetch never dumps a burst of "new" items.
    readonly ConcurrentDictionary<string, byte> primed = new();

    public async Task Handle(RefreshAllCommand command, IMediatorContext context, CancellationToken cancellationToken)
    {
        var force = command.Force;

        foreach (var account in config.Accounts)
        {
            foreach (var repo in account.Repos)
            {
                if (cancellationToken.IsCancellationRequested) return;
                try
                {
                    // Non-forced pass = cold-start paint: this reads the persistent cache (a hit is
                    // instant and hits no network) and just repaints the UI. Forced pass = a real
                    // live fetch, cheap in practice because unchanged data comes back as 304s.
                    var snap = await context
                        .Request(new GetRepoSnapshotRequest(account, repo), cancellationToken, force ? ForceRefresh : null)
                        .ConfigureAwait(false);

                    var prev = cache.PutSnapshot(snap);
                    await context.Publish(new SnapshotUpdatedEvent(account.Id, repo, snap), cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (!force)
                        continue; // cold-start paint only — no notifications, no baseline priming

                    // Build status. Both of these prime themselves off records in the document
                    // store rather than the in-memory `primed` set below, so they survive a
                    // restart without re-announcing and without going quiet.
                    foreach (var run in await cache.NewlyFailedRunsAsync(snap, cancellationToken).ConfigureAwait(false))
                        await context.Publish(new WorkflowRunFailedEvent(account.Id, repo, run), cancellationToken: cancellationToken).ConfigureAwait(false);

                    foreach (var run in await cache.RecoveredRunsAsync(snap, cancellationToken).ConfigureAwait(false))
                        await context.Publish(new WorkflowRunRecoveredEvent(account.Id, repo, run), cancellationToken: cancellationToken).ConfigureAwait(false);

                    // Only diff PRs/issues/stars/forks once this repo already had a live baseline
                    // from a prior forced cycle — so a restart never re-notifies about everything
                    // already open, and the first live fetch isn't read as "+400 stars".
                    var hadLiveBaseline = !primed.TryAdd(Key(account.Id, repo), 0);
                    if (hadLiveBaseline && prev is not null && !prev.HasError && !snap.HasError)
                    {
                        var knownPrs = prev.OpenPullRequests.Select(p => p.Number).ToHashSet();
                        foreach (var pr in snap.OpenPullRequests.Where(p => !knownPrs.Contains(p.Number)))
                            await context.Publish(new NewPullRequestEvent(account.Id, repo, pr), cancellationToken: cancellationToken).ConfigureAwait(false);

                        var newIssues = snap.OpenIssues - prev.OpenIssues;
                        if (newIssues > 0)
                            await context.Publish(new NewIssuesEvent(account.Id, repo, newIssues, snap.OpenIssues), cancellationToken: cancellationToken).ConfigureAwait(false);

                        // Stars and forks are counts in the repo summary the poll already fetches,
                        // so a delta is free. Only upward moves are interesting — an unstar or a
                        // deleted fork isn't worth an alert.
                        var newStars = snap.Stars - prev.Stars;
                        if (newStars > 0)
                            await context.Publish(new NewStarsEvent(account.Id, repo, newStars, snap.Stars), cancellationToken: cancellationToken).ConfigureAwait(false);

                        var newForks = snap.Forks - prev.Forks;
                        if (newForks > 0)
                            await context.Publish(new NewForksEvent(account.Id, repo, newForks, snap.Forks), cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Refresh failed for {Repo}", repo.FullName);

                    // First-load failure with nothing cached yet → surface an error card. Otherwise
                    // leave the last good snapshot on screen rather than flashing an error.
                    if (cache.TryGet(account.Id, repo) is null)
                    {
                        var errSnap = RepoSnapshot.Empty(account.Id, repo, ex.Message);
                        cache.PutSnapshot(errSnap);
                        await context.Publish(new SnapshotUpdatedEvent(account.Id, repo, errSnap), cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // Inbox isn't cached, so only fetch it on a live (forced) cycle — the cold-start paint
            // stays network-free and the next forced cycle (moments later) fills the inbox in.
            if (!force)
                continue;

            try
            {
                var items = await context.Request(new ListInboxRequest(account), cancellationToken).ConfigureAwait(false);
                var fresh = await cache.RecordInboxAsync(account.Id, items, cancellationToken).ConfigureAwait(false);
                await context.Publish(new InboxUpdatedEvent(account.Id, items), cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var item in fresh)
                    await context.Publish(new NewInboxItemEvent(account.Id, item), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Inbox refresh failed for {Account}", account.Label);
            }
        }

        // Followed people ride the same cycle: every caller of RefreshAllCommand — the poll job,
        // the cold-start paint, the tray, the page refresh buttons — means "bring everything up to
        // date", and hanging this off the one command keeps that true without teaching each caller
        // about a second one. Its own failures are contained by RefreshPersonsHandler, so a person
        // that can't be read never takes the repo refresh down with it.
        try
        {
            await context.Send(new RefreshPersonsCommand(force), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "People refresh failed");
        }
    }

    static string Key(string accountId, MonitoredRepo repo) => $"{accountId}|{repo.FullName}";
}
