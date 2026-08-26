namespace GitHubShine.Dashboard;

/// <summary>
/// Which presentation the dashboard's repository list is using. The two are the same repos and
/// the same live snapshots — only the layout differs, so this is a view preference, not a mode
/// with its own state.
/// </summary>
public enum RepoViewMode
{
    /// <summary>Reorderable cards, one per repo — the default, and the only view that can reorder.</summary>
    Cards = 0,

    /// <summary>A sortable grid, one row per repo — the view for comparing numbers across repos.</summary>
    Table = 1
}
