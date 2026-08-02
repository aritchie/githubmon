using Shiny.Mediator;

namespace GitHubShine.Persons.Mediator;

public sealed record GetPersonSnapshotRequest(MonitoredAccount Account, FollowedPerson Person)
    : IRequest<PersonSnapshot>, IContractKey
{
    // A stable, human-readable cache key, for the same reason GetRepoSnapshotRequest has one:
    // the default provider hashes the whole request, and MonitoredAccount carries a
    // List<MonitoredRepo> whose equality isn't stable across reloads — that would scatter the
    // persistent cache and defeat cold-start reuse.
    public string GetKey() => $"PersonSnapshot:{Person.Id}";
}
