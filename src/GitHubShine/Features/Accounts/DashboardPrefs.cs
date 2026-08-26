namespace GitHubShine.Accounts;

/// <summary>
/// Dashboard preferences persisted in the document store (NOT the key/value
/// prefs store) so a raw SQLite backup carries the card order along with
/// accounts and tokens — and, on the desktop heads where the key/value store is
/// in-memory only, so they survive a restart at all. Single row, fixed id.
/// </summary>
/// <remarks>
/// Deliberately a plain DTO: every member here is written to the document, so
/// interpretation (defaults, validation) belongs in ConfigStore, not on this type.
/// </remarks>
/// <param name="SortColumnId">
/// The repositories grid's remembered sort column, or null when it has never been sorted.
/// Nullable rather than defaulted so a row written before these fields existed
/// deserializes cleanly.
/// </param>
/// <param name="RepoView">
/// Which repository view the dashboard was last showing (<see cref="RepoViewMode"/>, stored by
/// name). Null — a row written before this field existed, or a name that no longer parses —
/// falls back to cards.
/// </param>
/// <param name="PersonSortColumnId">
/// As <paramref name="SortColumnId"/>, for the people grid. A separate pair rather than a shared
/// one because the two grids have disjoint column sets — a sort restored from the other grid
/// could only ever fall back to its default.
/// </param>
public sealed record DashboardPrefs(
    string Id,
    List<string> RepoOrder,
    string? SortColumnId = null,
    bool? SortDescending = null,
    string? RepoView = null,
    string? PersonSortColumnId = null,
    bool? PersonSortDescending = null
)
{
    public const string DefaultId = "default";
}
