using Octokit;

namespace GitHubShine.Providers;

/// <summary>
/// GitHub-backed provider. Wraps a single Octokit <see cref="GitHubClient"/>; the
/// calls here are the ones previously inlined in the snapshot/inbox handlers, the
/// repo archiver, and the account-edit validate flow. A non-null <c>hostUrl</c>
/// targets a GitHub Enterprise server; null uses github.com.
/// </summary>
public sealed class GitHubProvider : IGitProvider
{
    static readonly ProductHeaderValue Product = new("GitHubShine", "1.0");

    readonly GitHubClient client;
    readonly string accountId;

    public GitHubProvider(string? hostUrl, string token, string accountId)
    {
        this.accountId = accountId;
        this.client = string.IsNullOrWhiteSpace(hostUrl)
            ? new GitHubClient(Product)
            : new GitHubClient(Product, new Uri(hostUrl));
        this.client.Credentials = new Credentials(token);
    }

    public async Task<RepoStats> GetRepoStatsAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        // Fetch the repo and the open-PR count in parallel; GitHub's OpenIssuesCount
        // includes PRs, so subtract them to get a true issue count (as the old handler did).
        var repoTask = client.Repository.Get(repo.Owner, repo.Name);
        var prsTask = client.PullRequest.GetAllForRepository(repo.Owner, repo.Name, new PullRequestRequest
        {
            State = ItemStateFilter.Open
        });
        await Task.WhenAll(repoTask, prsTask).ConfigureAwait(false);

        var r = repoTask.Result;
        return new RepoStats(
            Math.Max(0, r.OpenIssuesCount - prsTask.Result.Count),
            r.StargazersCount,
            r.ForksCount,
            r.SubscribersCount);
    }

    public async Task<IReadOnlyList<PullRequestSummary>> GetOpenPullRequestsAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        var prs = await client.PullRequest
            .GetAllForRepository(repo.Owner, repo.Name, new PullRequestRequest { State = ItemStateFilter.Open })
            .ConfigureAwait(false);

        return prs
            .Select(p => new PullRequestSummary(
                p.Number,
                p.Title,
                p.User?.Login ?? "?",
                p.HtmlUrl,
                p.Draft,
                p.Mergeable ?? false))
            .ToArray();
    }

    public async Task<IReadOnlyList<WorkflowRunSummary>> GetRecentWorkflowRunsAsync(MonitoredRepo repo, int count, CancellationToken ct = default)
    {
        var runs = await client.Actions.Workflows.Runs
            .List(repo.Owner, repo.Name, new WorkflowRunsRequest(), new ApiOptions { PageSize = count, PageCount = 1 })
            .ConfigureAwait(false);

        return runs.WorkflowRuns
            .Select(r => new WorkflowRunSummary(
                r.Id,
                r.Name ?? "(workflow)",
                r.HeadBranch ?? "?",
                r.Status.ToString() ?? "unknown",
                r.Conclusion?.ToString(),
                r.CreatedAt,
                r.HtmlUrl ?? string.Empty))
            .ToArray();
    }

    public async Task<IReadOnlyList<InboxItem>> GetInboxAsync(CancellationToken ct = default)
    {
        var notifications = await client.Activity.Notifications
            .GetAllForCurrent(new NotificationsRequest { All = false, Participating = true })
            .ConfigureAwait(false);

        return notifications
            .Select(n => new InboxItem(
                accountId,
                n.Id,
                n.Repository?.FullName ?? "?",
                n.Subject?.Title ?? "(no title)",
                MapReason(n.Reason),
                n.Subject?.Type ?? "",
                ParseDate(n.UpdatedAt),
                n.Subject?.Url,
                n.Unread))
            .ToArray();
    }

    public async Task<Stream> DownloadArchiveAsync(MonitoredRepo repo, CancellationToken ct = default)
    {
        // An empty reference makes GitHub serve the repo's default branch.
        var bytes = await client.Repository.Content
            .GetArchive(repo.Owner, repo.Name, ArchiveFormat.Zipball, reference: string.Empty, timeout: TimeSpan.FromMinutes(10))
            .ConfigureAwait(false);
        return new MemoryStream(bytes, writable: false);
    }

    public async Task<GitUserInfo> ValidateAndGetUserAsync(CancellationToken ct = default)
    {
        var user = await client.User.Current().ConfigureAwait(false);
        return new GitUserInfo(user.Login);
    }

    public async Task<IReadOnlyList<AccessibleRepo>> ListAccessibleReposAsync(CancellationToken ct = default)
    {
        var fetched = await client.Repository
            .GetAllForCurrent(new ApiOptions { PageSize = 100, PageCount = 5, StartPage = 1 })
            .ConfigureAwait(false);

        return fetched
            .Select(r => new AccessibleRepo(r.Owner.Login, r.Name, r.Description, r.Private))
            .ToArray();
    }

    static DateTimeOffset ParseDate(string? s)
        => DateTimeOffset.TryParse(s, out var d) ? d : DateTimeOffset.UtcNow;

    static InboxReason MapReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        "mention" => InboxReason.Mention,
        "assign" => InboxReason.Assigned,
        "review_requested" => InboxReason.ReviewRequested,
        "author" => InboxReason.Author,
        "comment" => InboxReason.Comment,
        "subscribed" => InboxReason.Subscribed,
        _ => InboxReason.Other
    };
}
