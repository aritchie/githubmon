namespace GitHubShine.Clone;

/// <summary>What a single clone-or-update run did to one working copy.</summary>
public enum CloneAction
{
    /// <summary>The folder didn't exist (or was empty), so the repo was cloned onto its default branch.</summary>
    Cloned,

    /// <summary>The checked-out branch was fast-forwarded onto its upstream.</summary>
    Updated,

    /// <summary>Already level with its upstream — nothing to do.</summary>
    UpToDate,

    /// <summary>Local commits that aren't on the upstream, and nothing to pull. Left alone.</summary>
    AheadOnly,

    /// <summary>Tracked files are modified or staged, so pulling could clobber work in progress.</summary>
    SkippedDirty,

    /// <summary>Both sides have commits the other doesn't — merging or rebasing is the user's call.</summary>
    SkippedDiverged,

    /// <summary>HEAD is detached, so there's no branch to fast-forward.</summary>
    SkippedDetached,

    /// <summary>The checked-out branch has no matching branch on <c>origin</c>.</summary>
    SkippedNoUpstream,

    /// <summary>The folder exists but isn't a git working copy.</summary>
    SkippedNotARepo,

    /// <summary>It's a git working copy, but it has no <c>origin</c> remote to fetch from.</summary>
    SkippedNoRemote,

    /// <summary><c>origin</c> points at some other repo — almost certainly not the one asked for.</summary>
    SkippedDifferentRepo
}

/// <summary>The result of one <see cref="IRepoCloneEngine.CloneOrUpdateAsync"/> call.</summary>
/// <param name="Path">The working copy's folder, whether or not anything was written to it.</param>
/// <param name="Branch">The branch that was cloned or fast-forwarded, when one could be determined.</param>
/// <param name="Ahead">Commits on the local branch that the upstream doesn't have.</param>
/// <param name="Behind">Commits on the upstream that the local branch didn't have before this run.</param>
/// <param name="Summary">One line, written for a human to read in the UI.</param>
public sealed record CloneOutcome(
    CloneAction Action,
    string Path,
    string? Branch,
    int Ahead,
    int Behind,
    string Summary)
{
    /// <summary>True when this run actually wrote commits to disk.</summary>
    public bool Changed => this.Action is CloneAction.Cloned or CloneAction.Updated;

    /// <summary>
    /// True when the working copy was deliberately left untouched. These are not failures — each
    /// one is a state only the user can resolve — so a run reports them apart from its errors.
    /// </summary>
    public bool Skipped => this.Action
        is CloneAction.SkippedDirty
        or CloneAction.SkippedDiverged
        or CloneAction.SkippedDetached
        or CloneAction.SkippedNoUpstream
        or CloneAction.SkippedNotARepo
        or CloneAction.SkippedNoRemote
        or CloneAction.SkippedDifferentRepo;
}
