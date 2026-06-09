using Shiny.Mediator;

namespace GitHubMon.Dashboard.Mediator;

public sealed record SnapshotUpdatedEvent(string AccountId, MonitoredRepo Repo, RepoSnapshot Snapshot) : IEvent;
