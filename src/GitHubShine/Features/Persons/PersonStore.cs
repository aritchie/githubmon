using Shiny;
using Shiny.DocumentDb;

namespace GitHubShine.Persons;

public interface IPersonStore
{
    IReadOnlyList<FollowedPerson> People { get; }

    /// <summary>Raised when the followed set changes — consumers re-read <see cref="People"/>.</summary>
    event EventHandler? Changed;

    Task ReloadAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads the followed set once per process. The poll handler runs long before any page has
    /// been opened, so it needs the rows loaded — but calling <see cref="ReloadAsync"/> every
    /// cycle would raise <see cref="Changed"/> on a poll that changed nothing and rebuild the
    /// grid underneath the user.
    /// </summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    Task UpsertAsync(FollowedPerson person, CancellationToken ct = default);
    Task RemoveAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Drops everyone followed through <paramref name="accountId"/>. Call it when the account is
    /// deleted: without its token there is nothing left to read those logins with. Returns what
    /// was removed so the caller can say so.
    /// </summary>
    Task<IReadOnlyList<FollowedPerson>> PruneForRemovedAccountAsync(string accountId, CancellationToken ct = default);
}

/// <summary>
/// DocumentDb-backed CRUD for <see cref="FollowedPerson"/>, mirroring <see cref="SyncStore"/>'s
/// cache-and-raise-Changed shape so the people page can bind the way the sync page does.
/// </summary>
[Singleton]
public sealed class PersonStore(IDocumentStore store) : IPersonStore
{
    bool loaded;

    public IReadOnlyList<FollowedPerson> People { get; private set; } = [];
    public event EventHandler? Changed;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        this.People = await store
            .Query<FollowedPerson>()
            .ToList(ct)
            .ConfigureAwait(false);
        this.loaded = true;
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task EnsureLoadedAsync(CancellationToken ct = default)
        => this.loaded ? Task.CompletedTask : this.ReloadAsync(ct);

    public async Task UpsertAsync(FollowedPerson person, CancellationToken ct = default)
    {
        await store.Upsert(person, cancellationToken: ct).ConfigureAwait(false);
        await this.ReloadAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await store.Remove<FollowedPerson>(id, ct).ConfigureAwait(false);
        await this.ReloadAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FollowedPerson>> PruneForRemovedAccountAsync(string accountId, CancellationToken ct = default)
    {
        // Work from what's actually persisted — the caller may have been holding a stale cache.
        await this.ReloadAsync(ct).ConfigureAwait(false);

        var doomed = this.People.Where(p => p.AccountId == accountId).ToList();
        if (doomed.Count == 0)
            return [];

        foreach (var person in doomed)
            await store.Remove<FollowedPerson>(person.Id, ct).ConfigureAwait(false);

        // One reload for the batch; RemoveAsync's per-item reload would raise Changed N times.
        await this.ReloadAsync(ct).ConfigureAwait(false);
        return doomed;
    }
}
