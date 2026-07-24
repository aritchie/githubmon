namespace GitHubShine.Dashboard.Mediator;

// partial is required by Shiny.Mediator's source generator whenever a middleware attribute
// ([Cache]) is present on the handler method.
[MediatorSingleton]
public sealed partial class GetRepoSnapshotHandler(IGitProviderFactory factory, ILogger<GetRepoSnapshotHandler> logger)
    : IRequestHandler<GetRepoSnapshotRequest, RepoSnapshot>
{
    // Persist snapshots (AddMauiPersistentCache) so a cold start paints last-known data instantly
    // instead of re-fetching everything. Types are registered for serialization in
    // GitHubShineCacheJsonContext. The poller forces a refresh every cycle (see RefreshAllHandler /
    // PollerInitializer), so this cache is only *read* on the cold-start pass; steady-state polling
    // writes fresh data straight through it.
    [Cache(AbsoluteExpirationSeconds = 60 * 60 * 24)]
    public async Task<RepoSnapshot> Handle(GetRepoSnapshotRequest request, IMediatorContext context, CancellationToken cancellationToken)
    {
        // This log only appears on a cache miss or forced refresh — once [Cache] is re-enabled, a
        // persistent-cache hit is served by the middleware without invoking the handler at all.
        logger.LogDebug("[Snapshot] live fetch {Repo}", request.Repo.FullName);

        var provider = await factory.CreateAsync(request.Account, cancellationToken).ConfigureAwait(false);
        if (provider is null)
            return RepoSnapshot.Empty(request.Account.Id, request.Repo, "No token configured");

        // Deliberately no try/catch: the cache middleware never persists a thrown result, so a
        // transient outage can't poison the 24h snapshot cache. RefreshAllHandler catches, logs,
        // and decides whether to show an error card or keep the last good snapshot on screen.
        var data = await provider.GetRepoSnapshotAsync(request.Repo, 10, cancellationToken).ConfigureAwait(false);

        return new RepoSnapshot(
            request.Account.Id,
            request.Repo,
            DateTimeOffset.UtcNow,
            data.Stats.OpenIssues,
            data.Stats.Stars,
            data.Stats.Forks,
            data.Stats.Watchers,
            data.OpenPullRequests,
            data.RecentWorkflowRuns,
            null);
    }
}
