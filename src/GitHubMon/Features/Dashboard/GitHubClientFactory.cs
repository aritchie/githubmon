using System.Collections.Concurrent;
using Octokit;
using Shiny;

namespace GitHubMon.Dashboard;

[Singleton]
public sealed class GitHubClientFactory(ITokenVault vault) : IGitHubClientFactory
{
    static readonly ProductHeaderValue Product = new("GitHubMon", "1.0");
    readonly ConcurrentDictionary<string, IGitHubClient> cache = new();

    public async Task<IGitHubClient?> CreateAsync(string accountId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(accountId, out var existing))
            return existing;

        var token = await vault.GetTokenAsync(accountId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var client = new GitHubClient(Product)
        {
            Credentials = new Credentials(token)
        };
        cache[accountId] = client;
        return client;
    }

    public void Invalidate(string accountId) => cache.TryRemove(accountId, out _);

    public void InvalidateAll() => cache.Clear();
}
