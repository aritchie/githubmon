namespace GitHubMon;

public class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
        => new(new MainPage())
        {
            Title = "GitHubMon",
            Width = 1100,
            Height = 760
        };
}
