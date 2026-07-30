namespace GitHubShine.Dashboard;

public sealed record SeenFailedRun(string Id, DateTimeOffset SeenAt);

public sealed record SeenInboxItem(string Id, DateTimeOffset SeenAt);

/// <summary>
/// The red/green state last recorded for a single workflow (id is "accountId|owner/name|wf:name").
/// It's what makes a "back to green" alert possible without re-announcing every subsequent
/// success — and because a first sighting only writes the baseline, a fresh install never
/// reports a recovery it never saw the failure for.
/// </summary>
public sealed record SeenWorkflowState(string Id, bool Failing, DateTimeOffset SeenAt);
