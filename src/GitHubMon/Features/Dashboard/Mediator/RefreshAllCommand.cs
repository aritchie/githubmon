using Shiny.Mediator;

namespace GitHubMon.Dashboard.Mediator;

public sealed record RefreshAllCommand(bool Force = false) : ICommand;
