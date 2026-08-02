using Shiny.Mediator;

namespace GitHubShine.Persons.Mediator;

public sealed record PersonSnapshotUpdatedEvent(string PersonId, PersonSnapshot Snapshot) : IEvent;
