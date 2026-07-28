using Shiny;
using Shiny.DocumentDb;

namespace GitHubShine.Sync;

public interface ISyncStore
{
    IReadOnlyList<SyncMapping> Mappings { get; }

    /// <summary>Raised when mappings are added, edited or removed — consumers re-read <see cref="Mappings"/>.</summary>
    event EventHandler? Changed;

    Task ReloadAsync(CancellationToken ct = default);
    Task UpsertAsync(SyncMapping mapping, CancellationToken ct = default);
    Task RemoveAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Drops mappings that read from <paramref name="accountId"/> for a repo that isn't in
    /// <paramref name="monitoredRepos"/> any more. Call it right after saving an account's repo
    /// list: unmonitoring a repo takes its syncs with it, rather than leaving a mapping that can
    /// only fail at run time. Returns what was removed so the caller can say so.
    /// </summary>
    Task<IReadOnlyList<SyncMapping>> PruneForAccountAsync(
        string accountId,
        IReadOnlyCollection<MonitoredRepo> monitoredRepos,
        CancellationToken ct = default);

    /// <summary>
    /// Drops every mapping using <paramref name="accountId"/> at either end — a deleted account
    /// leaves no usable sync behind. Returns what was removed.
    /// </summary>
    Task<IReadOnlyList<SyncMapping>> PruneForRemovedAccountAsync(string accountId, CancellationToken ct = default);
}

/// <summary>
/// DocumentDb-backed CRUD for <see cref="SyncMapping"/>, mirroring <see cref="ConfigStore"/>'s
/// cache-and-raise-Changed shape so the sync page can bind the same way the accounts page does.
/// </summary>
[Singleton]
public sealed class SyncStore(IDocumentStore store) : ISyncStore
{
    IReadOnlyList<SyncMapping> mappings = Array.Empty<SyncMapping>();

    public IReadOnlyList<SyncMapping> Mappings => this.mappings;
    public event EventHandler? Changed;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        this.mappings = await store
            .Query<SyncMapping>()
            .ToList(ct)
            .ConfigureAwait(false);
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpsertAsync(SyncMapping mapping, CancellationToken ct = default)
    {
        await store.Upsert(mapping, cancellationToken: ct).ConfigureAwait(false);
        await this.ReloadAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await store.Remove<SyncMapping>(id, ct).ConfigureAwait(false);
        await this.ReloadAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncMapping>> PruneForAccountAsync(
        string accountId,
        IReadOnlyCollection<MonitoredRepo> monitoredRepos,
        CancellationToken ct = default)
    {
        // Reconciles against the list just saved rather than sweeping every orphan in the
        // database: only repos removed by this edit lose their syncs, so an account whose repo
        // list failed to load can't quietly wipe unrelated mappings.
        var keep = new HashSet<string>(monitoredRepos.Select(r => r.FullName), StringComparer.OrdinalIgnoreCase);

        return await this
            .RemoveWhereAsync(m => m.SourceAccountId == accountId && !keep.Contains(m.Source.FullName), ct)
            .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SyncMapping>> PruneForRemovedAccountAsync(string accountId, CancellationToken ct = default)
        => this.RemoveWhereAsync(m => m.SourceAccountId == accountId || m.TargetAccountId == accountId, ct);

    async Task<IReadOnlyList<SyncMapping>> RemoveWhereAsync(Func<SyncMapping, bool> predicate, CancellationToken ct)
    {
        // Work from what's actually persisted — the caller may have been holding a stale cache.
        await this.ReloadAsync(ct).ConfigureAwait(false);

        var doomed = this.mappings.Where(predicate).ToList();
        if (doomed.Count == 0)
            return Array.Empty<SyncMapping>();

        foreach (var mapping in doomed)
            await store.Remove<SyncMapping>(mapping.Id, ct).ConfigureAwait(false);

        // One reload for the batch; RemoveAsync's per-item reload would raise Changed N times.
        await this.ReloadAsync(ct).ConfigureAwait(false);
        return doomed;
    }
}
