namespace GitHubShine.Persons;

/// <summary>
/// The people grid's last-used sort — a DataGrid column id plus its direction. Persisted
/// alongside the repositories grid's sort so the page comes back ordered the way it was left.
/// </summary>
public sealed record PersonGridSort(string ColumnId, bool Descending)
{
    /// <summary>Most followers first — what a profile with no saved sort shows.</summary>
    public static readonly PersonGridSort Default = new(PersonGridColumns.Followers, true);

    /// <summary>
    /// Applies this sort. Login is always the tie-break: most of these counters are null or zero
    /// for most rows, and without it equal rows shuffle between renders.
    /// </summary>
    public List<PersonCardModel> Order(IEnumerable<PersonCardModel> cards)
    {
        var ordered = this.ColumnId switch
        {
            PersonGridColumns.Person => this.By(cards, c => c.Login, StringComparer.OrdinalIgnoreCase),
            PersonGridColumns.Type => this.By(cards, c => c.TypeLabel, StringComparer.OrdinalIgnoreCase),
            PersonGridColumns.Account => this.By(cards, c => c.AccountLabel, StringComparer.OrdinalIgnoreCase),
            PersonGridColumns.Following => this.By(cards, c => c.Following),
            PersonGridColumns.Repos => this.By(cards, c => c.PublicRepos),
            PersonGridColumns.Stars => this.By(cards, c => c.TotalStars),
            PersonGridColumns.Gists => this.By(cards, c => c.Gists),
            PersonGridColumns.Members => this.By(cards, c => c.Members),
            PersonGridColumns.Joined => this.By(cards, c => c.JoinedAt),
            PersonGridColumns.Location => this.By(cards, c => c.Location, StringComparer.OrdinalIgnoreCase),
            _ => this.By(cards, c => c.Followers)
        };
        return ordered.ThenBy(c => c.Login, StringComparer.OrdinalIgnoreCase).ToList();
    }

    IOrderedEnumerable<PersonCardModel> By<TKey>(
        IEnumerable<PersonCardModel> cards,
        Func<PersonCardModel, TKey> key,
        IComparer<TKey>? comparer = null
    ) => this.Descending
        ? cards.OrderByDescending(key, comparer)
        : cards.OrderBy(key, comparer);
}

/// <summary>
/// The people grid's columns. As with <see cref="RepoGridColumns"/> these constants ARE the
/// DataGrid's own column ids — a <c>PropertyColumn</c> takes its id from the member name in its
/// Property expression, so each is a <c>nameof</c> over the <see cref="PersonCardModel"/> member
/// its column binds to. Rename the member and this breaks the build instead of silently
/// orphaning a persisted sort.
/// </summary>
public static class PersonGridColumns
{
    public const string Person = nameof(PersonCardModel.Login);
    public const string Type = nameof(PersonCardModel.TypeLabel);
    public const string Account = nameof(PersonCardModel.AccountLabel);
    public const string Followers = nameof(PersonCardModel.Followers);
    public const string Following = nameof(PersonCardModel.Following);
    public const string Repos = nameof(PersonCardModel.PublicRepos);
    public const string Stars = nameof(PersonCardModel.TotalStars);
    public const string Gists = nameof(PersonCardModel.Gists);
    public const string Members = nameof(PersonCardModel.Members);
    public const string Joined = nameof(PersonCardModel.JoinedAt);
    public const string Location = nameof(PersonCardModel.Location);

    /// <summary>Guards a persisted id against a column that no longer exists.</summary>
    public static bool IsKnown(string columnId) => columnId is
        Person or Type or Account or Followers or Following or Repos
        or Stars or Gists or Members or Joined or Location;

    /// <summary>The column's header text — also how the sort is described outside the grid.</summary>
    public static string Label(string columnId) => columnId switch
    {
        Person => "Person",
        Type => "Type",
        Account => "Account",
        Following => "Following",
        Repos => "Repos",
        Stars => "Stars",
        Gists => "Gists",
        Members => "Members",
        Joined => "Joined",
        Location => "Location",
        _ => "Followers"
    };
}
