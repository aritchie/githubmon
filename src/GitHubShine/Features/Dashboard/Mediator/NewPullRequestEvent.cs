using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

public sealed record NewPullRequestEvent(string AccountId, MonitoredRepo Repo, PullRequestSummary PullRequest) : IEvent;
