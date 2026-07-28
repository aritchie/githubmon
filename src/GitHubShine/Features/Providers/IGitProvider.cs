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
/// <param name="PushedAt">
/// When the repo last received a push, straight off the repo payload both providers already
/// fetch for the counts. The sync list compares it against a mapping's last run to say whether
/// a backup is behind without spending an API call of its own.
/// </param>
public sealed record RepoStats(int OpenIssues, int Stars, int Forks, int Watchers, DateTimeOffset? PushedAt = null);

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
/// The slice of a repo's metadata the sync engine needs: where to fetch/push it
/// (<see cref="CloneUrl"/>), which branch is authoritative, and the settings copied
/// onto the target when the sync has to create it.
/// </summary>
public sealed record GitRepoInfo(
    string Owner,
    string Name,
    string? Description,
    bool Private,
    string DefaultBranch,
    string CloneUrl,
    // Last push to any branch — free here (the repo payload is already being read) and the basis
    // for the sync list's "behind" check.
    DateTimeOffset? PushedAt = null)
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

    // ---- repo-to-repo sync support (see GitHubShine.Sync.RepoSyncEngine) ----

    /// <summary>
    /// Metadata for a single repo, or <c>null</c> when it doesn't exist (or isn't visible to
    /// this token). The sync engine uses null to mean "create the target".
    /// </summary>
    Task<GitRepoInfo?> GetRepoInfoAsync(MonitoredRepo repo, CancellationToken ct = default);

    /// <summary>Branch names on the repo, for the sync mapping's branch picker.</summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(MonitoredRepo repo, CancellationToken ct = default);

    /// <summary>
    /// Creates an <b>empty</b> repo — no auto-init/README. An auto-initialised repo has a root
    /// commit the source doesn't share, which turns the first sync push into a non-fast-forward.
    /// </summary>
    Task<GitRepoInfo> CreateRepoAsync(string owner, string name, string? description, bool isPrivate, CancellationToken ct = default);

    /// <summary>Points the repo's HEAD at <paramref name="branch"/>.</summary>
    Task SetDefaultBranchAsync(MonitoredRepo repo, string branch, CancellationToken ct = default);
}
