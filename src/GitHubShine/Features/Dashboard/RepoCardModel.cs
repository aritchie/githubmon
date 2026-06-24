using Shiny.Blazor.Controls;

namespace GitHubShine.Dashboard;

/// <summary>
/// Per-repo display state for the dashboard grid. Mutable on purpose — the
/// dashboard folds each snapshot in via Apply, which reports whether a rendered
/// field changed so the page can skip re-rendering on an unchanged poll.
/// </summary>
public sealed class RepoCardModel(MonitoredAccount account, MonitoredRepo repo)
{
    public MonitoredAccount Account { get; } = account;
    public MonitoredRepo Repo { get; } = repo;

    public string Key => $"{Account.Id}|{Repo.FullName}";
    public string AccountLabel => Account.Label;
    public string Title => Repo.FullName;

    public string RepoUrl => $"https://github.com/{Repo.FullName}";
    public string IssuesUrl => $"https://github.com/{Repo.FullName}/issues";
    public string PullsUrl => $"https://github.com/{Repo.FullName}/pulls";
    public string ActionsUrl => $"https://github.com/{Repo.FullName}/actions";
    public string StarsUrl => $"https://github.com/{Repo.FullName}/stargazers";
    public string ForksUrl => $"https://github.com/{Repo.FullName}/forks";
    public string WatchersUrl => $"https://github.com/{Repo.FullName}/watchers";

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

    // Everything the dashboard actually renders for this card. LastUpdated is
    // intentionally excluded — it isn't shown anywhere, so a poll that only
    // refreshes the fetch timestamp must not count as a change.
    (int, int, int, int, int, string?, bool, bool, PillType, string?, string?, bool, string?) RenderSignature()
        => (OpenIssues, OpenPullRequests, Stars, Forks, Watchers,
            LatestRunStatus, LatestRunSucceeded, LatestRunFailed,
            LatestRunPillType, LatestRunDescription, LatestRunUrl, HasError, ErrorMessage);

    /// <summary>
    /// Folds a fresh snapshot into the card. Returns true only when a rendered
    /// field actually changed — the dashboard polls every repo each cycle even
    /// when nothing moved, and re-rendering on an identical snapshot ships a
    /// wasted render batch into the WebView every minute for the life of the app.
    /// </summary>
    public bool Apply(RepoSnapshot snap)
    {
        var before = RenderSignature();

        ErrorMessage = snap.ErrorMessage;
        HasError = snap.HasError;
        LastUpdated = snap.FetchedAt;
        if (snap.HasError)
            return RenderSignature() != before;

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
            return RenderSignature() != before;
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

        return RenderSignature() != before;
    }
}
