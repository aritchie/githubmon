using Shiny.Mediator;
using Shiny.Notifications;

namespace GitHubShine.Dashboard.Mediator;

/// <summary>
/// Turns poll events into user-facing alerts. Every handler gates on the matching
/// <see cref="NotificationCategory"/>, which covers the global mute too — see
/// <see cref="NotificationPrefs.IsEnabled"/>.
///
/// Wording here stays provider-neutral: the same events come from every IGitProvider
/// (GitHub, Gitea, and whatever follows), so nothing names a host or a vendor.
/// </summary>
[MediatorSingleton]
public sealed class NotificationDispatchHandler(
    INotificationHub notifications,
    INotificationPrefsStore prefs,
    IConfigStore config)
    : IEventHandler<WorkflowRunFailedEvent>,
      IEventHandler<WorkflowRunRecoveredEvent>,
      IEventHandler<NewInboxItemEvent>,
      IEventHandler<NewPullRequestEvent>,
      IEventHandler<NewIssuesEvent>,
      IEventHandler<NewStarsEvent>,
      IEventHandler<NewForksEvent>
{
    static int idSeed = 1000;

    public async Task Handle(WorkflowRunFailedEvent @event, IMediatorContext context, CancellationToken cancellationToken)
    {
        if (!await On(NotificationCategory.Builds, cancellationToken).ConfigureAwait(false))
            return;

        await Send(new Notification
        {
            Id = Interlocked.Increment(ref idSeed),
            Title = $"Workflow failed — {Where(@event.AccountId, @event.Repo)}",
            Message = $"{@event.Run.WorkflowName} on {@event.Run.Branch}"
        }).ConfigureAwait(false);
    }

    public async Task Handle(WorkflowRunRecoveredEvent @event, IMediatorContext context, CancellationToken cancellationToken)
    {
        if (!await On(NotificationCategory.Builds, cancellationToken).ConfigureAwait(false))
            return;

        await Send(new Notification
        {
            Id = Interlocked.Increment(ref idSeed),
            Title = $"Workflow back to green — {Where(@event.AccountId, @event.Repo)}",
            Message = $"{@event.Run.WorkflowName} on {@event.Run.Branch}"
        }).ConfigureAwait(false);
    }

    public async Task Handle(NewInboxItemEvent @event, IMediatorContext context, CancellationToken cancellationToken)
    {
        if (!await On(NotificationCategory.Inbox, cancellationToken).ConfigureAwait(false))
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
            Title = $"You were {label} — {Where(@event.AccountId, @event.Item.RepoFullName)}",
            Message = @event.Item.Title
        }).ConfigureAwait(false);
    }

    public async Task Handle(NewPullRequestEvent @event, IMediatorContext context, CancellationToken cancellationToken)
    {
        if (!await On(NotificationCategory.PullRequests, cancellationToken).ConfigureAwait(false))
            return;

        await Send(new Notification
        {
            Id = Interlocked.Increment(ref idSeed),
            Title = $"New PR #{@event.PullRequest.Number} — {Where(@event.AccountId, @event.Repo)}",
            Message = $"{@event.PullRequest.Title} (@{@event.PullRequest.Author})"
        }).ConfigureAwait(false);
    }

    public async Task Handle(NewIssuesEvent @event, IMediatorContext context, CancellationToken cancellationToken)
    {
        if (!await On(NotificationCategory.Issues, cancellationToken).ConfigureAwait(false))
            return;

        await Send(new Notification
        {
            Id = Interlocked.Increment(ref idSeed),
            Title = $"New issue{Plural(@event.NewCount)} — {Where(@event.AccountId, @event.Repo)}",
            Message = $"{@event.NewCount} new ({@event.TotalOpen} open total)"
        }).ConfigureAwait(false);
    }

    public async Task Handle(NewStarsEvent @event, IMediatorContext context, CancellationToken cancellationToken)
    {
        if (!await On(NotificationCategory.Stars, cancellationToken).ConfigureAwait(false))
            return;

        await Send(new Notification
        {
            Id = Interlocked.Increment(ref idSeed),
            Title = $"New star{Plural(@event.NewCount)} — {Where(@event.AccountId, @event.Repo)}",
            Message = $"{@event.NewCount} new ({@event.Total} total)"
        }).ConfigureAwait(false);
    }

    public async Task Handle(NewForksEvent @event, IMediatorContext context, CancellationToken cancellationToken)
    {
        if (!await On(NotificationCategory.Forks, cancellationToken).ConfigureAwait(false))
            return;

        await Send(new Notification
        {
            Id = Interlocked.Increment(ref idSeed),
            Title = $"New fork{Plural(@event.NewCount)} — {Where(@event.AccountId, @event.Repo)}",
            Message = $"{@event.NewCount} new ({@event.Total} total)"
        }).ConfigureAwait(false);
    }

    Task<bool> On(NotificationCategory category, CancellationToken ct) => prefs.IsEnabledAsync(category, ct);

    string Where(string accountId, MonitoredRepo repo) => Where(accountId, repo.FullName);

    /// <summary>
    /// "owner/name" isn't unique once more than one provider is in play — the same path can
    /// exist on a GitHub account and a Gitea one. Name the account too, but only when there's
    /// more than one account to confuse.
    /// </summary>
    string Where(string accountId, string repoFullName)
    {
        var accounts = config.Accounts;
        if (accounts.Count < 2)
            return repoFullName;

        var label = accounts.FirstOrDefault(a => a.Id == accountId)?.Label;
        return string.IsNullOrWhiteSpace(label) ? repoFullName : $"{repoFullName} ({label})";
    }

    static string Plural(int count) => count == 1 ? "" : "s";

    // INotificationHub handles permission, platform support and error swallowing (see NotificationHub).
    Task Send(Notification n) => notifications.SendAsync(n);
}
