using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

/// <summary>
/// A workflow's latest completed run went green after the last one recorded for it was red —
/// the counterpart to <see cref="WorkflowRunFailedEvent"/>. Raised once per red-to-green
/// transition, not once per successful run.
/// </summary>
public sealed record WorkflowRunRecoveredEvent(
    string AccountId,
    MonitoredRepo Repo,
    WorkflowRunSummary Run) : IEvent;
