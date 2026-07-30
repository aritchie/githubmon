using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

/// <summary>The open-issue count went up since the previous snapshot. The repo summary
/// every provider returns carries a count, not the issue list, so this is a delta event.</summary>
public sealed record NewIssuesEvent(string AccountId, MonitoredRepo Repo, int NewCount, int TotalOpen) : IEvent;
