using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

public sealed record NewInboxItemEvent(string AccountId, InboxItem Item) : IEvent;
