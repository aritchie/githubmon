using Octokit;
using Octokit.Internal;

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

    public GitHubProvider(string? hostUrl, string token, string accountId, RateLimitMonitor rateLimits, ILogger etagLogger)
    {
        this.accountId = accountId;

        // Build the Octokit connection over a ConditionalGetHandler so every request carries
        // If-None-Match and unchanged resources come back as free 304s (see the handler for why
        // that matters to the rate limit). Octokit has no built-in ETag support, so we own the
        // HTTP pipeline here rather than using the convenience GitHubClient constructors.
        var baseAddress = string.IsNullOrWhiteSpace(hostUrl) ? GitHubClient.GitHubApiUrl : new Uri(hostUrl);
        var handler = new ConditionalGetHandler(new HttpClientHandler(), rateLimits, etagLogger);
        var httpClient = new HttpClientAdapter(() => handler);
        var connection = new Connection(
            Product,
            baseAddress,
            new InMemoryCredentialStore(new Credentials(token)),
            httpClient,
            new SimpleJsonSerializer());

        this.client = new GitHubClient(connection);
    }

    public async Task<RepoSnapshotData> GetRepoSnapshotAsync(MonitoredRepo repo, int runCount, CancellationToken ct = default)
    {
        // One fan-out of three calls: repo, open PRs, recent runs. The PR list is reused for both
        // the issue-count math (GitHub's OpenIssuesCount includes PRs, so subtract them) and the
        // PR summaries — that's one fewer call per repo per poll than fetching PRs twice.
        var repoTask = client.Repository.Get(repo.Owner, repo.Name);
        var prsTask = client.PullRequest.GetAllForRepository(repo.Owner, repo.Name, new PullRequestRequest
        {
            State = ItemStateFilter.Open
        });
        var runsTask = client.Actions.Workflows.Runs
            .List(repo.Owner, repo.Name, new WorkflowRunsRequest(), new ApiOptions { PageSize = runCount, PageCount = 1 });
        await Task.WhenAll(repoTask, prsTask, runsTask).ConfigureAwait(false);

        var r = repoTask.Result;
        var prs = prsTask.Result;

        var stats = new RepoStats(
            Math.Max(0, r.OpenIssuesCount - prs.Count),
            r.StargazersCount,
            r.ForksCount,
            r.SubscribersCount);

        var prSummaries = prs
            .Select(p => new PullRequestSummary(
                p.Number,
                p.Title,
                p.User?.Login ?? "?",
                p.HtmlUrl,
                p.Draft,
                p.Mergeable ?? false))
            .ToArray();

        var runSummaries = runsTask.Result.WorkflowRuns
            .Select(run => new WorkflowRunSummary(
                run.Id,
                run.Name ?? "(workflow)",
                run.HeadBranch ?? "?",
                run.Status.ToString() ?? "unknown",
                run.Conclusion?.ToString(),
                run.CreatedAt,
                run.HtmlUrl ?? string.Empty))
            .ToArray();

        return new RepoSnapshotData(stats, prSummaries, runSummaries);
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
