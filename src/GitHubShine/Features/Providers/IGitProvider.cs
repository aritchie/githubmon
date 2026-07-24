namespace GitHubShine.Providers;

/// <summary>
/// The git hosting backend an account talks to. Persisted on <see cref="MonitoredAccount"/>.
/// </summary>
public enum GitProviderType
{
    GitHub,
    Gitea
}

/// <summary>
/// The handful of repo counters the dashboard shows. Each provider computes
/// <see cref="OpenIssues"/> in its own way (GitHub derives it from the issues
/// count minus PRs; Gitea exposes a dedicated PR counter).
/// </summary>
public sealed record RepoStats(int OpenIssues, int Stars, int Forks, int Watchers);

public sealed record GitUserInfo(string Login);

/// <summary>
/// Everything a single repo tile needs, fetched together so a provider can minimise round-trips
/// (GitHub reuses the open-PR list for both the issue-count math and the PR summaries instead of
/// requesting it twice).
/// </summary>
public sealed record RepoSnapshotData(
    RepoStats Stats,
    IReadOnlyList<PullRequestSummary> OpenPullRequests,
    IReadOnlyList<WorkflowRunSummary> RecentWorkflowRuns);

/// <summary>
/// A repo the current token can access, used to populate the account-edit picker.
/// </summary>
public sealed record AccessibleRepo(string Owner, string Name, string? Description, bool Private)
{
    public string FullName => $"{Owner}/{Name}";
}

/// <summary>
/// Backend-agnostic view of a single account's git host. One instance is bound to
/// one account (host + token + id) and is cached per-account by
/// <see cref="IGitProviderFactory"/>. Implementations: GitHubProvider (Octokit),
/// GiteaProvider (REST /api/v1).
/// </summary>
public interface IGitProvider
{
    /// <summary>
    /// Fetches the stats, open PRs and recent workflow runs for a repo in as few calls as the
    /// backend allows (see <see cref="RepoSnapshotData"/>).
    /// </summary>
    Task<RepoSnapshotData> GetRepoSnapshotAsync(MonitoredRepo repo, int runCount, CancellationToken ct = default);
    Task<IReadOnlyList<InboxItem>> GetInboxAsync(CancellationToken ct = default);
    Task<Stream> DownloadArchiveAsync(MonitoredRepo repo, CancellationToken ct = default);
    Task<GitUserInfo> ValidateAndGetUserAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AccessibleRepo>> ListAccessibleReposAsync(CancellationToken ct = default);
}
