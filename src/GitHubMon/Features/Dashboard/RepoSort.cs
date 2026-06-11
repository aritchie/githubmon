namespace GitHubMon.Dashboard;

/// <summary>
/// How the repository list is ordered. Shared by the dashboard sidebar and the
/// tray-icon "Repositories" submenu so they always show the same arrangement.
/// </summary>
public enum RepoSort
{
    Stars,
    Forks,
    OpenIssues,
    OpenPullRequests,
    BuildState
}

public static class RepoSortInfo
{
    public static IReadOnlyList<RepoSort> All { get; } = Enum.GetValues<RepoSort>();

    public static string Label(RepoSort sort) => sort switch
    {
        RepoSort.Forks => "Most forks",
        RepoSort.OpenIssues => "Most open issues",
        RepoSort.OpenPullRequests => "Most open PRs",
        RepoSort.BuildState => "Build status",
        _ => "Most stars",
    };
}
