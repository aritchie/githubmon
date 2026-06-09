using CommunityToolkit.Mvvm.ComponentModel;
using Shiny.Maui.Controls;

namespace GitHubMon.Dashboard;

public sealed partial class RepoCardViewModel(MonitoredAccount account, MonitoredRepo repo) : ObservableObject
{
    public MonitoredAccount Account { get; } = account;
    public MonitoredRepo Repo { get; } = repo;

    public string Key => $"{account.Id}|{repo.FullName}";
    public string AccountLabel => account.Label;
    public string Title => repo.FullName;

    [ObservableProperty] int openIssues;
    [ObservableProperty] int openPullRequests;
    [ObservableProperty] string? latestRunStatus;
    [ObservableProperty] PillType latestRunPillType = PillType.Info;
    [ObservableProperty] string? latestRunDescription;
    [ObservableProperty] DateTimeOffset? lastUpdated;
    [ObservableProperty] string? errorMessage;
    [ObservableProperty] bool hasError;

    public void Apply(RepoSnapshot snap)
    {
        ErrorMessage = snap.ErrorMessage;
        HasError = snap.HasError;
        LastUpdated = snap.FetchedAt;
        if (snap.HasError) return;

        OpenIssues = snap.OpenIssues;
        OpenPullRequests = snap.OpenPullRequests.Count;

        var latest = snap.RecentWorkflowRuns.FirstOrDefault();
        if (latest is null)
        {
            LatestRunStatus = "No runs";
            LatestRunPillType = PillType.Info;
            LatestRunDescription = "—";
            return;
        }

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
