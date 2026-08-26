namespace GitHubShine.Sync;

/// <summary>
/// Which refs a mapping pushes to its target.
/// </summary>
public enum SyncBranchMode
{
    /// <summary>Just the source repo's default branch, resolved live from the API each run.</summary>
    DefaultBranch = 0,

    /// <summary>Only the branches named in <see cref="SyncMapping.Branches"/>.</summary>
    Named = 1,

    /// <summary>Every branch on the source.</summary>
    All = 2
}

/// <summary>
/// One persisted source-repo → target-repo sync. The source must be a repo already linked to a
/// monitored account; the target is any account the app knows about (typically a self-hosted
/// Gitea acting as a backup). Stored in the DocumentDb alongside accounts, so a database backup
/// carries the sync setup with it.
/// </summary>
public sealed record SyncMapping(
    string Id,
    string SourceAccountId,
    MonitoredRepo Source,
    string TargetAccountId,
    string TargetOwner,
    // Null means "same name as the source repo".
    string? TargetName = null,
    SyncBranchMode BranchMode = SyncBranchMode.DefaultBranch,
    List<string>? Branches = null,
    bool IncludeTags = true,
    // Force-update refs on the target (and, in All mode, delete target refs the source no longer
    // has). Off by default so a diverged target fails loudly instead of losing commits.
    bool Force = false,
    // Whether the scheduled background pass may run this mapping. On by default so an existing
    // (and every newly created) sync keeps auto-syncing; turning it off leaves the sync fully
    // usable by hand, it just stops being picked up unattended.
    bool AutoSync = true,
    DateTimeOffset? LastSyncedUtc = null,
    string? LastResult = null)
{
    public string EffectiveTargetName => string.IsNullOrWhiteSpace(this.TargetName)
        ? this.Source.Name
        : this.TargetName.Trim();

    public MonitoredRepo TargetRepo => new(this.TargetOwner, this.EffectiveTargetName);

    public string TargetFullName => $"{this.TargetOwner}/{this.EffectiveTargetName}";

    public string BranchSummary => this.BranchMode switch
    {
        SyncBranchMode.All => "All branches",
        SyncBranchMode.Named => this.Branches is { Count: > 0 }
            ? string.Join(", ", this.Branches)
            : "No branches selected",
        _ => "Default branch"
    };
}
