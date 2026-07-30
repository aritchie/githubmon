using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
/// Copies commits between two hosts by way of a local bare repo, using the <c>git</c> CLI:
/// fetch the source's branches/tags into a cache under <see cref="AppPaths.SyncCacheDirectory"/>,
/// then push the requested refs to the target. The cache means a "catch up" run only transfers
/// the objects the target is actually missing rather than re-cloning every time.
///
/// The cache is deliberately NOT a `git clone --mirror`: a mirror's <c>+refs/*:refs/*</c> refspec
/// also drags in GitHub's <c>refs/pull/*</c>, which is large and meaningless on the target. Only
/// <c>refs/heads/*</c> and <c>refs/tags/*</c> are mirrored.
///
/// Credentials are handled by <see cref="GitCli"/> — see its remarks; nothing here ever puts a
/// token into a URL, an argument, or the cache's .git config.
/// </summary>
[Singleton]
public sealed class RepoSyncEngine(
    IConfigStore config,
    ISyncStore syncStore,
    IGitProviderFactory factory,
    ITokenVault vault,
    IGitCli git,
    ILogger<RepoSyncEngine> logger) : IRepoSyncEngine
{
    // Two mappings can share a source, and therefore a cache directory. Runs are sequential today,
    // but concurrent git processes in one bare repo would corrupt it, so gate per cache path.
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
        await git.EnsureAvailableAsync(ct).ConfigureAwait(false);

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
            await this.EnsureCacheAsync(cacheDir, sourceUrl, ct).ConfigureAwait(false);

            Report(progress, $"Fetching from {Redact(sourceUrl)}…");
            await this.RunGitAsync(cacheDir, ["fetch", "--no-progress", "--prune", "--prune-tags", "origin"], sourceCredential, ct)
                .ConfigureAwait(false);

            pushedBranches = await this.ResolveBranchesAsync(cacheDir, mapping, sourceInfo.DefaultBranch, progress, ct).ConfigureAwait(false);

            var args = BuildPushArgs(mapping, targetUrl, pushedBranches);
            Report(progress, $"Pushing {DescribeRefs(mapping, pushedBranches)} to {Redact(targetUrl)}…");
            var push = await this.RunGitAsync(cacheDir, args, targetCredential, ct).ConfigureAwait(false);
            foreach (var line in Lines(push.StdErr).Concat(Lines(push.StdOut)))
                Report(progress, line);
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
    async Task EnsureCacheAsync(string cacheDir, string sourceUrl, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.SyncCacheDirectory);

        if (!File.Exists(Path.Combine(cacheDir, "HEAD")))
        {
            // Either brand new or a half-written cache from an interrupted run — start clean.
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);

            Directory.CreateDirectory(cacheDir);
            await this.RunGitAsync(cacheDir, ["init", "--bare", "--quiet"], null, ct).ConfigureAwait(false);
            await this.RunGitAsync(cacheDir, ["remote", "add", "origin", sourceUrl], null, ct).ConfigureAwait(false);
        }
        else
        {
            await this.RunGitAsync(cacheDir, ["remote", "set-url", "origin", sourceUrl], null, ct).ConfigureAwait(false);
        }

        // Rewritten every run so caches created by older builds pick up refspec changes.
        await this.RunGitAsync(cacheDir, ["config", "--replace-all", "remote.origin.fetch", "+refs/heads/*:refs/heads/*"], null, ct).ConfigureAwait(false);
        await this.RunGitAsync(cacheDir, ["config", "--add", "remote.origin.fetch", "+refs/tags/*:refs/tags/*"], null, ct).ConfigureAwait(false);
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
    async Task<IReadOnlyList<string>> ResolveBranchesAsync(
        string cacheDir,
        SyncMapping mapping,
        string sourceDefaultBranch,
        IProgress<string>? progress,
        CancellationToken ct)
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

        var listed = await this.RunGitAsync(cacheDir, ["for-each-ref", "--format=%(refname:strip=2)", "refs/heads/"], null, ct).ConfigureAwait(false);
        var present = new HashSet<string>(Lines(listed.StdOut), StringComparer.Ordinal);

        var missing = wanted.Where(b => !present.Contains(b)).ToList();
        foreach (var b in missing)
            Report(progress, $"Skipping '{b}' — no such branch on {mapping.Source.FullName}.");

        var resolved = wanted.Where(present.Contains).ToList();
        if (resolved.Count == 0)
            throw new InvalidOperationException(
                $"None of the selected branches ({string.Join(", ", wanted)}) exist on {mapping.Source.FullName}.");

        return resolved;
    }

    static List<string> BuildPushArgs(SyncMapping mapping, string targetUrl, IReadOnlyList<string> branches)
    {
        // A leading '+' is a per-refspec force. Preferred over --force because it keeps the
        // "no force" case genuinely fast-forward-only, so a diverged target fails loudly.
        var force = mapping.Force ? "+" : "";
        var specs = new List<string>();

        if (mapping.BranchMode == SyncBranchMode.All)
            specs.Add($"{force}refs/heads/*:refs/heads/*");
        else
            specs.AddRange(branches.Select(b => $"{force}refs/heads/{b}:refs/heads/{b}"));

        if (mapping.IncludeTags)
            specs.Add($"{force}refs/tags/*:refs/tags/*");

        var args = new List<string> { "push", "--no-progress" };

        // Pruning deletes refs on the target that the source no longer has — exact-mirror
        // semantics, so it's gated behind both All mode and the explicit Force opt-in.
        if (mapping is { Force: true, BranchMode: SyncBranchMode.All })
            args.Add("--prune");

        args.Add(targetUrl);
        args.AddRange(specs);
        return args;
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

    async Task<GitResult> RunGitAsync(string workingDir, IReadOnlyList<string> args, GitCredential? credential, CancellationToken ct)
    {
        // Every invocation is scoped to the cache repo with -C so nothing depends on the process's
        // working directory (which the MAUI host owns).
        var full = new List<string> { "-C", workingDir };
        full.AddRange(args);

        var result = await git.RunAsync(workingDir, full, credential, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"git {args[0]} failed: {result.Message}");

        return result;
    }

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

    static IEnumerable<string> Lines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0);
}
