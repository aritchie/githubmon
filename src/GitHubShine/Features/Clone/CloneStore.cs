using Shiny;
using Shiny.DocumentDb;

namespace GitHubShine.Clone;

public interface ICloneStore
{
    /// <summary>The saved clone settings, or the defaults when nothing has been saved yet.</summary>
    Task<ClonePrefs> GetAsync(CancellationToken ct = default);

    /// <summary>Saves the clone settings.</summary>
    Task SaveAsync(ClonePrefs prefs, CancellationToken ct = default);

    /// <summary>Stamps when a clone pass last finished, leaving the rest of the settings alone.</summary>
    Task MarkRunAsync(DateTimeOffset whenUtc, CancellationToken ct = default);
}

/// <summary>
/// DocumentDb-backed accessor for the single <see cref="ClonePrefs"/> row, shaped like
/// <see cref="SyncStore"/>'s auto-sync half so the clone page binds the way the sync page does.
/// </summary>
[Singleton]
public sealed class CloneStore(IDocumentStore store) : ICloneStore
{
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
