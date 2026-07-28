#if DEBUG && LINUX
using Microsoft.Maui.DevFlow.Agent.Gtk;
#endif

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

#if DEBUG && LINUX
    // Every other head's agent comes up from the MauiProgram registration alone. The GTK one
    // attaches to the live Application instead — StartDevFlowAgent extends
    // Microsoft.Maui.Controls.Application, not MauiApp — so it needs starting once the app is
    // actually activated, which is what OnStart gives us.
    protected override void OnStart()
    {
        base.OnStart();
        this.StartDevFlowAgent();
    }
#endif
}
