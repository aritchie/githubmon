using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

public sealed record WorkflowRunFailedEvent(string AccountId, MonitoredRepo Repo, WorkflowRunSummary Run) : IEvent;
