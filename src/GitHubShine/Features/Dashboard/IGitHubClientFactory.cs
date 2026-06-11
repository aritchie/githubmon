using Octokit;

namespace GitHubShine.Dashboard;

public interface IGitHubClientFactory
{
    Task<IGitHubClient?> CreateAsync(string accountId, CancellationToken ct = default);
    void Invalidate(string accountId);
    void InvalidateAll();
}
