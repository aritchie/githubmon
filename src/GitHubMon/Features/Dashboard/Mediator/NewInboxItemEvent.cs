using Shiny.Mediator;

namespace GitHubMon.Dashboard.Mediator;

public sealed record NewInboxItemEvent(string AccountId, InboxItem Item) : IEvent;
