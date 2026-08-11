using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;
using Shiny;

namespace GitHubShine.Sync;

/// <summary>What a single completed sync did.</summary>
public sealed record SyncOutcome(bool CreatedTarget, IReadOnlyList<string> Branches, string Summary);

public interface IRepoSyncEngine
{
    /// <summary>
    /// Brings the mapping's target repo up to date with its source, creating the target repo
    /// first when it doesn't exist yet. Progress lines are suitable for showing in the UI.
    /// </summary>
    Task<SyncOutcome> SyncAsync(SyncMapping mapping, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Copies commits between two hosts by way of a local bare repo, driving libgit2 in-process through
/// LibGit2Sharp: fetch the source's branches/tags into a cache under
/// <see cref="AppPaths.SyncCacheDirectory"/>, then push the requested refs to the target. The cache
/// means a "catch up" run only transfers the objects the target is actually missing rather than
/// re-cloning every time.
///
/// The cache is deliberately NOT a mirror clone: a mirror's <c>+refs/*:refs/*</c> refspec also drags
/// in GitHub's <c>refs/pull/*</c>, which is large and meaningless on the target. Only
/// <c>refs/heads/*</c> and <c>refs/tags/*</c> are mirrored.
///
/// Credentials go to libgit2 through a callback (see <see cref="GitCallbacks"/>) and are never
/// written anywhere: not into a URL, an argument, or the cache's config.
/// </summary>
[Singleton]
public sealed class RepoSyncEngine(
    IConfigStore config,
    ISyncStore syncStore,
    IGitProviderFactory factory,
    ITokenVault vault,
    IGitRuntime runtime,
    ILogger<RepoSyncEngine> logger) : IRepoSyncEngine
{
    /// <summary>
    /// The cache's remote for whichever target is being pushed right now. A cache is shared by every
    /// mapping with the same source, so this is repointed each run — under the same gate that keeps
    /// two runs out of one cache directory.
    /// </summary>
    const string TargetRemote = "githubshine-target";

    // Two mappings can share a source, and therefore a cache directory. Runs are sequential today,
    // but concurrent writers in one bare repo would corrupt it, so gate per cache path.
    readonly ConcurrentDictionary<string, SemaphoreSlim> cacheGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SyncOutcome> SyncAsync(SyncMapping mapping, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var outcome = await this.RunAsync(mapping, progress, ct).ConfigureAwait(false);
            await this.RecordAsync(mapping, outcome.Summary, ct).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync failed for {Source} -> {Target}", mapping.Source.FullName, mapping.TargetFullName);
            await this.RecordAsync(mapping, $"Failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    async Task<SyncOutcome> RunAsync(SyncMapping mapping, IProgress<string>? progress, CancellationToken ct)
    {
        await runtime.EnsureAvailableAsync(ct).ConfigureAwait(false);

        var sourceAccount = this.FindAccount(mapping.SourceAccountId, "source");
        var targetAccount = this.FindAccount(mapping.TargetAccountId, "target");

        // Syncing is limited to repos actually linked in the app — a repo that's been unlinked
        // since the mapping was created stops here rather than silently pushing something stale.
        if (!sourceAccount.Repos.Any(r =>
                string.Equals(r.Owner, mapping.Source.Owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Name, mapping.Source.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"{mapping.Source.FullName} is no longer a monitored repo on {sourceAccount.Label} — re-add it on the account before syncing.");
        }

        var sourceProvider = await factory.CreateAsync(sourceAccount, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No access token is saved for {sourceAccount.Label}.");
        var targetProvider = await factory.CreateAsync(targetAccount, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No access token is saved for {targetAccount.Label}.");

        Report(progress, $"Reading {mapping.Source.FullName} from {sourceAccount.Label}…");
        var sourceInfo = await sourceProvider.GetRepoInfoAsync(mapping.Source, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{mapping.Source.FullName} wasn't found on {sourceAccount.Label} — check it still exists and the token can see it.");

        var targetRepo = mapping.TargetRepo;
        var targetInfo = await targetProvider.GetRepoInfoAsync(targetRepo, ct).ConfigureAwait(false);
        var created = false;
        if (targetInfo is null)
        {
            Report(progress, $"{mapping.TargetFullName} doesn't exist on {targetAccount.Label} — creating it…");
            targetInfo = await targetProvider
                .CreateRepoAsync(targetRepo.Owner, targetRepo.Name, sourceInfo.Description, sourceInfo.Private, ct)
                .ConfigureAwait(false);
            created = true;
        }

        var sourceCredential = await this.CredentialAsync(sourceAccount, sourceProvider, ct).ConfigureAwait(false);
        var targetCredential = await this.CredentialAsync(targetAccount, targetProvider, ct).ConfigureAwait(false);
        var sourceUrl = CloneUrl(sourceInfo, sourceAccount, mapping.Source);
        var targetUrl = CloneUrl(targetInfo, targetAccount, targetRepo);

        var cacheDir = this.CacheDirectoryFor(mapping);
        var gate = this.cacheGates.GetOrAdd(cacheDir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);

        IReadOnlyList<string> pushedBranches;
        try
        {
            EnsureCache(cacheDir, sourceUrl);

            Report(progress, $"Fetching from {Redact(sourceUrl)}…");

            pushedBranches = await GitCallbacks.Run(() =>
            {
                using var git = new Repository(cacheDir);

                // Prune covers tags as well as heads here, because the refspecs this cache fetches
                // with include refs/tags/* — which is what `--prune --prune-tags` bought the CLI.
                Commands.Fetch(
                    git,
                    "origin",
                    [],
                    GitCallbacks.Fetch(sourceCredential, ct, prune: true, progress, TagFetchMode.All),
                    null);

                var branches = ResolveBranches(git, mapping, sourceInfo.DefaultBranch, progress);
                var specs = BuildPushRefSpecs(git, mapping, branches);

                var remote = git.Network.Remotes[TargetRemote] is null
                    ? git.Network.Remotes.Add(TargetRemote, targetUrl)
                    : UpdateUrl(git, TargetRemote, targetUrl);

                // Deletions are worked out here rather than by a push option: libgit2 has no
                // equivalent of `push --prune`, so the refs the target has and the source no longer
                // does become explicit ":refs/…" delete refspecs.
                if (mapping is { Force: true, BranchMode: SyncBranchMode.All })
                    specs.AddRange(DeletionRefSpecs(git, remote, targetCredential, mapping.IncludeTags, progress));

                Report(progress, $"Pushing {DescribeRefs(mapping, branches)} to {Redact(targetUrl)}…");
                Push(git, remote, specs, targetCredential, ct);

                return branches;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        // A freshly created repo starts on its host's own default (usually "main"). If the source
        // calls it something else, and we just pushed that branch, repoint the target's HEAD.
        if (created &&
            !string.Equals(targetInfo.DefaultBranch, sourceInfo.DefaultBranch, StringComparison.Ordinal) &&
            (mapping.BranchMode == SyncBranchMode.All || pushedBranches.Contains(sourceInfo.DefaultBranch, StringComparer.Ordinal)))
        {
            try
            {
                await targetProvider.SetDefaultBranchAsync(targetRepo, sourceInfo.DefaultBranch, ct).ConfigureAwait(false);
                Report(progress, $"Set default branch to {sourceInfo.DefaultBranch}.");
            }
            catch (Exception ex)
            {
                // Cosmetic — the commits are already there, so don't fail the sync over it.
                Report(progress, $"Note: couldn't set the default branch to {sourceInfo.DefaultBranch} ({ex.Message}).");
            }
        }

        var summary = created
            ? $"Created and synced {DescribeRefs(mapping, pushedBranches)}"
            : $"Synced {DescribeRefs(mapping, pushedBranches)}";
        Report(progress, summary);
        return new SyncOutcome(created, pushedBranches, summary);
    }

    // ---- cache ----

    /// <summary>
    /// Creates (or refreshes) the bare staging repo. Keyed by source account + repo, so several
    /// mappings that back up the same source to different targets share one local copy.
    /// </summary>
    static void EnsureCache(string cacheDir, string sourceUrl)
    {
        Directory.CreateDirectory(AppPaths.SyncCacheDirectory);

        if (!File.Exists(Path.Combine(cacheDir, "HEAD")))
        {
            // Either brand new or a half-written cache from an interrupted run — start clean.
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);

            Repository.Init(cacheDir, isBare: true);

            using var fresh = new Repository(cacheDir);
            fresh.Network.Remotes.Add("origin", sourceUrl);
            SetSourceRefSpecs(fresh);
            return;
        }

        using var git = new Repository(cacheDir);
        UpdateUrl(git, "origin", sourceUrl);

        // Rewritten every run so caches created by older builds pick up refspec changes.
        SetSourceRefSpecs(git);
    }

    /// <summary>
    /// Mirrors heads and tags into the cache under the same names, and nothing else — no
    /// <c>refs/pull/*</c>, no remote-tracking indirection. Replaces whatever was configured before.
    /// </summary>
    static void SetSourceRefSpecs(Repository git)
        => git.Network.Remotes.Update("origin", r => r.FetchRefSpecs =
        [
            "+refs/heads/*:refs/heads/*",
            "+refs/tags/*:refs/tags/*"
        ]);

    static Remote UpdateUrl(Repository git, string name, string url)
    {
        git.Network.Remotes.Update(name, r => r.Url = url);
        return git.Network.Remotes[name];
    }

    /// <summary>
    /// Cache directory for a mapping's <b>source</b> — several mappings backing the same repo up
    /// to different targets share one local copy. The readable prefix is only for humans poking
    /// around the folder; the appended hash of the exact key is what guarantees uniqueness, since
    /// sanitising can map two different repo names onto the same string.
    /// </summary>
    string CacheDirectoryFor(SyncMapping mapping)
    {
        var key = $"{mapping.SourceAccountId}\n{mapping.Source.Owner}\n{mapping.Source.Name}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
        var label = $"{Sanitize(mapping.Source.Owner)}-{Sanitize(mapping.Source.Name)}";
        return Path.Combine(AppPaths.SyncCacheDirectory, $"{label}-{hash}.git");
    }

    static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(invalid.Contains(c) || c is '.' or ' ' ? '-' : c);
        return sb.ToString();
    }

    // ---- refs ----

    /// <summary>
    /// Turns the mapping's branch mode into the concrete branch names to push, dropping (and
    /// warning about) any that aren't in the source. Returns empty for All mode, which pushes a
    /// wildcard refspec instead of naming branches.
    /// </summary>
    static IReadOnlyList<string> ResolveBranches(
        Repository git,
        SyncMapping mapping,
        string sourceDefaultBranch,
        IProgress<string>? progress)
    {
        if (mapping.BranchMode == SyncBranchMode.All)
            return [];

        var wanted = mapping.BranchMode == SyncBranchMode.Named
            ? (mapping.Branches ?? new List<string>())
                .Select(b => b.Trim())
                .Where(b => b.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [sourceDefaultBranch];

        if (wanted.Count == 0)
            throw new InvalidOperationException("This sync has no branches selected — edit it and pick at least one.");

        var present = new HashSet<string>(HeadNames(git), StringComparer.Ordinal);

        var missing = wanted.Where(b => !present.Contains(b)).ToList();
        foreach (var b in missing)
            Report(progress, $"Skipping '{b}' — no such branch on {mapping.Source.FullName}.");

        var resolved = wanted.Where(present.Contains).ToList();
        if (resolved.Count == 0)
            throw new InvalidOperationException(
                $"None of the selected branches ({string.Join(", ", wanted)}) exist on {mapping.Source.FullName}.");

        return resolved;
    }

    /// <summary>The cache's own branches, by bare name — the fetch mirrored them under refs/heads.</summary>
    static IEnumerable<string> HeadNames(Repository git)
        => git.Refs
            .Select(r => r.CanonicalName)
            .Where(n => n.StartsWith("refs/heads/", StringComparison.Ordinal))
            .Select(n => n["refs/heads/".Length..])
            .Where(n => n.Length > 0);

    /// <summary>
    /// The concrete refspecs to push. Every wildcard is expanded against the cache first, because
    /// libgit2's push — unlike the CLI's — rejects a pattern refspec outright ("not a valid
    /// reference 'refs/heads/*'"). Expanding here means "all branches" still means whatever the
    /// source had at the moment it was fetched, which is exactly what the wildcard used to mean.
    /// </summary>
    static List<string> BuildPushRefSpecs(Repository git, SyncMapping mapping, IReadOnlyList<string> branches)
    {
        // A leading '+' is a per-refspec force. Preferred over a blanket force flag because it keeps
        // the "no force" case genuinely fast-forward-only, so a diverged target fails loudly.
        var force = mapping.Force ? "+" : "";
        var specs = new List<string>();

        if (mapping.BranchMode == SyncBranchMode.All)
            specs.AddRange(RefsUnder(git, "refs/heads/").Select(n => $"{force}{n}:{n}"));
        else
            specs.AddRange(branches.Select(b => $"{force}refs/heads/{b}:refs/heads/{b}"));

        if (mapping.IncludeTags)
            specs.AddRange(RefsUnder(git, "refs/tags/").Select(n => $"{force}{n}:{n}"));

        if (specs.Count == 0)
            throw new InvalidOperationException(
                $"{mapping.Source.FullName} has no branches to push — it looks empty.");

        return specs;
    }

    /// <summary>Canonical names of the cache's refs below a prefix, in a stable order.</summary>
    static IEnumerable<string> RefsUnder(Repository git, string prefix)
        => git.Refs
            .Select(r => r.CanonicalName)
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.Length > prefix.Length)
            .OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>
    /// Delete refspecs for everything the target still has and the source has dropped — exact-mirror
    /// semantics, which is why the caller only asks for them under both All mode and the explicit
    /// Force opt-in. Listing the target's refs is one extra round-trip, and the only way to see them:
    /// libgit2 has no push-side prune.
    /// </summary>
    static IEnumerable<string> DeletionRefSpecs(
        Repository git,
        Remote remote,
        GitCredential? credential,
        bool includeTags,
        IProgress<string>? progress)
    {
        List<string> theirs;
        try
        {
            theirs = git.Network
                .ListReferences(remote, GitCallbacks.Credentials(credential))
                .Select(r => r.CanonicalName)
                .ToList();
        }
        catch (Exception ex)
        {
            // A target that can't be listed (brand new and still empty, most likely) has nothing to
            // prune anyway — never fail the push over the bookkeeping half of it.
            Report(progress, $"Note: couldn't list the target's refs to prune ({ex.Message}).");
            yield break;
        }

        var ours = new HashSet<string>(git.Refs.Select(r => r.CanonicalName), StringComparer.Ordinal);

        foreach (var name in theirs)
        {
            var isHead = name.StartsWith("refs/heads/", StringComparison.Ordinal);
            var isTag = includeTags && name.StartsWith("refs/tags/", StringComparison.Ordinal);

            // Peeled tags ("refs/tags/v1^{}") are a listing artefact, not a ref you can delete.
            if ((!isHead && !isTag) || name.EndsWith("^{}", StringComparison.Ordinal) || ours.Contains(name))
                continue;

            Report(progress, $"Pruning {name} from the target — it's gone from the source.");
            yield return $":{name}";
        }
    }

    /// <summary>
    /// Pushes and turns a per-ref rejection into a failure. libgit2 reports those through a callback
    /// rather than by throwing, so without this a target that refused a non-fast-forward would look
    /// like a clean sync.
    /// </summary>
    static void Push(Repository git, Remote remote, IEnumerable<string> specs, GitCredential? credential, CancellationToken ct)
    {
        var rejected = new List<string>();
        var options = GitCallbacks.Push(credential, ct);
        options.OnPushStatusError = error => rejected.Add($"{error.Reference}: {error.Message}");

        git.Network.Push(remote, specs, options);

        if (rejected.Count > 0)
            throw new InvalidOperationException($"The target rejected the push — {string.Join("; ", rejected)}");
    }

    static string DescribeRefs(SyncMapping mapping, IReadOnlyList<string> branches)
    {
        var branchPart = mapping.BranchMode == SyncBranchMode.All
            ? "all branches"
            : branches.Count == 1
                ? $"branch {branches[0]}"
                : $"{branches.Count} branches ({string.Join(", ", branches)})";
        return mapping.IncludeTags ? branchPart + " and tags" : branchPart;
    }

    // ---- plumbing ----

    MonitoredAccount FindAccount(string accountId, string role)
        => config.Accounts.FirstOrDefault(a => a.Id == accountId)
            ?? throw new InvalidOperationException($"The {role} account for this sync no longer exists — edit or delete the sync.");

    async Task<GitCredential> CredentialAsync(MonitoredAccount account, IGitProvider provider, CancellationToken ct)
    {
        var token = await vault.GetTokenAsync(account.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"No access token is saved for {account.Label}.");

        // GitHub ignores the Basic username, but Gitea matches it to the token's owner, so resolve
        // a real login when the account was saved without one.
        var username = account.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            try
            {
                username = (await provider.ValidateAndGetUserAsync(ct).ConfigureAwait(false)).Login;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Couldn't resolve a login for {Account}; falling back to x-access-token", account.Label);
                username = "x-access-token";
            }
        }

        return new GitCredential(username!, token!);
    }

    static string CloneUrl(GitRepoInfo info, MonitoredAccount account, MonitoredRepo repo)
        => info.CloneUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? info.CloneUrl
            : $"{account.WebBaseUrl}/{repo.Owner}/{repo.Name}.git";

    async Task RecordAsync(SyncMapping mapping, string result, CancellationToken ct)
    {
        try
        {
            await syncStore
                .UpsertAsync(mapping with { LastSyncedUtc = DateTimeOffset.UtcNow, LastResult = result }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Bookkeeping only — never turn a successful sync into a failure.
            logger.LogWarning(ex, "Couldn't record the sync result for {Source}", mapping.Source.FullName);
        }
    }

    static void Report(IProgress<string>? progress, string message) => progress?.Report(message);

    /// <summary>Belt-and-braces: URLs are built without credentials, but never log one that has them.</summary>
    static string Redact(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return url;
        return $"{uri.Scheme}://***@{uri.Host}{uri.AbsolutePath}";
    }
}
