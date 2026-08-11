namespace GitHubShine.Clone;

/// <summary>Where a repo lands underneath the chosen root folder.</summary>
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
/// <param name="InitialBranch">
/// The branch a freshly cloned repo is left on. Null (the default) means each repo's own default
/// branch, and so does a name a given repo doesn't have — a preference for "develop" across the
/// board shouldn't fail the clone of a repo that never had one. Only ever applies to a first
/// clone; an existing working copy stays on whatever branch the user put it on.
/// </param>
public sealed record ClonePrefs(
    string Id,
    string? RootDirectory = null,
    CloneLayout Layout = CloneLayout.OwnerAndName,
    DateTimeOffset? LastRunUtc = null,
    string? InitialBranch = null)
{
    public const string DefaultId = "default";

    public static ClonePrefs Default => new(DefaultId);
}
