using Octokit;
using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

[MediatorSingleton]
public sealed class GetRepoSnapshotHandler(IGitHubClientFactory factory, ILogger<GetRepoSnapshotHandler> logger)
    : IRequestHandler<GetRepoSnapshotRequest, RepoSnapshot>
{
    public async Task<RepoSnapshot> Handle(GetRepoSnapshotRequest request, IMediatorContext context, CancellationToken cancellationToken)
    {
        var client = await factory.CreateAsync(request.Account.Id, cancellationToken).ConfigureAwait(false);
        if (client is null)
            return RepoSnapshot.Empty(request.Account.Id, request.Repo, "No token configured");

        try
        {
            var owner = request.Repo.Owner;
            var name = request.Repo.Name;

            var repoTask = client.Repository.Get(owner, name);
            var prsTask = client.PullRequest.GetAllForRepository(owner, name, new PullRequestRequest
            {
                State = ItemStateFilter.Open
            });
            var runsTask = client.Actions.Workflows.Runs.List(owner, name, new WorkflowRunsRequest(), new ApiOptions { PageSize = 10, PageCount = 1 });

            await Task.WhenAll(repoTask, prsTask, runsTask).ConfigureAwait(false);

            var repo = repoTask.Result;
            var prs = prsTask.Result
                .Select(p => new PullRequestSummary(
                    p.Number,
                    p.Title,
                    p.User?.Login ?? "?",
                    p.HtmlUrl,
                    p.Draft,
                    p.Mergeable ?? false))
                .ToArray();

            var runs = runsTask.Result.WorkflowRuns
                .Select(r => new WorkflowRunSummary(
                    r.Id,
                    r.Name ?? "(workflow)",
                    r.HeadBranch ?? "?",
                    r.Status.ToString() ?? "unknown",
                    r.Conclusion?.ToString(),
                    r.CreatedAt,
                    r.HtmlUrl ?? string.Empty))
                .ToArray();

            return new RepoSnapshot(
                request.Account.Id,
                request.Repo,
                DateTimeOffset.UtcNow,
                Math.Max(0, repo.OpenIssuesCount - prs.Length),
                repo.StargazersCount,
                repo.ForksCount,
                repo.SubscribersCount,
                prs,
                runs,
                null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed snapshot for {Repo}", request.Repo.FullName);
            return RepoSnapshot.Empty(request.Account.Id, request.Repo, ex.Message);
        }
    }
}
