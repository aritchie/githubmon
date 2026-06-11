namespace GitHubShine.Accounts;

public sealed record MonitoredAccount(
    string Id,
    string Label,
    string? Username,
    List<MonitoredRepo> Repos);
