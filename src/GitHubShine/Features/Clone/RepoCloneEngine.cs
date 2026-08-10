using System.Text;
using Shiny;

namespace GitHubShine.Clone;

public interface IRepoCloneEngine
{
    /// <summary>Where <paramref name="repo"/> lives (or would live) under <paramref name="rootDirectory"/>.</summary>
    string ResolvePath(string rootDirectory, MonitoredRepo repo, CloneLayout layout);

    /// <summary>
    /// Clones <paramref name="repo"/> into <paramref name="rootDirectory"/> when it isn't there yet,
    /// or brings an existing working copy up to date with whatever branch it is currently on.
    /// Never merges, rebases or discards anything: a working copy with local changes, local-only
    /// commits or a diverged branch is reported and left exactly as it was.
    /// </summary>
    Task<CloneOutcome> CloneOrUpdateAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string rootDirectory,
        CloneLayout layout,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Keeps ordinary (non-bare) working copies of monitored repos in a folder of the user's choosing,
/// driving the <c>git</c> CLI through <see cref="IGitCli"/> — so, as with
/// <see cref="RepoSyncEngine"/>, no token ever reaches a URL, an argument or a <c>.git/config</c>.
///
/// The update path is deliberately conservative. It fetches, then fast-forwards <em>only</em> when
/// the checked-out branch is strictly behind its upstream and no tracked file has been touched.
/// Anything else — dirty tree, detached HEAD, diverged branch, an <c>origin</c> pointing somewhere
/// else — is reported back and skipped. Losing a user's uncommitted work to a background "catch up"
/// is not a trade worth making, and a merge or rebase is a decision only they can take.
///
/// It is also the cheap path: updating reads the remote from the working copy's own <c>origin</c>
/// and spends no API calls at all. Only a first clone needs the provider (for the clone URL and the
/// default branch).
/// </summary>
[Singleton]
public sealed class RepoCloneEngine(
    IGitProviderFactory factory,
    ITokenVault vault,
    IGitCli git,
    ILogger<RepoCloneEngine> logger) : IRepoCloneEngine
{
    public string ResolvePath(string rootDirectory, MonitoredRepo repo, CloneLayout layout)
        => layout == CloneLayout.NameOnly
            ? Path.Combine(rootDirectory, Sanitize(repo.Name))
            : Path.Combine(rootDirectory, Sanitize(repo.Owner), Sanitize(repo.Name));

    public async Task<CloneOutcome> CloneOrUpdateAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string rootDirectory,
        CloneLayout layout,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new InvalidOperationException("Choose a folder to clone into first.");

        await git.EnsureAvailableAsync(ct).ConfigureAwait(false);

        var path = this.ResolvePath(rootDirectory, repo, layout);

        // An empty folder counts as "not there yet" — git clone is happy to fill one in, and a
        // leftover empty directory shouldn't be the thing that stops a repo being cloned.
        var exists = Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();

        return exists
            ? await this.UpdateAsync(account, repo, path, progress, ct).ConfigureAwait(false)
            : await this.CloneAsync(account, repo, path, progress, ct).ConfigureAwait(false);
    }

    // ---- first clone ----

    async Task<CloneOutcome> CloneAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string path,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var provider = await factory.CreateAsync(account, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No access token is saved for {account.Label}.");

        Report(progress, $"Reading {repo.FullName} from {account.Label}…");
        var info = await provider.GetRepoInfoAsync(repo, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{repo.FullName} wasn't found on {account.Label} — check it still exists and the token can see it.");

        var url = CloneUrl(info, account, repo);
        var credential = await this.TryCredentialAsync(account, provider, ct).ConfigureAwait(false);

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        // No --branch: a plain clone checks out the remote's HEAD, which is the default branch by
        // definition. Naming it explicitly would only add a way to fail — `git clone -b <name>` is
        // fatal against a repo with no refs at all, which is exactly what a newly created repo is.
        // "--" so a repo or folder name that starts with a dash can't be read as an option.
        var args = new List<string> { "clone", "--", url, path };

        Report(progress, $"Cloning {Redact(url)} into {path}…");
        var result = await git.RunAsync(parent, args, credential, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"git clone failed: {result.Message}");

        foreach (var line in Lines(result.StdErr).Concat(Lines(result.StdOut)))
            Report(progress, line);

        var branch = string.IsNullOrWhiteSpace(info.DefaultBranch) ? null : info.DefaultBranch;
        var summary = branch is null
            ? $"Cloned into {path}"
            : $"Cloned {branch} into {path}";
        Report(progress, summary);
        return new CloneOutcome(CloneAction.Cloned, path, branch, 0, 0, summary);
    }

    // ---- catch an existing working copy up ----

    async Task<CloneOutcome> UpdateAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string path,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // Tested by looking for .git in this exact folder rather than asking git: `rev-parse` walks
        // up, so a plain folder nested inside some other checkout would answer "yes, a work tree"
        // and we'd go on to fetch into its parent repo.
        var dotGit = Path.Combine(path, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit))
            return Skip(CloneAction.SkippedNotARepo, path, null, progress,
                $"{path} already exists but isn't a git working copy — move it aside or pick another folder.");

        var originResult = await this.TryGitAsync(path, ["remote", "get-url", "origin"], null, ct).ConfigureAwait(false);
        var origin = originResult.StdOut.Trim();
        if (!originResult.Success || origin.Length == 0)
            return Skip(CloneAction.SkippedNoRemote, path, null, progress,
                "Skipped — this working copy has no 'origin' remote to fetch from.");

        // A folder holding some other repo (easily done under the flat layout, where two owners'
        // repos of the same name land in one place) must not be fetched into.
        if (!RemoteMatches(origin, repo))
            return Skip(CloneAction.SkippedDifferentRepo, path, null, progress,
                $"Skipped — origin is {Redact(origin)}, not {repo.FullName}.");

        // Cheap and local, so it comes before the fetch: no point spending a round-trip on a
        // working copy that can't be moved anyway.
        var status = await this.TryGitAsync(path, ["status", "--porcelain"], null, ct).ConfigureAwait(false);
        if (!status.Success)
            throw new InvalidOperationException($"git status failed: {status.Message}");

        var lines = Lines(status.StdOut).ToList();
        var untracked = lines.Count(l => l.StartsWith("??", StringComparison.Ordinal));
        var modified = lines.Count - untracked;

        // Untracked files deliberately do NOT block: they can't be lost to a fast-forward (git
        // refuses one that would overwrite them), and treating build output as "uncommitted work"
        // would permanently wedge most real checkouts.
        if (modified > 0)
            return Skip(CloneAction.SkippedDirty, path, null, progress,
                $"Skipped — {modified} uncommitted change{(modified == 1 ? "" : "s")} to tracked files.");

        if (untracked > 0)
            Report(progress, $"{untracked} untracked file{(untracked == 1 ? "" : "s")} left alone.");

        var head = await this.TryGitAsync(path, ["symbolic-ref", "--quiet", "--short", "HEAD"], null, ct).ConfigureAwait(false);
        var branch = head.StdOut.Trim();
        if (!head.Success || branch.Length == 0)
            return Skip(CloneAction.SkippedDetached, path, null, progress,
                "Skipped — HEAD isn't on a branch (detached), so there's nothing to fast-forward.");

        // Whatever branch the working copy is on is followed to whatever it actually tracks, which
        // on a fork is often a second remote rather than origin. Fetching only origin there would
        // compare against a stale ref and report "up to date" when it isn't.
        var tracking = await this.TryGitAsync(path, ["config", "--get", $"branch.{branch}.remote"], null, ct).ConfigureAwait(false);
        var remote = tracking.StdOut.Trim();
        if (remote.Length == 0)
            remote = "origin";

        // "." means the branch tracks another local branch — there is nothing to fetch.
        if (remote != ".")
        {
            var remoteUrl = origin;
            if (!string.Equals(remote, "origin", StringComparison.Ordinal))
            {
                var other = await this.TryGitAsync(path, ["remote", "get-url", remote], null, ct).ConfigureAwait(false);
                remoteUrl = other.Success ? other.StdOut.Trim() : "";
            }

            // Only authenticate when the remote is on the host this account belongs to.
            // http.extraHeader applies to every HTTP request the invocation makes, so a remote
            // pointing at some other server must not be handed this account's token.
            var credential = SameHost(remoteUrl, account)
                ? await this.TryCredentialAsync(account, null, ct).ConfigureAwait(false)
                : null;

            Report(progress, $"Fetching {remote} for {repo.FullName} ({branch})…");
            var fetch = await this.TryGitAsync(path, ["fetch", "--no-progress", "--prune", remote], credential, ct).ConfigureAwait(false);
            if (!fetch.Success)
                throw new InvalidOperationException($"git fetch failed: {fetch.Message}");
        }

        var upstream = await this.ResolveUpstreamAsync(path, branch, remote, ct).ConfigureAwait(false);
        if (upstream is null)
            return Skip(CloneAction.SkippedNoUpstream, path, branch, progress,
                $"Skipped — '{branch}' is local only; there's no '{remote}/{branch}' to catch up to.");

        var (ahead, behind) = await this.CountAsync(path, upstream, ct).ConfigureAwait(false);

        if (behind == 0 && ahead == 0)
            return Done(CloneAction.UpToDate, path, branch, 0, 0, progress, $"{branch} is already up to date.");

        if (behind == 0)
            return Done(CloneAction.AheadOnly, path, branch, ahead, 0, progress,
                $"{branch} is {ahead} commit{(ahead == 1 ? "" : "s")} ahead of {upstream} — nothing to pull.");

        if (ahead > 0)
            return Skip(CloneAction.SkippedDiverged, path, branch, progress,
                $"Skipped — {branch} has diverged from {upstream} ({ahead} local, {behind} remote). Merge or rebase it yourself.");

        // Strictly behind and clean: --ff-only can only move the branch pointer forward, and fails
        // loudly rather than inventing a merge commit if that turns out not to be true.
        var merge = await this.TryGitAsync(path, ["merge", "--ff-only", upstream], null, ct).ConfigureAwait(false);
        if (!merge.Success)
            throw new InvalidOperationException($"git merge --ff-only failed: {merge.Message}");

        return Done(CloneAction.Updated, path, branch, 0, behind, progress,
            $"Fast-forwarded {branch} by {behind} commit{(behind == 1 ? "" : "s")}.");
    }

    /// <summary>
    /// The ref <paramref name="branch"/> should be compared against: its configured upstream, or
    /// <c>&lt;remote&gt;/&lt;branch&gt;</c> when it has none (the common case for a branch created
    /// locally and pushed with a plain <c>git push</c>). Null when neither exists — a purely local
    /// branch has nothing to catch up to.
    /// </summary>
    async Task<string?> ResolveUpstreamAsync(string path, string branch, string remote, CancellationToken ct)
    {
        var configured = await this
            .TryGitAsync(path, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", branch + "@{upstream}"], null, ct)
            .ConfigureAwait(false);

        if (configured.Success && configured.StdOut.Trim() is { Length: > 0 } name)
            return name;

        var fallback = $"{remote}/{branch}";
        var exists = await this
            .TryGitAsync(path, ["rev-parse", "--verify", "--quiet", $"refs/remotes/{fallback}"], null, ct)
            .ConfigureAwait(false);

        return exists.Success && exists.StdOut.Trim().Length > 0 ? fallback : null;
    }

    /// <summary>Commits on HEAD the upstream lacks, and commits on the upstream HEAD lacks.</summary>
    async Task<(int Ahead, int Behind)> CountAsync(string path, string upstream, CancellationToken ct)
    {
        var result = await this
            .TryGitAsync(path, ["rev-list", "--left-right", "--count", $"HEAD...{upstream}"], null, ct)
            .ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException($"git rev-list failed: {result.Message}");

        // "<ahead>\t<behind>"
        var parts = result.StdOut.Split(new[] { '\t', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var ahead) || !int.TryParse(parts[1], out var behind))
            throw new InvalidOperationException($"Couldn't read how far behind the branch is (git said '{result.StdOut.Trim()}').");

        return (ahead, behind);
    }

    // ---- plumbing ----

    Task<GitResult> TryGitAsync(string workingDir, IReadOnlyList<string> args, GitCredential? credential, CancellationToken ct)
    {
        // Scoped with -C as well as the process working directory so nothing depends on what the
        // MAUI host left the latter set to.
        var full = new List<string> { "-C", workingDir };
        full.AddRange(args);
        return git.RunAsync(workingDir, full, credential, ct);
    }

    /// <summary>
    /// The account's HTTPS credential, or null when there's no usable token. Null is not fatal —
    /// a public repo fetches fine without one, and git will fail clearly if it turns out to matter.
    /// </summary>
    async Task<GitCredential?> TryCredentialAsync(MonitoredAccount account, IGitProvider? provider, CancellationToken ct)
    {
        var token = await vault.GetTokenAsync(account.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        // GitHub ignores the Basic username, but Gitea matches it to the token's owner, so resolve
        // a real login when the account was saved without one.
        var username = account.Username;
        if (string.IsNullOrWhiteSpace(username) && provider is not null)
        {
            try
            {
                username = (await provider.ValidateAndGetUserAsync(ct).ConfigureAwait(false)).Login;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Couldn't resolve a login for {Account}; falling back to x-access-token", account.Label);
                username = null;
            }
        }

        return new GitCredential(
            string.IsNullOrWhiteSpace(username) ? "x-access-token" : username,
            token!);
    }

    static string CloneUrl(GitRepoInfo info, MonitoredAccount account, MonitoredRepo repo)
        => info.CloneUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? info.CloneUrl
            : $"{account.WebBaseUrl}/{repo.Owner}/{repo.Name}.git";

    /// <summary>
    /// Whether a remote URL names <paramref name="repo"/>. Compares only the trailing owner/name so
    /// it works for both <c>https://host/owner/name.git</c> and <c>git@host:owner/name.git</c>;
    /// a URL neither shape parses is given the benefit of the doubt rather than blocking the update.
    /// </summary>
    static bool RemoteMatches(string url, MonitoredRepo repo)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        var parts = trimmed.Split(new[] { '/', ':' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return true;

        return string.Equals(parts[^2], repo.Owner, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(parts[^1], repo.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether an HTTP(S) remote lives on the same host as the account, so its token applies.</summary>
    static bool SameHost(string url, MonitoredAccount account)
        => Uri.TryCreate(url, UriKind.Absolute, out var remote) &&
           remote.Scheme is "http" or "https" &&
           Uri.TryCreate(account.WebBaseUrl, UriKind.Absolute, out var web) &&
           string.Equals(remote.Host, web.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Folder-safe form of an owner or repo name — both can legally contain a dot.</summary>
    static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(invalid.Contains(c) ? '-' : c);

        // A name of "." or ".." would resolve to the parent folder rather than a child of it.
        var cleaned = sb.ToString().Trim();
        return cleaned.Length == 0 || cleaned.All(c => c == '.') ? "-" : cleaned;
    }

    static CloneOutcome Skip(CloneAction action, string path, string? branch, IProgress<string>? progress, string summary)
        => Done(action, path, branch, 0, 0, progress, summary);

    static CloneOutcome Done(CloneAction action, string path, string? branch, int ahead, int behind, IProgress<string>? progress, string summary)
    {
        Report(progress, summary);
        return new CloneOutcome(action, path, branch, ahead, behind, summary);
    }

    static void Report(IProgress<string>? progress, string message) => progress?.Report(message);

    /// <summary>Belt-and-braces: URLs are built without credentials, but never log one that has them.</summary>
    static string Redact(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return url;
        return $"{uri.Scheme}://***@{uri.Host}{uri.AbsolutePath}";
    }

    static IEnumerable<string> Lines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0);
}
