using Shiny.Mediator;

namespace GitHubMon.Dashboard.Mediator;

public sealed record NewPullRequestEvent(string AccountId, MonitoredRepo Repo, PullRequestSummary PullRequest) : IEvent;
