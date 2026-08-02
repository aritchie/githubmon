using Shiny.Mediator;

namespace GitHubShine.Persons.Mediator;

// partial is required by Shiny.Mediator's source generator whenever a middleware attribute
// ([Cache]) is present on the handler method.
[MediatorSingleton]
public sealed partial class GetPersonSnapshotHandler(
    IGitProviderFactory factory,
    ILogger<GetPersonSnapshotHandler> logger)
    : IRequestHandler<GetPersonSnapshotRequest, PersonSnapshot>
{
    // Persist snapshots (AddMauiPersistentCache) so a cold start paints last-known counters
    // instantly instead of re-fetching everything — the same deal the repo snapshots get, and it
    // matters more here because a person snapshot costs a paged repo listing. Types are
    // registered for serialization in GitHubShineCacheJsonContext.
    [Cache(AbsoluteExpirationSeconds = 60 * 60 * 24)]
    public async Task<PersonSnapshot> Handle(GetPersonSnapshotRequest request, IMediatorContext context, CancellationToken cancellationToken)
    {
        // Only logged on a cache miss or forced refresh — a persistent-cache hit is served by the
        // middleware without invoking the handler at all.
        logger.LogDebug("[Person] live fetch {Login}", request.Person.Login);

        var provider = await factory.CreateAsync(request.Account, cancellationToken).ConfigureAwait(false);
        if (provider is null)
            return PersonSnapshot.Empty(request.Person, "No token configured");

        // Deliberately no try/catch, as with GetRepoSnapshotHandler: the cache middleware never
        // persists a thrown result, so a transient outage can't poison the 24h cache.
        // RefreshPersonsHandler catches, logs, and decides what the row should show.
        var data = await provider
            .GetPersonSnapshotAsync(request.Person.Login, request.Person.Kind, cancellationToken)
            .ConfigureAwait(false);

        // A null here is the host saying the login is gone — renamed, deleted, or no longer
        // visible to this token. That's a durable answer, so it's cached like any other.
        if (data is null)
            return PersonSnapshot.Empty(request.Person, "Not found on this host");

        var p = data.Profile;
        return new PersonSnapshot(
            request.Account.Id,
            p.Login,
            p.Kind,
            DateTimeOffset.UtcNow,
            p.Name,
            p.AvatarUrl,
            p.Bio,
            p.Location,
            p.Company,
            p.WebsiteUrl,
            p.Followers,
            p.Following,
            p.PublicRepos,
            p.PublicGists,
            data.TotalStars,
            data.MemberCount,
            p.JoinedAt,
            null,
            data.StarsTruncated,
            data.MembersTruncated);
    }
}
