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
/// <param name="Private">
/// Whether the repo is private. Also free off the repo payload, and the only place the dashboard
/// can learn a monitored repo's visibility — the saved <see cref="MonitoredRepo"/> is just owner/name.
/// </param>
public sealed record RepoStats(int OpenIssues, int Stars, int Forks, int Watchers, DateTimeOffset? PushedAt = null, bool Private = false);

public sealed record GitUserInfo(string Login);

/// <summary>
/// Whether a login names a person or an organisation. Both providers model an org as a
/// kind of account, so the two share one lookup path and differ only in which extra
/// counters make sense (members for an org, followers/following for a person).
/// </summary>
public enum GitPersonKind
{
    User,
    Organization
}

/// <summary>
/// A person or organisation's profile, as far as a single account lookup exposes it.
/// Every counter is nullable because the two backends publish different subsets — Gitea has
/// no gists at all, and neither backend reports followers meaningfully for an organisation —
/// and a null renders as "—" rather than a misleading zero.
/// </summary>
public sealed record GitPersonProfile(
    string Login,
    GitPersonKind Kind,
    string? Name,
    string? AvatarUrl,
    string? Bio,
    string? Location,
    string? Company,
    string? WebsiteUrl,
    int? Followers,
    int? Following,
    int? PublicRepos,
    int? PublicGists,
    DateTimeOffset? JoinedAt);

/// <summary>
/// Everything one row of the people grid needs, fetched together so a provider can share
/// work between the counters (the repo enumeration that sums <see cref="TotalStars"/> also
/// yields the repo count on backends whose profile payload omits it).
/// </summary>
/// <param name="TotalStars">
/// Stars summed over every repo the account's token can see for this login. There is no
/// single-call equivalent on either backend, so this costs a paged repo listing — bounded by
/// <see cref="GitPersonLimits.MaxRepos"/>, which is why it can under-report for a login
/// with a very large repo set.
/// </param>
/// <param name="MemberCount">
/// Organisation members visible to the token; null for a person, and also null when the token
/// lacks the scope to read the member list (that is a permission gap, not "no members").
/// </param>
/// <param name="StarsTruncated">
/// True when the repo listing stopped at the cap, so <see cref="TotalStars"/> is a floor rather
/// than a total. The UI shows it as "n+" — a capped number rendered as exact is a lie, and these
/// caps are hit by any organisation of real size.
/// </param>
/// <param name="MembersTruncated">As <paramref name="StarsTruncated"/>, for the member count.</param>
public sealed record GitPersonSnapshotData(
    GitPersonProfile Profile,
    int TotalStars,
    int? MemberCount,
    bool StarsTruncated = false,
    bool MembersTruncated = false);

/// <summary>
/// Ceilings for the paged person lookups, expressed as item counts so both providers truncate at
/// the same place despite their different page sizes (GitHub takes 100 per page, Gitea 50).
/// These bound the cost of a poll: without them one login with tens of thousands of repos would
/// stall every other row behind it.
/// </summary>
public static class GitPersonLimits
{
    /// <summary>Repos summed for the star total before the listing is cut short.</summary>
    public const int MaxRepos = 1000;

    /// <summary>Organisation members counted before the listing is cut short.</summary>
    public const int MaxMembers = 500;
}

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

    // ---- people/organisation following (see GitHubShine.Persons) ----

    /// <summary>
    /// Resolves a login to its profile, or <c>null</c> when nothing on this host answers to it.
    /// One call, and it settles <see cref="GitPersonProfile.Kind"/> too — the follow flow uses it
    /// to confirm a typed-in login before writing anything.
    /// </summary>
    Task<GitPersonProfile?> GetPersonAsync(string login, CancellationToken ct = default);

    /// <summary>
    /// The full counter set for a followed login (see <see cref="GitPersonSnapshotData"/>).
    /// Costs a paged repo listing on top of the profile lookup, so it belongs on the poll
    /// path, not in the interactive follow flow — use <see cref="GetPersonAsync"/> for that.
    /// </summary>
    /// <param name="kind">
    /// The kind recorded when the login was followed. Passed in rather than re-derived so the
    /// provider can go straight to the org- or user-shaped endpoints.
    /// </param>
    Task<GitPersonSnapshotData?> GetPersonSnapshotAsync(string login, GitPersonKind kind, CancellationToken ct = default);

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
