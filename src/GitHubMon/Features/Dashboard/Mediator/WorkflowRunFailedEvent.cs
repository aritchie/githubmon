using Shiny.Mediator;

namespace GitHubMon.Dashboard.Mediator;

public sealed record WorkflowRunFailedEvent(string AccountId, MonitoredRepo Repo, WorkflowRunSummary Run) : IEvent;
