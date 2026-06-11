namespace GitHubShine.Accounts;

public interface ITokenVault
{
    Task<string?> GetTokenAsync(string accountId, CancellationToken ct = default);
    Task SetTokenAsync(string accountId, string token, CancellationToken ct = default);
    Task DeleteTokenAsync(string accountId, CancellationToken ct = default);
}
