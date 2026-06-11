using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

/// <summary>The open-issue count went up since the previous snapshot. GitHub only
/// gives us a count (not the issue list) per snapshot, so this is a delta event.</summary>
public sealed record NewIssuesEvent(string AccountId, MonitoredRepo Repo, int NewCount, int TotalOpen) : IEvent;
