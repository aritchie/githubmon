using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

/// <summary>
/// The star count went up between two live refreshes. A count, not a list of who: the repo
/// summary the poll already fetches carries the total, and listing stargazers would cost an
/// extra request per repo per cycle.
/// </summary>
public sealed record NewStarsEvent(
    string AccountId,
    MonitoredRepo Repo,
    int NewCount,
    int Total) : IEvent;
