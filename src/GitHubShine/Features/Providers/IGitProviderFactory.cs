namespace GitHubShine.Providers;

/// <summary>
/// Builds and caches one <see cref="IGitProvider"/> per account. Drop-in successor
/// to the old IGitHubClientFactory — same cache/invalidate contract, but returns a
/// backend-agnostic provider chosen from the account's <see cref="MonitoredAccount.ProviderType"/>.
/// </summary>
public interface IGitProviderFactory
{
    /// <summary>
    /// Resolves the cached provider for a saved account, building it from the
    /// account's provider type + host and the token in the vault. Returns null when
    /// no token is configured.
    /// </summary>
    Task<IGitProvider?> CreateAsync(MonitoredAccount account, CancellationToken ct = default);

    /// <summary>
    /// Builds a transient (uncached) provider for an account that hasn't been saved
    /// yet — used by the account-edit page to validate a token and list repos.
    /// </summary>
    IGitProvider Create(GitProviderType type, string? hostUrl, string token, string accountId);

    void Invalidate(string accountId);
    void InvalidateAll();
}
