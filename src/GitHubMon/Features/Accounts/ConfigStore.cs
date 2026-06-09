using Shiny.DocumentDb;
using Shiny.Extensions.Stores;

namespace GitHubMon.Accounts;

public sealed class ConfigStore(
    IDocumentStore store,
    [FromKeyedServices(StoreKeys.Default)] IKeyValueStore prefs) : IConfigStore
{
    const string KeyPollSeconds = "pollSeconds";
    const string KeyMuted = "muted";
    const int DefaultPollSeconds = 60;

    IReadOnlyList<MonitoredAccount> accounts = Array.Empty<MonitoredAccount>();

    public IReadOnlyList<MonitoredAccount> Accounts => accounts;
    public event EventHandler? Changed;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        accounts = await store
            .Query(AccountsJsonContext.Default.MonitoredAccount)
            .ToList(ct)
            .ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpsertAccountAsync(MonitoredAccount account, CancellationToken ct = default)
    {
        await store
            .Upsert(account, AccountsJsonContext.Default.MonitoredAccount, ct)
            .ConfigureAwait(false);
        await ReloadAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAccountAsync(string accountId, CancellationToken ct = default)
    {
        await store.Remove<MonitoredAccount>(accountId, ct).ConfigureAwait(false);
        await ReloadAsync(ct).ConfigureAwait(false);
    }

    public Task<TimeSpan> GetPollIntervalAsync()
        => Task.FromResult(TimeSpan.FromSeconds(prefs.Get(KeyPollSeconds, DefaultPollSeconds)));

    public Task SetPollIntervalAsync(TimeSpan interval)
    {
        prefs.Set(KeyPollSeconds, (int)Math.Max(15, interval.TotalSeconds));
        return Task.CompletedTask;
    }

    public Task<bool> GetNotificationsMutedAsync()
        => Task.FromResult(prefs.Get(KeyMuted, false));

    public Task SetNotificationsMutedAsync(bool muted)
    {
        prefs.Set(KeyMuted, muted);
        return Task.CompletedTask;
    }
}
