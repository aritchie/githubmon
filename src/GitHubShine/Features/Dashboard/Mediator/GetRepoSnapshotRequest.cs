using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

public sealed record GetRepoSnapshotRequest(MonitoredAccount Account, MonitoredRepo Repo) : IRequest<RepoSnapshot>;
