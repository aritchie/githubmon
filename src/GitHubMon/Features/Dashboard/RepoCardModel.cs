using Shiny.Blazor.Controls;

namespace GitHubMon.Dashboard;

/// <summary>
/// Per-repo display state for the dashboard grid. Mutable on purpose — the
/// dashboard component re-renders after Apply, so no change notification needed.
/// </summary>
public sealed class RepoCardModel(MonitoredAccount account, MonitoredRepo repo)
{
    public MonitoredAccount Account { get; } = account;
    public MonitoredRepo Repo { get; } = repo;

    public string Key => $"{Account.Id}|{Repo.FullName}";
    public string AccountLabel => Account.Label;
    public string Title => Repo.FullName;

    public string IssuesUrl => $"https://github.com/{Repo.FullName}/issues";
    public string PullsUrl => $"https://github.com/{Repo.FullName}/pulls";
    public string ActionsUrl => $"https://github.com/{Repo.FullName}/actions";

    /// <summary>The latest workflow run's page (the failure detail when it failed); null until a run is seen.</summary>
    public string? LatestRunUrl { get; private set; }

    public int OpenIssues { get; private set; }
    public int OpenPullRequests { get; private set; }
    public int Stars { get; private set; }
    public int Forks { get; private set; }
    public int Watchers { get; private set; }
    public string? LatestRunStatus { get; private set; }
    public bool LatestRunSucceeded { get; private set; }
    public bool LatestRunFailed { get; private set; }
    public PillType LatestRunPillType { get; private set; } = PillType.Info;
    public string? LatestRunDescription { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool HasError { get; private set; }

    public void Apply(RepoSnapshot snap)
    {
        ErrorMessage = snap.ErrorMessage;
        HasError = snap.HasError;
        LastUpdated = snap.FetchedAt;
        if (snap.HasError)
            return;

        OpenIssues = snap.OpenIssues;
        OpenPullRequests = snap.OpenPullRequests.Count;
        Stars = snap.Stars;
        Forks = snap.Forks;
        Watchers = snap.Watchers;

        // The latest run is the most useful signal for the dashboard's build totals,
        // so flag whether this repo's newest run is currently a success or a failure.
        var head = snap.RecentWorkflowRuns.FirstOrDefault();
        LatestRunSucceeded = head?.IsSuccess ?? false;
        LatestRunFailed = head?.IsFailed ?? false;

        var latest = snap.RecentWorkflowRuns.FirstOrDefault();
        if (latest is null)
        {
            LatestRunStatus = "No runs";
            LatestRunPillType = PillType.Info;
            LatestRunDescription = "—";
            LatestRunUrl = null;
            return;
        }

        LatestRunUrl = string.IsNullOrEmpty(latest.HtmlUrl) ? null : latest.HtmlUrl;

        if (latest.IsFailed)
        {
            LatestRunStatus = "Failed";
            LatestRunPillType = PillType.Critical;
        }
        else if (latest.IsInProgress)
        {
            LatestRunStatus = "Running";
            LatestRunPillType = PillType.Warning;
        }
        else if (latest.IsSuccess)
        {
            LatestRunStatus = "Success";
            LatestRunPillType = PillType.Success;
        }
        else
        {
            LatestRunStatus = latest.Conclusion ?? latest.Status;
            LatestRunPillType = PillType.Info;
        }
        LatestRunDescription = $"{latest.WorkflowName} • {latest.Branch}";
    }
}
