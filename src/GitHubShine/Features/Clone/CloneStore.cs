using Shiny;
using Shiny.DocumentDb;

namespace GitHubShine.Clone;

public interface ICloneStore
{
    /// <summary>Every saved working copy, as of the last <see cref="ReloadAsync"/>.</summary>
    IReadOnlyList<CloneTarget> Targets { get; }

    /// <summary>Raised when entries are added, edited or removed — consumers re-read <see cref="Targets"/>.</summary>
    event EventHandler? Changed;

    Task ReloadAsync(CancellationToken ct = default);
    Task UpsertAsync(CloneTarget target, CancellationToken ct = default);

    /// <summary>Saves a batch as one edit: one reload and one <see cref="Changed"/> for the lot.</summary>
    Task UpsertManyAsync(IReadOnlyCollection<CloneTarget> targets, CancellationToken ct = default);

    Task RemoveAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Drops every entry read through <paramref name="accountId"/> — without the account there's no
    /// token to clone or fetch with. The folders on disk are left exactly where they are.
    /// </summary>
    Task<IReadOnlyList<CloneTarget>> PruneForRemovedAccountAsync(string accountId, CancellationToken ct = default);

    /// <summary>The saved clone settings, or the defaults when nothing has been saved yet.</summary>
    Task<ClonePrefs> GetAsync(CancellationToken ct = default);

    /// <summary>Saves the clone settings.</summary>
    Task SaveAsync(ClonePrefs prefs, CancellationToken ct = default);

    /// <summary>Stamps when a clone pass last finished, leaving the rest of the settings alone.</summary>
    Task MarkRunAsync(DateTimeOffset whenUtc, CancellationToken ct = default);
}

/// <summary>
/// DocumentDb-backed CRUD for <see cref="CloneTarget"/> plus the single <see cref="ClonePrefs"/>
/// row, shaped like <see cref="SyncStore"/> so the clone page binds the way the sync page does.
/// </summary>
[Singleton]
public sealed class CloneStore(IDocumentStore store) : ICloneStore
{
    public IReadOnlyList<CloneTarget> Targets { get; private set; } = [];
    public event EventHandler? Changed;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        this.Targets = await store
            .Query<CloneTarget>()
            .ToList(ct)
            .ConfigureAwait(false);
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpsertAsync(CloneTarget target, CancellationToken ct = default)
    {
        // patchIfUpdate: false — a branch cleared back to null (the repo's default) must actually
        // clear, not be merged over by the name stored before.
        await store.Upsert(target, patchIfUpdate: false, cancellationToken: ct).ConfigureAwait(false);
        await this.ReloadAsync(ct).ConfigureAwait(false);
    }

    public async Task UpsertManyAsync(IReadOnlyCollection<CloneTarget> targets, CancellationToken ct = default)
    {
        if (targets.Count == 0)
            return;

        foreach (var target in targets)
            await store.Upsert(target, patchIfUpdate: false, cancellationToken: ct).ConfigureAwait(false);

        await this.ReloadAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await store.Remove<CloneTarget>(id, ct).ConfigureAwait(false);
        await this.ReloadAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloneTarget>> PruneForRemovedAccountAsync(string accountId, CancellationToken ct = default)
    {
        // Work from what's actually persisted — the caller may have been holding a stale cache.
        await this.ReloadAsync(ct).ConfigureAwait(false);

        var doomed = this.Targets.Where(t => t.AccountId == accountId).ToList();
        if (doomed.Count == 0)
            return [];

        foreach (var target in doomed)
            await store.Remove<CloneTarget>(target.Id, ct).ConfigureAwait(false);

        await this.ReloadAsync(ct).ConfigureAwait(false);
        return doomed;
    }

    public async Task<ClonePrefs> GetAsync(CancellationToken ct = default)
        => await store.Get<ClonePrefs>(ClonePrefs.DefaultId, cancellationToken: ct).ConfigureAwait(false)
            ?? ClonePrefs.Default;

    public Task SaveAsync(ClonePrefs prefs, CancellationToken ct = default)
        // patchIfUpdate: false for the same reason ConfigStore uses it — callers read the whole row,
        // change a field and write it back, so the stored body should be replaced, not merged into.
        => store.Upsert(prefs with { Id = ClonePrefs.DefaultId }, patchIfUpdate: false, cancellationToken: ct);

    public async Task MarkRunAsync(DateTimeOffset whenUtc, CancellationToken ct = default)
    {
        // Re-read first: the user may have changed the folder during the pass that just finished.
        var prefs = await this.GetAsync(ct).ConfigureAwait(false);
        await store
            .Upsert(prefs with { LastRunUtc = whenUtc }, patchIfUpdate: false, cancellationToken: ct)
            .ConfigureAwait(false);
    }
}
