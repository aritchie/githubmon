using Shiny.Mediator;
using Shiny.Notifications;

namespace GitHubShine.Dashboard.Mediator;

[MediatorSingleton]
public sealed class NotificationDispatchHandler(INotificationManager notifications, IConfigStore config, ILogger<NotificationDispatchHandler> logger)
    : IEventHandler<WorkflowRunFailedEvent>,
      IEventHandler<NewInboxItemEvent>,
      IEventHandler<NewPullRequestEvent>,
      IEventHandler<NewIssuesEvent>
{
    static int idSeed = 1000;

    public async Task Handle(WorkflowRunFailedEvent @event, IMediatorContext context, CancellationToken cancellationToken)
    {
        if (await config.GetNotificationsMutedAsync().ConfigureAwait(false))
            return;

        await Send(new Notification
        {
            Id = Interlocked.Increment(ref idSeed),
            Title = $"Workflow failed — {@event.Repo.FullName}",
            Message = $"{@event.Run.WorkflowName} on {@event.Run.Branch}"
        }).ConfigureAwait(false);
    }

    public async Task Handle(NewInboxItemEvent @event, IMediatorContext context, CancellationToken cancellationToken)
    {
        if (await config.GetNotificationsMutedAsync().ConfigureAwait(false))
            return;

        if (@event.Item.Reason is not (InboxReason.Mention or InboxReason.Assigned or InboxReason.ReviewRequested))
            return;

        var label = @event.Item.Reason switch
        {
            InboxReason.Mention => "mentioned",
            InboxReason.Assigned => "assigned",
            InboxReason.ReviewRequested => "review requested",
            _ => "notification"
        };

        await Send(new Notification
        {
            Id = Interlocked.Increment(ref idSeed),
            Title = $"You were {label} — {@event.Item.RepoFullName}",
            Message = @event.Item.Title
        }).ConfigureAwait(false);
    }

    public async Task Handle(NewPullRequestEvent @event, IMediatorContext context, CancellationToken cancellationToken)
    {
        if (await config.GetNotificationsMutedAsync().ConfigureAwait(false))
            return;

        await Send(new Notification
        {
            Id = Interlocked.Increment(ref idSeed),
            Title = $"New PR #{@event.PullRequest.Number} — {@event.Repo.FullName}",
            Message = $"{@event.PullRequest.Title} (@{@event.PullRequest.Author})"
        }).ConfigureAwait(false);
    }

    public async Task Handle(NewIssuesEvent @event, IMediatorContext context, CancellationToken cancellationToken)
    {
        if (await config.GetNotificationsMutedAsync().ConfigureAwait(false))
            return;

        await Send(new Notification
        {
            Id = Interlocked.Increment(ref idSeed),
            Title = $"New issue{(@event.NewCount == 1 ? "" : "s")} — {@event.Repo.FullName}",
            Message = $"{@event.NewCount} new ({@event.TotalOpen} open total)"
        }).ConfigureAwait(false);
    }

    async Task Send(Notification n)
    {
        try { await notifications.Send(n).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to send notification"); }
    }
}
