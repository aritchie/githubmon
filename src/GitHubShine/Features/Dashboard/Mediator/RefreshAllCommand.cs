using Shiny.Mediator;

namespace GitHubShine.Dashboard.Mediator;

public sealed record RefreshAllCommand(bool Force = false) : ICommand;
