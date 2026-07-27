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
}
