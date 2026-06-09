using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.Mediator;

namespace GitHubMon.Dashboard;

[ShellMap<DashboardPage>("Dashboard", registerRoute: false)]
public sealed partial class DashboardViewModel : ObservableObject, IPageLifecycleAware, IDisposable
{
    readonly IMediator mediator;
    readonly IConfigStore config;
    readonly SnapshotCache cache;
    readonly INavigator navigator;
    readonly List<IDisposable> subs = new();
    bool suppressMutePersist;

    // MAUI Essentials' static MainThread is broken on the maui-labs MacOS/Linux
    // backends ("Not supported in portable version"). MAUI Core's IDispatcher
    // (Application.Current.Dispatcher) is part of the handler pipeline and works.
    static void RunOnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is not null) d.Dispatch(action);
        else action();
    }

    public ObservableCollection<RepoCardViewModel> Cards { get; } = new();
    public ObservableCollection<InboxItem> Inbox { get; } = new();

    [ObservableProperty] bool isBusy;
    [ObservableProperty] bool notificationsMuted;
    [ObservableProperty] string? statusText;
    [ObservableProperty] bool isEmpty = true;

    public DashboardViewModel(IMediator mediator, IConfigStore config, SnapshotCache cache, INavigator navigator)
    {
        this.mediator = mediator;
        this.config = config;
        this.cache = cache;
        this.navigator = navigator;
        config.Changed += OnConfigChanged;
        subs.Add(mediator.Subscribe<SnapshotUpdatedEvent>(OnSnapshotUpdated));
        subs.Add(mediator.Subscribe<InboxUpdatedEvent>(OnInboxUpdated));
    }

    public async void OnAppearing()
    {
        await config.ReloadAsync().ConfigureAwait(false);
        var muted = await config.GetNotificationsMutedAsync().ConfigureAwait(false);
        RunOnUi(() =>
        {
            suppressMutePersist = true;
            try { NotificationsMuted = muted; }
            finally { suppressMutePersist = false; }
            RebuildCards();
            foreach (var card in Cards)
            {
                var existing = cache.TryGet(card.Account.Id, card.Repo);
                if (existing is not null) card.Apply(existing);
            }
        });
    }

    public void OnDisappearing() { }

    void OnConfigChanged(object? sender, EventArgs e)
        => RunOnUi(RebuildCards);

    void RebuildCards()
    {
        var existing = Cards.ToDictionary(c => c.Key);
        var desired = config.Accounts.SelectMany(a => a.Repos.Select(r => (Account: a, Repo: r))).ToList();
        var desiredKeys = desired.Select(d => $"{d.Account.Id}|{d.Repo.FullName}").ToHashSet();

        for (var i = Cards.Count - 1; i >= 0; i--)
            if (!desiredKeys.Contains(Cards[i].Key)) Cards.RemoveAt(i);

        foreach (var (account, repo) in desired)
        {
            var key = $"{account.Id}|{repo.FullName}";
            if (!existing.ContainsKey(key))
                Cards.Add(new RepoCardViewModel(account, repo));
        }

        IsEmpty = Cards.Count == 0;
        StatusText = IsEmpty ? null : $"{Cards.Count} repo{(Cards.Count == 1 ? "" : "s")} monitored";
    }

    Task OnSnapshotUpdated(SnapshotUpdatedEvent e, IMediatorContext _, CancellationToken __)
    {
        RunOnUi(() =>
        {
            var key = $"{e.AccountId}|{e.Repo.FullName}";
            var card = Cards.FirstOrDefault(c => c.Key == key);
            card?.Apply(e.Snapshot);
        });
        return Task.CompletedTask;
    }

    Task OnInboxUpdated(InboxUpdatedEvent e, IMediatorContext _, CancellationToken __)
    {
        RunOnUi(() =>
        {
            var others = Inbox.Where(i => i.AccountId != e.AccountId).ToList();
            Inbox.Clear();
            foreach (var i in others.Concat(e.Items)
                .Where(i => i.Reason is InboxReason.Mention or InboxReason.Assigned or InboxReason.ReviewRequested)
                .OrderByDescending(i => i.UpdatedAt))
                Inbox.Add(i);
        });
        return Task.CompletedTask;
    }

    partial void OnNotificationsMutedChanged(bool value)
    {
        if (suppressMutePersist) return;
        _ = config.SetNotificationsMutedAsync(value);
    }

    [RelayCommand]
    async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await mediator.Send(new RefreshAllCommand(true)).ConfigureAwait(false); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    Task AddAccountAsync()
        => navigator.NavigateTo<AccountEditViewModel>(vm => vm.Load(null));

    [RelayCommand]
    Task ManageAccountsAsync()
        => navigator.NavigateTo<AccountsViewModel>(relativeNavigation: false);

    public void Dispose()
    {
        config.Changed -= OnConfigChanged;
        foreach (var s in subs) s.Dispose();
    }
}
