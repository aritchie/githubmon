namespace GitHubShine.Accounts;

public sealed record MonitoredRepo(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";
}
