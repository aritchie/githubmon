namespace GitHubShine.Clone;

/// <summary>
/// The shape a working copy is in, as far as it decides what can be done to it. Ordered from
/// "ready to work with" to "can't be touched": everything below <see cref="NoUpstream"/> means the
/// folder isn't something this page should fetch into at all.
/// </summary>
public enum CloneStatusKind
{
    /// <summary>Nothing at the destination yet — a run would clone it.</summary>
    NotCloned,

    /// <summary>On a branch with a remote counterpart, so ahead/behind counts mean something.</summary>
    Tracking,

    /// <summary>On a branch that exists only locally — there's nothing to compare it against.</summary>
    NoUpstream,

    /// <summary>HEAD isn't on a branch, so there's no branch to fast-forward.</summary>
    Detached,

    /// <summary>The folder exists but isn't a git working copy.</summary>
    NotARepo,

    /// <summary>A working copy with no <c>origin</c> remote to fetch from.</summary>
    NoRemote,

    /// <summary><c>origin</c> points at some other repo entirely.</summary>
    DifferentRepo
}

/// <summary>
/// A branch the working copy can be switched to: one that exists locally, one that only exists on
/// a remote (switching creates a local tracking branch for it), or both.
/// </summary>
/// <param name="Remote">
/// The remote it was seen on, or null for a purely local branch. <c>origin</c> wins when a branch
/// of the same name is on more than one.
/// </param>
public sealed record CloneBranch(string Name, bool Local, string? Remote)
{
    /// <summary>Only on a remote so far — checking it out means creating a tracking branch.</summary>
    public bool RemoteOnly => !this.Local && this.Remote is not null;
}

/// <summary>
/// What one working copy looks like right now: which branch it's on, what else it could be on, and
/// how far that branch has drifted from its upstream. Read-only — nothing here changes a repo.
/// </summary>
/// <param name="Branch">The checked-out branch, or null when HEAD is detached or there's no repo.</param>
/// <param name="Upstream">The ref <paramref name="Branch"/> is compared against, when it has one.</param>
/// <param name="Ahead">Commits on the local branch its upstream doesn't have.</param>
/// <param name="Behind">Commits on the upstream the local branch doesn't have — what a pull would bring in.</param>
/// <param name="Modified">Changed or staged tracked files. Any at all blocks a fast-forward.</param>
/// <param name="Untracked">Untracked files, which never block anything (see <see cref="RepoCloneEngine"/>).</param>
/// <param name="Branches">Every branch this working copy could be switched to, local and remote.</param>
/// <param name="Fetched">Whether the remote was contacted, so the counts are current rather than from the last fetch.</param>
/// <param name="Detail">One line, written for a human to read in the UI.</param>
public sealed record CloneStatus(
    CloneStatusKind Kind,
    string Path,
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    int Modified,
    int Untracked,
    IReadOnlyList<CloneBranch> Branches,
    bool Fetched,
    string Detail)
{
    /// <summary>A state with nothing to report beyond why — no branch, no counts, no branch list.</summary>
    public static CloneStatus Plain(CloneStatusKind kind, string path, string detail)
        => new(kind, path, null, null, 0, 0, 0, 0, [], false, detail);

    /// <summary>True when the folder holds a git working copy pointed at the right repo.</summary>
    public bool IsWorkingCopy => this.Kind is CloneStatusKind.Tracking or CloneStatusKind.NoUpstream or CloneStatusKind.Detached;

    /// <summary>Tracked files have been touched, so a pull could clobber work in progress.</summary>
    public bool Dirty => this.Modified > 0;

    /// <summary>True when a run would actually move this working copy forward.</summary>
    public bool CanFastForward => this.Kind == CloneStatusKind.Tracking && this.Behind > 0 && this.Ahead == 0 && this.Modified == 0;

    /// <summary>
    /// True when this repo has something to collect: it isn't cloned yet, or its branch is behind.
    /// A branch that's behind <em>and</em> dirty or diverged still counts — the user asked to see
    /// what's out of date, and a run reports why it couldn't be moved rather than hiding it.
    /// </summary>
    public bool NeedsUpdate => this.Kind == CloneStatusKind.NotCloned || this.Behind > 0;
}
