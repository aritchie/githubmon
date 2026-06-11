using Shiny.DocumentDb;

namespace GitHubShine.Accounts;

// Tokens are stored alongside accounts in the SQLite DocDb. The original design
// kept secrets in SecureStorage, but MAUI Essentials SecureStorage doesn't work
// on the maui-labs MacOS/Linux backends (the static AND the DI-resolved
// implementations both throw "Not supported in portable version"). And the
// Shiny.Extensions.Stores Secure store ultimately routes through the same
// essentials on those backends. So tokens live in DocDb until secure storage
// has a working implementation on these platforms.
public sealed record StoredToken(string Id, string Token);

[Shiny.Singleton]
public sealed class SecureTokenVault(IDocumentStore store) : ITokenVault
{
    public async Task<string?> GetTokenAsync(string accountId, CancellationToken ct = default)
    {
        var tokens = await store
            .Query<StoredToken>()
            .ToList(ct)
            .ConfigureAwait(false);
        return tokens.FirstOrDefault(t => t.Id == accountId)?.Token;
    }

    public Task SetTokenAsync(string accountId, string token, CancellationToken ct = default)
        => store.Upsert(new StoredToken(accountId, token), cancellationToken: ct);

    public Task DeleteTokenAsync(string accountId, CancellationToken ct = default)
        => store.Remove<StoredToken>(accountId, ct);
}
