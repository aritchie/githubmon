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
    Task<RepoStats> GetRepoStatsAsync(MonitoredRepo repo, CancellationToken ct = default);
    Task<IReadOnlyList<PullRequestSummary>> GetOpenPullRequestsAsync(MonitoredRepo repo, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowRunSummary>> GetRecentWorkflowRunsAsync(MonitoredRepo repo, int count, CancellationToken ct = default);
    Task<IReadOnlyList<InboxItem>> GetInboxAsync(CancellationToken ct = default);
    Task<Stream> DownloadArchiveAsync(MonitoredRepo repo, CancellationToken ct = default);
    Task<GitUserInfo> ValidateAndGetUserAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AccessibleRepo>> ListAccessibleReposAsync(CancellationToken ct = default);
}
