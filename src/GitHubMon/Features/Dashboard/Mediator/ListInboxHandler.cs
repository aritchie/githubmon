using Octokit;
using Shiny.Mediator;

namespace GitHubMon.Dashboard.Mediator;

[MediatorSingleton]
public sealed class ListInboxHandler(IGitHubClientFactory factory, ILogger<ListInboxHandler> logger)
    : IRequestHandler<ListInboxRequest, IReadOnlyList<InboxItem>>
{
    public async Task<IReadOnlyList<InboxItem>> Handle(ListInboxRequest request, IMediatorContext context, CancellationToken cancellationToken)
    {
        var client = await factory.CreateAsync(request.Account.Id, cancellationToken).ConfigureAwait(false);
        if (client is null)
            return Array.Empty<InboxItem>();

        try
        {
            var notifications = await client.Activity.Notifications.GetAllForCurrent(new NotificationsRequest
            {
                All = false,
                Participating = true
            }).ConfigureAwait(false);

            return notifications
                .Select(n => new InboxItem(
                    request.Account.Id,
                    n.Id,
                    n.Repository?.FullName ?? "?",
                    n.Subject?.Title ?? "(no title)",
                    MapReason(n.Reason),
                    n.Subject?.Type ?? "",
                    ParseDate(n.UpdatedAt),
                    n.Subject?.Url,
                    n.Unread))
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed inbox for {Account}", request.Account.Label);
            return Array.Empty<InboxItem>();
        }
    }

    static DateTimeOffset ParseDate(string? s)
        => DateTimeOffset.TryParse(s, out var d) ? d : DateTimeOffset.UtcNow;

    static InboxReason MapReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        "mention" => InboxReason.Mention,
        "assign" => InboxReason.Assigned,
        "review_requested" => InboxReason.ReviewRequested,
        "author" => InboxReason.Author,
        "comment" => InboxReason.Comment,
        "subscribed" => InboxReason.Subscribed,
        _ => InboxReason.Other
    };
}
