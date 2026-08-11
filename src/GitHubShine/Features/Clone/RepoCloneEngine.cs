using System.Text;
using LibGit2Sharp;
using Shiny;

namespace GitHubShine.Clone;

public interface IRepoCloneEngine
{
    /// <summary>Where <paramref name="repo"/> lives (or would live) under <paramref name="rootDirectory"/>.</summary>
    string ResolvePath(string rootDirectory, MonitoredRepo repo, CloneLayout layout);

    /// <summary>
    /// Reads the working copy without changing anything: the branch it's on, the branches it could
    /// be on, and how far the current one has drifted from its upstream.
    /// </summary>
    /// <param name="fetchRemote">
    /// Whether to fetch first. False is entirely local and instant, but the counts are only as
    /// fresh as the last fetch; true costs a round-trip and makes "behind" mean behind right now.
    /// </param>
    Task<CloneStatus> GetStatusAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string rootDirectory,
        CloneLayout layout,
        bool fetchRemote,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Checks <paramref name="branch"/> out, creating a local tracking branch when it so far only
    /// exists on a remote, and returns the working copy's state afterwards. Refuses (rather than
    /// stashing or discarding) when tracked files have been modified.
    /// </summary>
    Task<CloneStatus> SwitchBranchAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string rootDirectory,
        CloneLayout layout,
        string branch,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Clones <paramref name="repo"/> into <paramref name="rootDirectory"/> when it isn't there yet,
    /// or brings an existing working copy up to date with whatever branch it is currently on.
    /// Never merges, rebases or discards anything: a working copy with local changes, local-only
    /// commits or a diverged branch is reported and left exactly as it was.
    /// </summary>
    /// <param name="initialBranch">
    /// The branch a <em>fresh</em> clone should be left on. Null means the repo's own default, and
    /// so does a name the repo doesn't have — a preference for "develop" shouldn't fail the clone
    /// of a repo that never had one. Ignored for a working copy that already exists, which stays on
    /// whatever branch the user put it on.
    /// </param>
    Task<CloneOutcome> CloneOrUpdateAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string rootDirectory,
        CloneLayout layout,
        string? initialBranch = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Keeps ordinary (non-bare) working copies of monitored repos in a folder of the user's choosing,
/// driving libgit2 in-process through LibGit2Sharp — so, as with <see cref="RepoSyncEngine"/>, no
/// token ever reaches a URL, an argument or a <c>.git/config</c>; credentials are handed to libgit2
/// through a callback and never leave the process.
///
/// The update path is deliberately conservative. It fetches, then fast-forwards <em>only</em> when
/// the checked-out branch is strictly behind its upstream and no tracked file has been touched.
/// Anything else — dirty tree, detached HEAD, diverged branch, an <c>origin</c> pointing somewhere
/// else — is reported back and skipped. Losing a user's uncommitted work to a background "catch up"
/// is not a trade worth making, and a merge or rebase is a decision only they can take.
///
/// Reading a working copy and acting on one are the same code path: <see cref="InspectAsync"/>
/// produces a <see cref="CloneStatus"/> and <see cref="ApplyAsync"/> decides what that state
/// permits, so the badge the UI shows and the decision a run makes can't drift apart.
///
/// It is also the cheap path: everything except a first clone reads the remote from the working
/// copy's own config and spends no API calls at all. Only a first clone needs the provider (for the
/// clone URL and the default branch).
///
/// Every libgit2 call is synchronous and some of them are minutes long, so they all run through
/// <see cref="GitCallbacks.Run{T}"/> on the thread pool rather than on the caller's thread.
/// </summary>
[Singleton]
public sealed class RepoCloneEngine(
    IGitProviderFactory factory,
    ITokenVault vault,
    IGitRuntime runtime,
    ILogger<RepoCloneEngine> logger) : IRepoCloneEngine
{
    /// <summary>
    /// Identity stamped on a merge. A fast-forward writes no commit, so this is never recorded
    /// anywhere — but libgit2 requires a signature for any merge, including one that can't commit.
    /// </summary>
    static readonly Signature MergeIdentity = new("GitHub Shine", "githubshine@localhost", DateTimeOffset.Now);

    public string ResolvePath(string rootDirectory, MonitoredRepo repo, CloneLayout layout)
        => layout == CloneLayout.NameOnly
            ? Path.Combine(rootDirectory, Sanitize(repo.Name))
            : Path.Combine(rootDirectory, Sanitize(repo.Owner), Sanitize(repo.Name));

    public async Task<CloneStatus> GetStatusAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string rootDirectory,
        CloneLayout layout,
        bool fetchRemote,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var path = this.Prepare(rootDirectory, repo, layout);
        await runtime.EnsureAvailableAsync(ct).ConfigureAwait(false);
        return await this.InspectAsync(account, repo, path, fetchRemote, progress, ct).ConfigureAwait(false);
    }

    public async Task<CloneOutcome> CloneOrUpdateAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string rootDirectory,
        CloneLayout layout,
        string? initialBranch = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var path = this.Prepare(rootDirectory, repo, layout);
        await runtime.EnsureAvailableAsync(ct).ConfigureAwait(false);

        // An empty folder counts as "not there yet" — a clone is happy to fill one in, and a
        // leftover empty directory shouldn't be the thing that stops a repo being cloned.
        if (IsEmpty(path))
            return await this.CloneAsync(account, repo, path, initialBranch, progress, ct).ConfigureAwait(false);

        var status = await this.InspectAsync(account, repo, path, fetchRemote: true, progress, ct).ConfigureAwait(false);
        return await this.ApplyAsync(status, progress, ct).ConfigureAwait(false);
    }

    // ---- first clone ----

    async Task<CloneOutcome> CloneAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string path,
        string? initialBranch,
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

        Report(progress, $"Cloning {Redact(url)} into {path}…");

        var branch = await GitCallbacks.Run(() =>
        {
            var options = new CloneOptions(GitCallbacks.Fetch(credential, ct, prune: false, progress))
            {
                Checkout = true
                // BranchName is deliberately left unset even when one was asked for: cloning a
                // named branch is fatal against a repo that hasn't got it (and against one with no
                // refs at all, which is exactly what a newly created repo is). A plain clone checks
                // out the remote's HEAD, and the requested branch is switched to afterwards, where
                // failing to find it is a note rather than a failed clone.
            };

            Repository.Clone(url, path, options);

            // The clone's actual HEAD, not the API's idea of the default branch — they agree in the
            // normal case, and where they don't it's the working copy that's telling the truth.
            using var cloned = new Repository(path);
            var head = CurrentBranch(cloned)
                ?? (string.IsNullOrWhiteSpace(info.DefaultBranch) ? null : info.DefaultBranch);

            if (!string.IsNullOrWhiteSpace(initialBranch) &&
                !string.Equals(initialBranch, head, StringComparison.Ordinal))
            {
                if (TrySwitch(cloned, initialBranch!))
                    head = initialBranch;
                else
                    Report(progress, $"No '{initialBranch}' branch on {repo.FullName} — left on {head ?? "the default branch"}.");
            }

            return head;
        }, ct).ConfigureAwait(false);

        var summary = branch is null
            ? $"Cloned into {path}"
            : $"Cloned {branch} into {path}";
        Report(progress, summary);
        return new CloneOutcome(CloneAction.Cloned, path, branch, 0, 0, summary);
    }

    // ---- read a working copy ----

    /// <summary>
    /// Everything that can be learned about a working copy without changing it. Structural problems
    /// (not a repo, wrong repo) short-circuit; anything past those reports counts <em>and</em> the
    /// reason it may not be movable, because "3 behind, 2 files uncommitted" is more use than
    /// either half on its own.
    ///
    /// Split either side of the credential lookup, which is async and can't happen inside a libgit2
    /// call: the first pass reads what the working copy says about itself (including which remote
    /// and therefore which host is about to be contacted), and the second does the fetch and the
    /// counting. Opening the repository twice costs microseconds next to a network round-trip.
    /// </summary>
    async Task<CloneStatus> InspectAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string path,
        bool fetchRemote,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (IsEmpty(path))
            return CloneStatus.Plain(CloneStatusKind.NotCloned, path, "Not cloned yet.");

        // Tested by looking for .git in this exact folder rather than letting libgit2 discover one:
        // discovery walks up, so a plain folder nested inside some other checkout would answer
        // "yes, a work tree" and we'd go on to fetch into its parent repo.
        var dotGit = Path.Combine(path, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit))
            return CloneStatus.Plain(CloneStatusKind.NotARepo, path,
                $"{path} already exists but isn't a git working copy — move it aside or pick another folder.");

        var probe = await GitCallbacks.Run(() => Probe(path, repo), ct).ConfigureAwait(false);
        if (probe.Blocked is { } blocked)
            return blocked;

        // Only authenticate when the remote is on the host this account belongs to — a remote
        // pointing at some other server must not be handed this account's token.
        var credential = fetchRemote && probe.CanFetch && SameHost(probe.RemoteUrl, account)
            ? await this.TryCredentialAsync(account, null, ct).ConfigureAwait(false)
            : null;

        if (fetchRemote && probe.CanFetch)
            Report(progress, $"Fetching {probe.Remote} for {repo.FullName}…");

        return await GitCallbacks
            .Run(() => Read(path, probe, fetchRemote && probe.CanFetch, credential, progress, ct), ct)
            .ConfigureAwait(false);
    }

    /// <summary>What the working copy says about itself before anything touches the network.</summary>
    readonly record struct RepoProbe(CloneStatus? Blocked, string Remote, string RemoteUrl, bool CanFetch);

    static RepoProbe Probe(string path, MonitoredRepo repo)
    {
        if (!Repository.IsValid(path))
            return new RepoProbe(CloneStatus.Plain(CloneStatusKind.NotARepo, path,
                $"{path} already exists but isn't a git working copy — move it aside or pick another folder."), "", "", false);

        using var git = new Repository(path);

        var origin = git.Network.Remotes["origin"]?.Url ?? "";
        if (origin.Length == 0)
            return new RepoProbe(CloneStatus.Plain(CloneStatusKind.NoRemote, path,
                "This working copy has no 'origin' remote to fetch from."), "", "", false);

        // A folder holding some other repo (easily done under the flat layout, where two owners'
        // repos of the same name land in one place) must not be fetched into.
        if (!RemoteMatches(origin, repo))
            return new RepoProbe(CloneStatus.Plain(CloneStatusKind.DifferentRepo, path,
                $"origin is {Redact(origin)}, not {repo.FullName}."), "", "", false);

        // Whatever branch the working copy is on is followed to whatever it actually tracks, which
        // on a fork is often a second remote rather than origin. Fetching only origin there would
        // compare against a stale ref and report "up to date" when it isn't. A detached HEAD has no
        // such config, and origin is the only sensible guess.
        var remote = "origin";
        if (CurrentBranch(git) is { } branch &&
            git.Config.Get<string>($"branch.{branch}.remote")?.Value is { Length: > 0 } configured)
            remote = configured;

        // "." means the branch tracks another local branch — there is nothing to fetch.
        if (remote == ".")
            return new RepoProbe(null, remote, "", false);

        var url = string.Equals(remote, "origin", StringComparison.Ordinal)
            ? origin
            : git.Network.Remotes[remote]?.Url ?? "";

        return new RepoProbe(null, remote, url, url.Length > 0);
    }

    static CloneStatus Read(
        string path,
        RepoProbe probe,
        bool fetch,
        GitCredential? credential,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        using var git = new Repository(path);

        // Counted the way `status --porcelain` prints them: a "??" line per untracked path (with
        // untracked folders collapsed to one entry), everything else a tracked change.
        var entries = git
            .RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                RecurseUntrackedDirs = false,
                IncludeIgnored = false,
                IncludeUnaltered = false
            })
            .ToList();

        var untracked = entries.Count(e => e.State == FileStatus.NewInWorkdir);
        var modified = entries.Count - untracked;

        var fetched = false;
        if (fetch)
        {
            Commands.Fetch(
                git,
                probe.Remote,
                [],
                GitCallbacks.Fetch(credential, ct, prune: true, progress),
                null);

            fetched = true;
        }

        // After the fetch, so a branch that has just appeared on the remote is offered.
        var branches = ListBranches(git);
        var branch = CurrentBranch(git);

        if (branch is null)
            return new CloneStatus(
                CloneStatusKind.Detached, path, null, null, 0, 0, modified, untracked, branches, fetched,
                Suffix("HEAD isn't on a branch (detached).", modified, untracked));

        var upstream = ResolveUpstream(git, branch, probe.Remote);
        if (upstream is null)
            return new CloneStatus(
                CloneStatusKind.NoUpstream, path, branch, null, 0, 0, modified, untracked, branches, fetched,
                Suffix($"'{branch}' is local only; there's no '{probe.Remote}/{branch}' to catch up to.", modified, untracked));

        var (ahead, behind) = Count(git, branch, upstream);

        return new CloneStatus(
            CloneStatusKind.Tracking, path, branch, upstream, ahead, behind, modified, untracked, branches, fetched,
            Suffix(Describe(branch, upstream, ahead, behind), modified, untracked));
    }

    /// <summary>Reads the current state as a sentence, before any "and it's dirty" qualifier.</summary>
    static string Describe(string branch, string upstream, int ahead, int behind) => (ahead, behind) switch
    {
        (0, 0) => $"{branch} is up to date with {upstream}.",
        (0, _) => $"{branch} is {Commits(behind)} behind {upstream}.",
        (_, 0) => $"{branch} is {Commits(ahead)} ahead of {upstream}.",
        _ => $"{branch} has diverged from {upstream} — {ahead} local, {behind} remote."
    };

    static string Suffix(string detail, int modified, int untracked)
    {
        if (modified > 0)
            detail += $" {modified} uncommitted change{(modified == 1 ? "" : "s")} to tracked files.";
        if (untracked > 0)
            detail += $" {untracked} untracked file{(untracked == 1 ? "" : "s")}.";
        return detail;
    }

    // ---- act on what was read ----

    /// <summary>
    /// Turns an inspected state into the one thing that state permits. The only write it ever makes
    /// is a fast-forward merge, and only from strictly-behind-and-clean.
    /// </summary>
    async Task<CloneOutcome> ApplyAsync(CloneStatus status, IProgress<string>? progress, CancellationToken ct)
    {
        switch (status.Kind)
        {
            case CloneStatusKind.NotARepo:
                return Skip(CloneAction.SkippedNotARepo, status, progress, status.Detail);

            case CloneStatusKind.NoRemote:
                return Skip(CloneAction.SkippedNoRemote, status, progress, $"Skipped — {Lower(status.Detail)}");

            case CloneStatusKind.DifferentRepo:
                return Skip(CloneAction.SkippedDifferentRepo, status, progress, $"Skipped — {Lower(status.Detail)}");
        }

        // Untracked files deliberately do NOT block: they can't be lost to a fast-forward (it
        // refuses one that would overwrite them), and treating build output as "uncommitted work"
        // would permanently wedge most real checkouts.
        if (status.Modified > 0)
            return Skip(CloneAction.SkippedDirty, status, progress,
                $"Skipped — {status.Modified} uncommitted change{(status.Modified == 1 ? "" : "s")} to tracked files.");

        if (status.Untracked > 0)
            Report(progress, $"{status.Untracked} untracked file{(status.Untracked == 1 ? "" : "s")} left alone.");

        switch (status.Kind)
        {
            case CloneStatusKind.Detached:
                return Skip(CloneAction.SkippedDetached, status, progress,
                    "Skipped — HEAD isn't on a branch (detached), so there's nothing to fast-forward.");

            case CloneStatusKind.NoUpstream:
                return Skip(CloneAction.SkippedNoUpstream, status, progress,
                    $"Skipped — '{status.Branch}' is local only; there's no matching remote branch to catch up to.");
        }

        var branch = status.Branch!;
        var upstream = status.Upstream!;

        if (status.Behind == 0 && status.Ahead == 0)
            return Done(CloneAction.UpToDate, status, 0, 0, progress, $"{branch} is already up to date.");

        if (status.Behind == 0)
            return Done(CloneAction.AheadOnly, status, status.Ahead, 0, progress,
                $"{branch} is {Commits(status.Ahead)} ahead of {upstream} — nothing to pull.");

        if (status.Ahead > 0)
            return Skip(CloneAction.SkippedDiverged, status, progress,
                $"Skipped — {branch} has diverged from {upstream} ({status.Ahead} local, {status.Behind} remote). Merge or rebase it yourself.");

        // Strictly behind and clean: FastForwardOnly can only move the branch pointer forward, and
        // fails loudly rather than inventing a merge commit if that turns out not to be true.
        await GitCallbacks.Run(() =>
        {
            using var git = new Repository(status.Path);
            var target = git.Branches[upstream]
                ?? throw new InvalidOperationException($"'{upstream}' has gone from the working copy — fetch again and retry.");

            var result = git.Merge(target, MergeIdentity, new MergeOptions
            {
                FastForwardStrategy = FastForwardStrategy.FastForwardOnly,
                FailOnConflict = true
            });

            if (result.Status != MergeStatus.FastForward && result.Status != MergeStatus.UpToDate)
                throw new InvalidOperationException(
                    $"Couldn't fast-forward {branch} to {upstream} — the merge came back {result.Status}.");
        }, ct).ConfigureAwait(false);

        return Done(CloneAction.Updated, status, 0, status.Behind, progress,
            $"Fast-forwarded {branch} by {Commits(status.Behind)}.");
    }

    // ---- change branch ----

    public async Task<CloneStatus> SwitchBranchAsync(
        MonitoredAccount account,
        MonitoredRepo repo,
        string rootDirectory,
        CloneLayout layout,
        string branch,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new InvalidOperationException("Choose a branch to switch to.");

        var path = this.Prepare(rootDirectory, repo, layout);
        await runtime.EnsureAvailableAsync(ct).ConfigureAwait(false);

        // Local only: switching to a branch already in the working copy shouldn't need the network,
        // and a branch the user picked came from a list this same read produced.
        var before = await this.InspectAsync(account, repo, path, fetchRemote: false, progress, ct).ConfigureAwait(false);
        if (!before.IsWorkingCopy)
            throw new InvalidOperationException(before.Detail);

        if (string.Equals(before.Branch, branch, StringComparison.Ordinal))
            return before;

        // A checkout would refuse anything that overwrites a modified file anyway, but it would
        // happily carry the rest across to the new branch — which is a surprise, not a feature,
        // when the switch was one click on a list of twenty repos.
        if (before.Modified > 0)
            throw new InvalidOperationException(
                $"{repo.FullName} has {before.Modified} uncommitted change{(before.Modified == 1 ? "" : "s")} — commit or stash them before switching branch.");

        Report(progress, $"Switching {repo.FullName} to {branch}…");

        var switched = await GitCallbacks.Run(() =>
        {
            using var git = new Repository(path);
            return TrySwitch(git, branch);
        }, ct).ConfigureAwait(false);

        if (!switched)
            throw new InvalidOperationException($"Couldn't switch {repo.FullName} to '{branch}' — no such branch locally or on a remote.");

        var after = await this.InspectAsync(account, repo, path, fetchRemote: false, progress, ct).ConfigureAwait(false);
        Report(progress, after.Detail);
        return after;
    }

    /// <summary>
    /// Checks <paramref name="branch"/> out, creating a local tracking branch from a remote when it
    /// doesn't exist locally yet. False (rather than a throw) when no branch of that name exists at
    /// all, which is a legitimate answer on the clone path.
    /// </summary>
    static bool TrySwitch(Repository git, string branch)
    {
        if (git.Branches[branch] is { IsRemote: false } local)
        {
            // Looked up by exact name rather than by revparse, so a typo can't be resolved into
            // some remote's branch and silently create a new local one.
            Commands.Checkout(git, local);
            return true;
        }

        var source = ListBranches(git).FirstOrDefault(b => string.Equals(b.Name, branch, StringComparison.Ordinal));
        if (source?.Remote is not { } remote)
            return false;

        var tracked = git.Branches[$"{remote}/{branch}"];
        if (tracked?.Tip is null)
            return false;

        var created = git.Branches.Add(branch, tracked.Tip);
        git.Branches.Update(created, b => b.TrackedBranch = tracked.CanonicalName);
        Commands.Checkout(git, created);
        return true;
    }

    /// <summary>
    /// Every branch this working copy could be switched to — local heads plus remote-tracking refs,
    /// merged by name so a branch that exists both places appears once. <c>origin</c> wins when the
    /// same name is on more than one remote, since that's the one a plain clone set up.
    /// </summary>
    static IReadOnlyList<CloneBranch> ListBranches(Repository git)
    {
        var found = new Dictionary<string, CloneBranch>(StringComparer.Ordinal);

        foreach (var reference in git.Refs)
        {
            var line = reference.CanonicalName;

            if (line.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                var name = line["refs/heads/".Length..];
                if (name.Length == 0)
                    continue;

                found[name] = found.TryGetValue(name, out var existing)
                    ? existing with { Local = true }
                    : new CloneBranch(name, Local: true, Remote: null);
            }
            else if (line.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                // refs/remotes/<remote>/<branch>, where <branch> may itself contain slashes.
                var rest = line["refs/remotes/".Length..];
                var slash = rest.IndexOf('/');
                if (slash <= 0)
                    continue;

                var remote = rest[..slash];
                var name = rest[(slash + 1)..];

                // refs/remotes/<remote>/HEAD is a symref to the remote's default branch, not a
                // branch anyone can check out by that name.
                if (name.Length == 0 || name == "HEAD")
                    continue;

                if (!found.TryGetValue(name, out var existing))
                    found[name] = new CloneBranch(name, Local: false, Remote: remote);
                else if (existing.Remote is null || (existing.Remote != "origin" && remote == "origin"))
                    found[name] = existing with { Remote = remote };
            }
        }

        return found.Values
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The checked-out branch, or null when HEAD is detached.</summary>
    static string? CurrentBranch(Repository git)
    {
        if (git.Info.IsHeadDetached)
            return null;

        // Unborn HEAD (a repo with no commits yet) still names the branch it would create, which is
        // what `symbolic-ref HEAD` reports too.
        var name = git.Head.FriendlyName;
        return string.IsNullOrWhiteSpace(name) || name == "(no branch)" ? null : name;
    }

    /// <summary>
    /// The ref <paramref name="branch"/> should be compared against: its configured upstream, or
    /// <c>&lt;remote&gt;/&lt;branch&gt;</c> when it has none (the common case for a branch created
    /// locally and pushed with a plain <c>git push</c>). Null when neither exists — a purely local
    /// branch has nothing to catch up to.
    /// </summary>
    static string? ResolveUpstream(Repository git, string branch, string remote)
    {
        if (git.Head.TrackedBranch?.FriendlyName is { Length: > 0 } tracked)
            return tracked;

        var fallback = $"{remote}/{branch}";
        return git.Branches[fallback] is { IsRemote: true } ? fallback : null;
    }

    /// <summary>Commits on HEAD the upstream lacks, and commits on the upstream HEAD lacks.</summary>
    static (int Ahead, int Behind) Count(Repository git, string branch, string upstream)
    {
        var local = git.Head.Tip;
        var target = git.Branches[upstream]?.Tip;

        // An unborn branch has no tip: nothing local, and everything on the remote is "behind".
        if (local is null || target is null)
            return (0, 0);

        var divergence = git.ObjectDatabase.CalculateHistoryDivergence(local, target);

        // Null means the two have no common ancestor at all, which no count describes honestly.
        if (divergence.AheadBy is not { } ahead || divergence.BehindBy is not { } behind)
            throw new InvalidOperationException(
                $"'{branch}' and '{upstream}' have unrelated histories, so there's nothing to catch up to.");

        return (ahead, behind);
    }

    // ---- plumbing ----

    /// <summary>Validates the root and resolves where this repo lives under it.</summary>
    string Prepare(string rootDirectory, MonitoredRepo repo, CloneLayout layout)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new InvalidOperationException("Choose a folder to clone into first.");

        return this.ResolvePath(rootDirectory, repo, layout);
    }

    /// <summary>Whether the destination has nothing in it — missing, or present but empty.</summary>
    static bool IsEmpty(string path)
        => !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();

    /// <summary>
    /// The account's HTTPS credential, or null when there's no usable token. Null is not fatal —
    /// a public repo fetches fine without one, and libgit2 fails clearly if it turns out to matter.
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

    static string Commits(int count) => $"{count} commit{(count == 1 ? "" : "s")}";

    /// <summary>Folds a standalone sentence into the middle of a longer one.</summary>
    static string Lower(string sentence)
        => sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence[1..];

    static CloneOutcome Skip(CloneAction action, CloneStatus status, IProgress<string>? progress, string summary)
        => Done(action, status, 0, 0, progress, summary);

    static CloneOutcome Done(CloneAction action, CloneStatus status, int ahead, int behind, IProgress<string>? progress, string summary)
    {
        Report(progress, summary);
        return new CloneOutcome(action, status.Path, status.Branch, ahead, behind, summary);
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
