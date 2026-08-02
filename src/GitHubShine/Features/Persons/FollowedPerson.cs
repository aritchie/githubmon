using GitHubShine.Providers;

namespace GitHubShine.Persons;

/// <summary>
/// A person or organisation being watched from the people grid. Following here is purely local:
/// nothing is written to the host, so an account can watch a login it doesn't follow there (and
/// following someone there doesn't add a row here).
/// </summary>
/// <remarks>
/// Rows are scoped to an account rather than global because a login is only meaningful against a
/// host, and the account is what supplies the token and base URL to read it with. The same name on
/// two hosts is therefore two rows, which is correct — they're two different people.
/// </remarks>
/// <param name="Kind">
/// Resolved when the login was added and refreshed on every poll. Stored so a poll can go straight
/// to the org- or user-shaped endpoints instead of probing for the kind first.
/// </param>
/// <param name="DisplayName">
/// The profile name as it read when added, so a newly added row has something to show before its
/// first snapshot lands. The live value comes off the snapshot, not from here.
/// </param>
public sealed record FollowedPerson(
    string Id,
    string AccountId,
    string Login,
    GitPersonKind Kind,
    string? DisplayName = null,
    DateTimeOffset? FollowedUtc = null)
{
    /// <summary>
    /// The document id: account plus login, case-folded because hosts treat logins
    /// case-insensitively and "Foo" must not become a second row alongside "foo".
    /// </summary>
    public static string MakeId(string accountId, string login)
        => $"{accountId}|{login.Trim().ToLowerInvariant()}";
}
