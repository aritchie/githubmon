using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

[MediatorSingleton]
public sealed class ListInboxHandler(IGitProviderFactory factory, ILogger<ListInboxHandler> logger)
    : IRequestHandler<ListInboxRequest, IReadOnlyList<InboxItem>>
{
    public async Task<IReadOnlyList<InboxItem>> Handle(ListInboxRequest request, IMediatorContext context, CancellationToken cancellationToken)
    {
        var provider = await factory.CreateAsync(request.Account, cancellationToken).ConfigureAwait(false);
        if (provider is null)
            return [];

        try
        {
            return await provider.GetInboxAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed inbox for {Account}", request.Account.Label);
            return [];
        }
    }
}
