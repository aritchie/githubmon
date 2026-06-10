namespace GitHubMon.Accounts;

/// <summary>
/// Dashboard preferences persisted in the document store (NOT the key/value
/// prefs store) so a raw SQLite backup carries the card order along with
/// accounts and tokens. Single row, fixed id.
/// </summary>
public sealed record DashboardPrefs(string Id, List<string> RepoOrder)
{
    public const string DefaultId = "default";
}
