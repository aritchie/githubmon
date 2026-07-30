using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

/// <summary>
/// The fork count went up between two live refreshes. Count-only for the same reason as
/// <see cref="NewStarsEvent"/>.
/// </summary>
public sealed record NewForksEvent(
    string AccountId,
    MonitoredRepo Repo,
    int NewCount,
    int Total) : IEvent;
