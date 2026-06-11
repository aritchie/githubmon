using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

public sealed record SnapshotUpdatedEvent(string AccountId, MonitoredRepo Repo, RepoSnapshot Snapshot) : IEvent;
