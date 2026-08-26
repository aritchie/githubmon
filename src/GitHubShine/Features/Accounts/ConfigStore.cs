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
    const string KeyRepoOrder = "repoOrder";
    // A conservative baseline: with ETag conditional requests making unchanged polls essentially
    // free, and adaptive back-off when the rate-limit budget runs low, this keeps steady-state
    // API pressure well under control while staying responsive. Users can still lower it (floor 15s).
    const int DefaultPollSeconds = 120;

    public IReadOnlyList<MonitoredAccount> Accounts { get; private set; } = [];
    public event EventHandler? Changed;
    public event EventHandler? PreferencesChanged;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        this.Accounts = await store
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

    public async Task<RepoGridSort> GetRepoGridSortAsync()
    {
        var doc = await LoadDashboardPrefsAsync().ConfigureAwait(false);
        // IsKnown guards a stored id whose column has since been renamed or dropped.
        return doc.SortColumnId is { Length: > 0 } id && RepoGridColumns.IsKnown(id)
            ? new RepoGridSort(id, doc.SortDescending ?? true)
            : RepoGridSort.Default;
    }

    public async Task SetRepoGridSortAsync(RepoGridSort sort)
    {
        var current = await LoadDashboardPrefsAsync().ConfigureAwait(false);
        await SaveDashboardPrefsAsync(current with
        {
            SortColumnId = sort.ColumnId,
            SortDescending = sort.Descending
        }).ConfigureAwait(false);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<RepoViewMode> GetRepoViewAsync()
    {
        var doc = await LoadDashboardPrefsAsync().ConfigureAwait(false);
        // Stored by name, so an unknown value (an older build, a hand-edited row) reads as the
        // default rather than throwing or landing on whatever enum member happens to be 0.
        return Enum.TryParse<RepoViewMode>(doc.RepoView, ignoreCase: true, out var view)
            ? view
            : RepoViewMode.Cards;
    }

    public async Task SetRepoViewAsync(RepoViewMode view)
    {
        var current = await LoadDashboardPrefsAsync().ConfigureAwait(false);
        await SaveDashboardPrefsAsync(current with { RepoView = view.ToString() }).ConfigureAwait(false);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<PersonGridSort> GetPersonGridSortAsync()
    {
        var doc = await LoadDashboardPrefsAsync().ConfigureAwait(false);
        // IsKnown guards a stored id whose column has since been renamed or dropped.
        return doc.PersonSortColumnId is { Length: > 0 } id && PersonGridColumns.IsKnown(id)
            ? new PersonGridSort(id, doc.PersonSortDescending ?? true)
            : PersonGridSort.Default;
    }

    public async Task SetPersonGridSortAsync(PersonGridSort sort)
    {
        var current = await LoadDashboardPrefsAsync().ConfigureAwait(false);
        await SaveDashboardPrefsAsync(current with
        {
            PersonSortColumnId = sort.ColumnId,
            PersonSortDescending = sort.Descending
        }).ConfigureAwait(false);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<string>> GetRepoOrderAsync()
        => (await LoadDashboardPrefsAsync().ConfigureAwait(false)).RepoOrder;

    public async Task SetRepoOrderAsync(IReadOnlyList<string> orderedKeys)
    {
        var current = await LoadDashboardPrefsAsync().ConfigureAwait(false);
        await SaveDashboardPrefsAsync(current with { RepoOrder = orderedKeys.ToList() })
            .ConfigureAwait(false);
    }

    // patchIfUpdate: false — both setters above read the whole row, change one field and write
    // it all back, so the stored body should be replaced, not RFC 7396 deep-merged into. The
    // merge default leaves behind any member that has since been dropped from DashboardPrefs,
    // which then reads back as a phantom (STJ ignores it, but it lingers in backups).
    Task SaveDashboardPrefsAsync(DashboardPrefs updated)
        => store.Upsert(updated, patchIfUpdate: false);

    // Card order and grid sort share one document-store row (not the key/value prefs) so a
    // raw SQLite backup carries them along with accounts and tokens — and because the
    // key/value store is in-memory on the desktop heads, where they'd otherwise be lost on
    // every restart. Both setters read-modify-write through here so neither clobbers the other.
    async Task<DashboardPrefs> LoadDashboardPrefsAsync()
    {
        var doc = await store.Get<DashboardPrefs>(DashboardPrefs.DefaultId).ConfigureAwait(false);
        if (doc is not null)
            return doc;

        // One-time migration of the card order from the legacy prefs-store encoding.
        var raw = prefs.Get(KeyRepoOrder, "");
        if (string.IsNullOrEmpty(raw))
            return new DashboardPrefs(DashboardPrefs.DefaultId, []);

        var migrated = new DashboardPrefs(
            DashboardPrefs.DefaultId,
            raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList()
        );
        await SaveDashboardPrefsAsync(migrated).ConfigureAwait(false);
        prefs.Remove(KeyRepoOrder);
        return migrated;
    }
}
