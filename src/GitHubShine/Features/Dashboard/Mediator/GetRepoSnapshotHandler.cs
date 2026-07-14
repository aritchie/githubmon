namespace GitHubShine.Dashboard.Mediator;

[MediatorSingleton]
public sealed class GetRepoSnapshotHandler(IGitProviderFactory factory, ILogger<GetRepoSnapshotHandler> logger)
    : IRequestHandler<GetRepoSnapshotRequest, RepoSnapshot>
{

    // [Cache(AbsoluteExpirationSeconds = 60 * 60 + 24)]
    public async Task<RepoSnapshot> Handle(GetRepoSnapshotRequest request, IMediatorContext context, CancellationToken cancellationToken)
    {
        var provider = await factory.CreateAsync(request.Account, cancellationToken).ConfigureAwait(false);
        if (provider is null)
            return RepoSnapshot.Empty(request.Account.Id, request.Repo, "No token configured");

        try
        {
            var repo = request.Repo;

            var statsTask = provider.GetRepoStatsAsync(repo, cancellationToken);
            var prsTask = provider.GetOpenPullRequestsAsync(repo, cancellationToken);
            var runsTask = provider.GetRecentWorkflowRunsAsync(repo, 10, cancellationToken);

            await Task.WhenAll(statsTask, prsTask, runsTask).ConfigureAwait(false);

            var stats = statsTask.Result;

            return new RepoSnapshot(
                request.Account.Id,
                request.Repo,
                DateTimeOffset.UtcNow,
                stats.OpenIssues,
                stats.Stars,
                stats.Forks,
                stats.Watchers,
                prsTask.Result,
                runsTask.Result,
                null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed snapshot for {Repo}", request.Repo.FullName);
            return RepoSnapshot.Empty(request.Account.Id, request.Repo, ex.Message);
        }
    }
}
