using Shiny;

namespace GitHubMon.Dashboard;

public partial class DashboardPage : ContentPage
{
    public DashboardPage()
    {
        InitializeComponent();
        // ShinyShell's IPageLifecycleAware dispatch relies on Shell.OnNavigated,
        // which doesn't fire reliably on the maui-labs MacOS Shell handler. Wire
        // MAUI Core's Loaded/Unloaded events (handler-pipeline, always fires) as
        // a backstop so the VM's OnAppearing/OnDisappearing actually run.
        Loaded += (_, _) => (BindingContext as IPageLifecycleAware)?.OnAppearing();
        Unloaded += (_, _) => (BindingContext as IPageLifecycleAware)?.OnDisappearing();
    }
}
