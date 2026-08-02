namespace GitHubShine.Dashboard;

/// <summary>
/// The repositories grid's last-used sort — a DataGrid column id plus its direction.
/// Persisted so the grid, and the tray-icon "Repositories" submenu that follows it,
/// come back ordered the way they were left.
/// </summary>
public sealed record RepoGridSort(string ColumnId, bool Descending)
{
    /// <summary>Most stars first — what a profile with no saved sort shows.</summary>
    public static readonly RepoGridSort Default = new(RepoGridColumns.Stars, true);

    /// <summary>
    /// Applies this sort to a set of cards. Repo name is always the tie-break: most repos
    /// share a metric value (zero open PRs, zero forks), and without it equal rows shuffle
    /// between renders.
    /// </summary>
    public List<RepoCardModel> Order(IEnumerable<RepoCardModel> cards)
    {
        var ordered = this.ColumnId switch
        {
            RepoGridColumns.Repository => this.By(cards, c => c.Title, StringComparer.OrdinalIgnoreCase),
            RepoGridColumns.Account => this.By(cards, c => c.AccountLabel, StringComparer.OrdinalIgnoreCase),
            RepoGridColumns.Visibility => this.By(cards, c => c.VisibilityRank),
            RepoGridColumns.Forks => this.By(cards, c => c.Forks),
            RepoGridColumns.Watchers => this.By(cards, c => c.Watchers),
            RepoGridColumns.OpenIssues => this.By(cards, c => c.OpenIssues),
            RepoGridColumns.OpenPullRequests => this.By(cards, c => c.OpenPullRequests),
            RepoGridColumns.Build => this.By(cards, c => c.BuildRank),
            _ => this.By(cards, c => c.Stars)
        };
        return ordered.ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    IOrderedEnumerable<RepoCardModel> By<TKey>(
        IEnumerable<RepoCardModel> cards,
        Func<RepoCardModel, TKey> key,
        IComparer<TKey>? comparer = null
    ) => this.Descending
        ? cards.OrderByDescending(key, comparer)
        : cards.OrderBy(key, comparer);
}

/// <summary>
/// The repositories grid's columns. These constants ARE the DataGrid's own column ids:
/// a <c>PropertyColumn</c> derives its id from the member name of its Property expression,
/// so each one is a <c>nameof</c> over the <see cref="RepoCardModel"/> member its column
/// binds to — rename the member and this breaks the build instead of silently orphaning
/// a persisted sort.
/// </summary>
public static class RepoGridColumns
{
    public const string Repository = nameof(RepoCardModel.Title);
    public const string Account = nameof(RepoCardModel.AccountLabel);
    public const string Visibility = nameof(RepoCardModel.VisibilityRank);
    public const string Stars = nameof(RepoCardModel.Stars);
    public const string Forks = nameof(RepoCardModel.Forks);
    public const string Watchers = nameof(RepoCardModel.Watchers);
    public const string OpenIssues = nameof(RepoCardModel.OpenIssues);
    public const string OpenPullRequests = nameof(RepoCardModel.OpenPullRequests);
    public const string Build = nameof(RepoCardModel.BuildRank);

    /// <summary>Guards a persisted id against a column that no longer exists.</summary>
    public static bool IsKnown(string columnId) => columnId is
        Repository or Account or Visibility or Stars or Forks or Watchers or OpenIssues or OpenPullRequests or Build;

    /// <summary>The column's header text — also how the sort is described outside the grid.</summary>
    public static string Label(string columnId) => columnId switch
    {
        Repository => "Repository",
        Account => "Account",
        Visibility => "Visibility",
        Forks => "Forks",
        Watchers => "Watchers",
        OpenIssues => "Open issues",
        OpenPullRequests => "Open PRs",
        Build => "Build",
        _ => "Stars"
    };
}
