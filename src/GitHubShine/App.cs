namespace GitHubShine;

public class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
        => new(new MainPage())
        {
            Title = "GitHub Shine",
            Width = 1100,
            Height = 760
        };
}
