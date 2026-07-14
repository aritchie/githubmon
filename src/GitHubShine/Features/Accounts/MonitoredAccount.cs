using GitHubShine.Providers;

namespace GitHubShine.Accounts;

public sealed record MonitoredAccount(
    string Id,
    string Label,
    string? Username,
    List<MonitoredRepo> Repos,
    // Trailing optional params so accounts persisted before multi-provider support
    // still deserialize from the DocDb (default to a github.com GitHub account).
    GitProviderType ProviderType = GitProviderType.GitHub,
    string? HostUrl = null)
{
    /// <summary>
    /// Web root for building user-facing repo links (issues, PRs, actions, …).
    /// Gitea always lives at its configured <see cref="HostUrl"/>; GitHub defaults
    /// to github.com but honours <see cref="HostUrl"/> for GitHub Enterprise. This
    /// is NOT the API base — links point at the human-facing site, not /api.
    /// </summary>
    public string WebBaseUrl => string.IsNullOrWhiteSpace(this.HostUrl)
        ? "https://github.com"
        : this.HostUrl.TrimEnd('/');
}
