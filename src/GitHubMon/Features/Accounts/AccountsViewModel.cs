using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;

namespace GitHubMon.Accounts;

[ShellMap<AccountsPage>("Accounts", registerRoute: false)]
public sealed partial class AccountsViewModel(IConfigStore config, INavigator navigator, IDialogs dialogs)
    : ObservableObject, IPageLifecycleAware
{
    public ObservableCollection<MonitoredAccount> Accounts { get; } = new();

    // See DashboardViewModel.RunOnUi — MAUI Essentials' static MainThread doesn't
    // work on maui-labs MacOS/Linux; MAUI Core's IDispatcher does.
    static void RunOnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is not null) d.Dispatch(action);
        else action();
    }

    public async void OnAppearing()
    {
        await config.ReloadAsync().ConfigureAwait(false);
        RunOnUi(Reload);
        config.Changed += OnConfigChanged;
    }

    public void OnDisappearing()
        => config.Changed -= OnConfigChanged;

    void OnConfigChanged(object? sender, EventArgs e)
        => RunOnUi(Reload);

    void Reload()
    {
        Accounts.Clear();
        foreach (var a in config.Accounts) Accounts.Add(a);
    }

    [RelayCommand]
    Task AddAsync()
        => navigator.NavigateTo<AccountEditViewModel>(vm => vm.Load(null));

    [RelayCommand]
    Task EditAsync(MonitoredAccount account)
        => navigator.NavigateTo<AccountEditViewModel>(vm => vm.Load(account));

    [RelayCommand]
    async Task DeleteAsync(MonitoredAccount account)
    {
        var confirm = await dialogs.Confirm("Delete account?",
            $"Remove {account.Label} and stop monitoring its repos?", "Delete", "Cancel");
        if (!confirm) return;
        await config.RemoveAccountAsync(account.Id);
    }
}
