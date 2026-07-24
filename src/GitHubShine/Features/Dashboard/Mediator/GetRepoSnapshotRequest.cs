using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

public sealed record GetRepoSnapshotRequest(MonitoredAccount Account, MonitoredRepo Repo)
    : IRequest<RepoSnapshot>, IContractKey
{
    // A stable, human-readable cache key. The default key provider would hash the whole request,
    // but MonitoredAccount carries a List<MonitoredRepo> whose equality/hash isn't stable across
    // reloads — that would scatter the persistent cache and defeat cold-start reuse.
    public string GetKey() => $"RepoSnapshot:{Account.Id}:{Repo.FullName}";
}
