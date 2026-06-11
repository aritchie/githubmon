using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

public sealed record InboxUpdatedEvent(string AccountId, IReadOnlyList<InboxItem> Items) : IEvent;
