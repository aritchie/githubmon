using Shiny.Mediator;

namespace GitHubShine.Persons.Mediator;

/// <summary>
/// Re-reads every followed login. <paramref name="Force"/> false is the cold-start paint (serve
/// whatever the persistent cache holds, touch no network); true is a live fetch.
/// </summary>
public sealed record RefreshPersonsCommand(bool Force = false) : ICommand;
