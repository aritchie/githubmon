
namespace GitHubMon.Accounts;

public interface IConfigStore
{
    IReadOnlyList<MonitoredAccount> Accounts { get; }
    event EventHandler? Changed;

    Task ReloadAsync(CancellationToken ct = default);
    Task UpsertAccountAsync(MonitoredAccount account, CancellationToken ct = default);
    Task RemoveAccountAsync(string accountId, CancellationToken ct = default);

    Task<TimeSpan> GetPollIntervalAsync();
    Task SetPollIntervalAsync(TimeSpan interval);

    Task<bool> GetNotificationsMutedAsync();
    Task SetNotificationsMutedAsync(bool muted);

    /// <summary>Dashboard card order as "accountId|owner/name" keys. Empty = natural (account/repo) order.</summary>
    Task<IReadOnlyList<string>> GetRepoOrderAsync();
    Task SetRepoOrderAsync(IReadOnlyList<string> orderedKeys);
}
