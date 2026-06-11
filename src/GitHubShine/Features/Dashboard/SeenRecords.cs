namespace GitHubShine.Dashboard;

public sealed record SeenFailedRun(string Id, DateTimeOffset SeenAt);

public sealed record SeenInboxItem(string Id, DateTimeOffset SeenAt);
