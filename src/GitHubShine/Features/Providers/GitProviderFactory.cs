using System.Collections.Concurrent;
using System.Net.Http;
using Shiny;

namespace GitHubShine.Providers;

[Singleton]
public sealed class GitProviderFactory(ITokenVault vault, RateLimitMonitor rateLimits, ILoggerFactory loggerFactory) : IGitProviderFactory
{
    readonly ILogger etagLogger = loggerFactory.CreateLogger("GitHubShine.Providers.ConditionalGet");

    readonly ConcurrentDictionary<string, IGitProvider> cache = new();

    public async Task<IGitProvider?> CreateAsync(MonitoredAccount account, CancellationToken ct = default)
    {
        if (cache.TryGetValue(account.Id, out var existing))
            return existing;

        var token = await vault.GetTokenAsync(account.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var provider = Create(account.ProviderType, account.HostUrl, token, account.Id);
        cache[account.Id] = provider;
        return provider;
    }

    public IGitProvider Create(GitProviderType type, string? hostUrl, string token, string accountId)
        => type switch
        {
            // A dedicated long-lived HttpClient per Gitea provider — providers are cached
            // per-account, so this is one client per account, not per-request. Its pipeline runs
            // through ConditionalGetHandler for ETag/304 savings and rate-limit tracking.
            GitProviderType.Gitea => new GiteaProvider(
                new HttpClient(new ConditionalGetHandler(new HttpClientHandler(), rateLimits, etagLogger)),
                hostUrl, token, accountId),
            _ => new GitHubProvider(hostUrl, token, accountId, rateLimits, etagLogger)
        };

    public void Invalidate(string accountId) => cache.TryRemove(accountId, out _);

    public void InvalidateAll() => cache.Clear();
}
