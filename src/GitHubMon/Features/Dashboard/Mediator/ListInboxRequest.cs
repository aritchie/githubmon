using Shiny.Mediator;

namespace GitHubMon.Dashboard.Mediator;

public sealed record ListInboxRequest(MonitoredAccount Account) : IRequest<IReadOnlyList<InboxItem>>;
