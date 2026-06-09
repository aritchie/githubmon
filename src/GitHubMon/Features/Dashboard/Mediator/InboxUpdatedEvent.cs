using Shiny.Mediator;

namespace GitHubMon.Dashboard.Mediator;

public sealed record InboxUpdatedEvent(string AccountId, IReadOnlyList<InboxItem> Items) : IEvent;
