using Shiny.Mediator;

namespace GitHubMon.Dashboard.Mediator;

[MediatorSingleton]
public sealed class RefreshAllHandler(IConfigStore config, SnapshotCache cache, ILogger<RefreshAllHandler> logger)
    : ICommandHandler<RefreshAllCommand>
{
    public async Task Handle(RefreshAllCommand command, IMediatorContext context, CancellationToken cancellationToken)
    {
        foreach (var account in config.Accounts)
        {
            foreach (var repo in account.Repos)
            {
                if (cancellationToken.IsCancellationRequested) return;
                try
                {
                    var snap = await context.Request(new GetRepoSnapshotRequest(account, repo), cancellationToken).ConfigureAwait(false);
                    var prev = cache.PutSnapshot(snap);
                    await context.Publish(new SnapshotUpdatedEvent(account.Id, repo, snap), cancellationToken: cancellationToken).ConfigureAwait(false);

                    foreach (var run in await cache.NewlyFailedRunsAsync(snap, cancellationToken).ConfigureAwait(false))
                        await context.Publish(new WorkflowRunFailedEvent(account.Id, repo, run), cancellationToken: cancellationToken).ConfigureAwait(false);

                    // New issue/PR detection diffs against the previous in-memory
                    // snapshot — null right after launch, so a restart never
                    // re-notifies about everything that's already open.
                    if (prev is not null && !prev.HasError && !snap.HasError)
                    {
                        var knownPrs = prev.OpenPullRequests.Select(p => p.Number).ToHashSet();
                        foreach (var pr in snap.OpenPullRequests.Where(p => !knownPrs.Contains(p.Number)))
                            await context.Publish(new NewPullRequestEvent(account.Id, repo, pr), cancellationToken: cancellationToken).ConfigureAwait(false);

                        var newIssues = snap.OpenIssues - prev.OpenIssues;
                        if (newIssues > 0)
                            await context.Publish(new NewIssuesEvent(account.Id, repo, newIssues, snap.OpenIssues), cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Refresh failed for {Repo}", repo.FullName);
                }
            }

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
    }
}
