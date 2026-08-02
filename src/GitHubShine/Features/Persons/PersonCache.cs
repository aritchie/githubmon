using System.Collections.Concurrent;
using Shiny;

namespace GitHubShine.Persons;

/// <summary>
/// Last-seen snapshot per followed login, so a page opened mid-session paints immediately
/// instead of waiting for the next poll. Purely in-memory — the cross-launch persistence is
/// Shiny.Mediator's <c>[Cache]</c> on the snapshot handler.
/// </summary>
/// <remarks>
/// Deliberately thinner than <see cref="SnapshotCache"/>: that one also keeps document-store
/// records to dedupe notifications across restarts, and the people grid raises none.
/// </remarks>
[Singleton(AsSelf = true)]
public sealed class PersonCache
{
    readonly ConcurrentDictionary<string, PersonSnapshot> snapshots = new();

    public PersonSnapshot? TryGet(FollowedPerson person)
        => this.snapshots.TryGetValue(person.Id, out var snap) ? snap : null;

    public void Put(string personId, PersonSnapshot snapshot)
        => this.snapshots[personId] = snapshot;

    /// <summary>Forgets an unfollowed login so re-following it doesn't paint stale counters.</summary>
    public void Remove(string personId)
        => this.snapshots.TryRemove(personId, out _);
}
