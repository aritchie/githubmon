namespace GitHubShine.Dashboard;

public sealed record RepoSnapshot(
    string AccountId,
    MonitoredRepo Repo,
    DateTimeOffset FetchedAt,
    int OpenIssues,
    int Stars,
    int Forks,
    int Watchers,
    IReadOnlyList<PullRequestSummary> OpenPullRequests,
    IReadOnlyList<WorkflowRunSummary> RecentWorkflowRuns,
    string? ErrorMessage)
{
    public bool HasError => ErrorMessage is not null;

    public static RepoSnapshot Empty(string accountId, MonitoredRepo repo, string? error = null)
        => new(accountId, repo, DateTimeOffset.UtcNow, 0, 0, 0, 0,
            Array.Empty<PullRequestSummary>(), Array.Empty<WorkflowRunSummary>(), error);
}

public sealed record PullRequestSummary(
    int Number,
    string Title,
    string Author,
    string HtmlUrl,
    bool Draft,
    bool Mergeable);

public sealed record WorkflowRunSummary(
    long Id,
    string WorkflowName,
    string Branch,
    string Status,
    string? Conclusion,
    DateTimeOffset CreatedAt,
    string HtmlUrl)
{
    public bool IsFailed => string.Equals(Conclusion, "failure", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Conclusion, "timed_out", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Conclusion, "startup_failure", StringComparison.OrdinalIgnoreCase);
    public bool IsInProgress => string.Equals(Status, "in_progress", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(Status, "queued", StringComparison.OrdinalIgnoreCase);
    public bool IsSuccess => string.Equals(Conclusion, "success", StringComparison.OrdinalIgnoreCase);
}
