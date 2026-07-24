using Shiny;
using Shiny.DocumentDb;
using Shiny.Extensions.Stores;

namespace GitHubShine.Accounts;

[Singleton]
public sealed class ConfigStore(
    IDocumentStore store,
    [FromKeyedServices(StoreKeys.Default)] IKeyValueStore prefs) : IConfigStore
{
    const string KeyPollSeconds = "pollSeconds";
    const string KeyMuted = "muted";
    const string KeyRepoOrder = "repoOrder";
    const string KeyRepoSort = "repoSort";
    // A conservative baseline: with ETag conditional requests making unchanged polls essentially
    // free, and adaptive back-off when the rate-limit budget runs low, this keeps steady-state
    // API pressure well under control while staying responsive. Users can still lower it (floor 15s).
    const int DefaultPollSeconds = 120;

    IReadOnlyList<MonitoredAccount> accounts = Array.Empty<MonitoredAccount>();

    public IReadOnlyList<MonitoredAccount> Accounts => accounts;
    public event EventHandler? Changed;
    public event EventHandler? PreferencesChanged;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        accounts = await store
            .Query<MonitoredAccount>()
            .ToList(ct)
            .ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpsertAccountAsync(MonitoredAccount account, CancellationToken ct = default)
    {
        await store
            .Upsert(account, cancellationToken: ct)
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
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<bool> GetNotificationsMutedAsync()
        => Task.FromResult(prefs.Get(KeyMuted, false));

    public Task SetNotificationsMutedAsync(bool muted)
    {
        prefs.Set(KeyMuted, muted);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<RepoSort> GetRepoSortAsync()
        => Task.FromResult((RepoSort)prefs.Get(KeyRepoSort, (int)RepoSort.Stars));

    public Task SetRepoSortAsync(RepoSort sort)
    {
        prefs.Set(KeyRepoSort, (int)sort);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    // Card order lives in the document store (not the key/value prefs) so a raw
    // SQLite backup carries it along with accounts and tokens.
    public async Task<IReadOnlyList<string>> GetRepoOrderAsync()
    {
        var doc = await store.Get<DashboardPrefs>(DashboardPrefs.DefaultId).ConfigureAwait(false);
        if (doc is not null)
            return doc.RepoOrder;

        // One-time migration from the legacy prefs-store encoding.
        var raw = prefs.Get(KeyRepoOrder, "");
        if (string.IsNullOrEmpty(raw))
            return Array.Empty<string>();
        var order = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        await store.Upsert(new DashboardPrefs(DashboardPrefs.DefaultId, order)).ConfigureAwait(false);
        prefs.Remove(KeyRepoOrder);
        return order;
    }

    public Task SetRepoOrderAsync(IReadOnlyList<string> orderedKeys)
        => store.Upsert(new DashboardPrefs(DashboardPrefs.DefaultId, orderedKeys.ToList()));
}
