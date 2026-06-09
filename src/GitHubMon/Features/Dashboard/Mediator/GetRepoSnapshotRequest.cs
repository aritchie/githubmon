using Shiny.Mediator;

namespace GitHubMon.Dashboard.Mediator;

public sealed record GetRepoSnapshotRequest(MonitoredAccount Account, MonitoredRepo Repo) : IRequest<RepoSnapshot>;
