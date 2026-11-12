namespace GitHubShine.Clone;

/// <summary>Where a newly added entry's folder is suggested underneath the chosen root folder.</summary>
public enum CloneLayout
{
    /// <summary><c>&lt;root&gt;/&lt;owner&gt;/&lt;name&gt;</c> — the default, because two monitored
    /// accounts can easily both watch a repo called "docs".</summary>
    OwnerAndName = 0,

    /// <summary><c>&lt;root&gt;/&lt;name&gt;</c> — flatter, at the cost of repos from different
    /// owners colliding on one folder.</summary>
    NameOnly = 1
}

/// <summary>
/// The clone page's remembered settings. Lives in the document store rather than the key/value
/// prefs store for the same two reasons as <see cref="DashboardPrefs"/>: the desktop heads' key/value
/// store is in-memory (so a folder chosen once would be forgotten on restart), and a raw SQLite
/// backup then carries it along with the accounts it belongs to. Single row, fixed id.
/// </summary>
/// <param name="RootDirectory">
/// The folder chosen to clone into, null until one has been. Desktop only: mobile can only write
/// inside its own sandbox, so it offers no folder picker and always uses
/// <see cref="AppPaths.CloneDirectory"/> — a path the OS assigns and can move between installs,
/// which is why it isn't stored here.
/// </param>
/// <param name="Layout">The folder suggested for an entry when it's added. Existing entries keep theirs.</param>
/// <param name="InitialBranch">
/// The branch suggested for an entry when it's added. Null (the default) means each repo's own
/// default branch. Existing entries keep theirs.
/// </param>
/// <param name="TargetsSeeded">
/// Whether the monitored repos have been turned into <see cref="CloneTarget"/>s once already.
/// The page used to list every monitored repo implicitly; the first visit since entries became
/// explicit carries that list over, and this stops it doing so again after the user has pruned
/// the entries down to the ones they want.
/// </param>
public sealed record ClonePrefs(
    string Id,
    string? RootDirectory = null,
    CloneLayout Layout = CloneLayout.OwnerAndName,
    DateTimeOffset? LastRunUtc = null,
    string? InitialBranch = null,
    bool TargetsSeeded = false)
{
    public const string DefaultId = "default";

    public static ClonePrefs Default => new(DefaultId);
}
