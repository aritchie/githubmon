using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

public sealed record ListInboxRequest(MonitoredAccount Account) : IRequest<IReadOnlyList<InboxItem>>;
