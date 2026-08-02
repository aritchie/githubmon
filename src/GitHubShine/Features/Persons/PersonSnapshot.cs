using GitHubShine.Providers;

namespace GitHubShine.Persons;

/// <summary>
/// One poll's worth of counters for a followed login — the people grid's equivalent of
/// <see cref="RepoSnapshot"/>, and cached the same way so a cold start paints last-known
/// numbers before any network call.
/// </summary>
/// <remarks>
/// Every counter is nullable except <see cref="TotalStars"/>: the two backends publish different
/// subsets (Gitea has no gists; neither reports followers for an organisation), and a column
/// should read "—" where a number was never available rather than a zero that looks measured.
/// </remarks>
public sealed record PersonSnapshot(
    string AccountId,
    string Login,
    GitPersonKind Kind,
    DateTimeOffset FetchedAt,
    string? DisplayName,
    string? AvatarUrl,
    string? Bio,
    string? Location,
    string? Company,
    string? WebsiteUrl,
    int? Followers,
    int? Following,
    int? PublicRepos,
    int? PublicGists,
    int TotalStars,
    int? MemberCount,
    DateTimeOffset? JoinedAt,
    string? ErrorMessage,
    // True when the listing behind the number stopped at its cap, so the number is a floor.
    // See GitPersonSnapshotData — the grid renders these as "n+".
    bool StarsTruncated = false,
    bool MembersTruncated = false)
{
    public bool HasError => ErrorMessage is not null;

    public bool IsOrganization => Kind == GitPersonKind.Organization;

    public static PersonSnapshot Empty(FollowedPerson person, string? error = null)
        => new(person.AccountId, person.Login, person.Kind, DateTimeOffset.UtcNow,
            person.DisplayName, null, null, null, null, null,
            null, null, null, null, 0, null, null, error);
}
