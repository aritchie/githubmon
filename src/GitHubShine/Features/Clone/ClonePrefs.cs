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
public sealed record ClonePrefs(
    string Id,
    string? RootDirectory = null,
    CloneLayout Layout = CloneLayout.OwnerAndName,
    DateTimeOffset? LastRunUtc = null)
{
    public const string DefaultId = "default";

    public static ClonePrefs Default => new(DefaultId);
}
