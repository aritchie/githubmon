using Shiny.Mediator;

namespace GitHubShine.Persons.Mediator;

[MediatorSingleton]
public sealed class RefreshPersonsHandler(
    IConfigStore config,
    IPersonStore persons,
    PersonCache cache,
    ILogger<RefreshPersonsHandler> logger)
    : ICommandHandler<RefreshPersonsCommand>
{
    // Force the snapshot cache to refresh (skip the read, run the handler, write the fresh result).
    static readonly Action<IMediatorContext> ForceRefresh = ctx => ctx.ForceCacheRefresh();

    public async Task Handle(RefreshPersonsCommand command, IMediatorContext context, CancellationToken cancellationToken)
    {
        await persons.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (persons.People.Count == 0)
            return;

        var force = command.Force;
        var accounts = config.Accounts.ToDictionary(a => a.Id);

        foreach (var person in persons.People)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            // A row whose account is gone can't be read at all. PruneForRemovedAccountAsync clears
            // these on delete, so this only catches a row orphaned by an interrupted delete.
            if (!accounts.TryGetValue(person.AccountId, out var account))
                continue;

            try
            {
                var snap = await context
                    .Request(new GetPersonSnapshotRequest(account, person), cancellationToken, force ? ForceRefresh : null)
                    .ConfigureAwait(false);

                cache.Put(person.Id, snap);
                await context
                    .Publish(new PersonSnapshotUpdatedEvent(person.Id, snap), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (force)
                    await SyncStoredFieldsAsync(person, snap, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refresh failed for {Login}", person.Login);

                // First-load failure with nothing cached yet → surface the error on the row.
                // Otherwise leave the last good numbers up rather than flashing an error.
                if (cache.TryGet(person) is null)
                {
                    var errSnap = PersonSnapshot.Empty(person, ex.Message);
                    cache.Put(person.Id, errSnap);
                    await context
                        .Publish(new PersonSnapshotUpdatedEvent(person.Id, errSnap), cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Writes back the two stored fields the host owns — the kind (a login can change from a
    /// person to an organisation) and the display name. Only on a real difference: an unconditional
    /// upsert would raise <see cref="IPersonStore.Changed"/> every poll and rebuild the grid
    /// underneath the user for nothing.
    /// </summary>
    async Task SyncStoredFieldsAsync(FollowedPerson person, PersonSnapshot snap, CancellationToken ct)
    {
        if (snap.HasError)
            return;
        if (person.Kind == snap.Kind && person.DisplayName == snap.DisplayName)
            return;

        await persons
            .UpsertAsync(person with { Kind = snap.Kind, DisplayName = snap.DisplayName }, ct)
            .ConfigureAwait(false);
    }
}
