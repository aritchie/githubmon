using Shiny;

namespace GitHubMon.Accounts;

public partial class AccountsPage : ContentPage
{
    public AccountsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (BindingContext as IPageLifecycleAware)?.OnAppearing();
        Unloaded += (_, _) => (BindingContext as IPageLifecycleAware)?.OnDisappearing();
    }
}
