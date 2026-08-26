
namespace GitHubShine.Accounts;

public interface IConfigStore
{
    IReadOnlyList<MonitoredAccount> Accounts { get; }

    /// <summary>Raised when accounts/repos change — consumers re-read <see cref="Accounts"/>.</summary>
    event EventHandler? Changed;

    /// <summary>Raised when a user preference (grid sort, poll interval) changes. Kept
    /// separate from <see cref="Changed"/> so a pref tweak doesn't trigger a full account refresh.
    /// Notification preferences have their own store and event — see INotificationPrefsStore.</summary>
    event EventHandler? PreferencesChanged;

    Task ReloadAsync(CancellationToken ct = default);
    Task UpsertAccountAsync(MonitoredAccount account, CancellationToken ct = default);
    Task RemoveAccountAsync(string accountId, CancellationToken ct = default);

    Task<TimeSpan> GetPollIntervalAsync();
    Task SetPollIntervalAsync(TimeSpan interval);

    /// <summary>The repositories grid's last-used sort column and direction. The tray menu
    /// orders its "Repositories" submenu by the same thing, so it matches the grid.</summary>
    Task<RepoGridSort> GetRepoGridSortAsync();
    Task SetRepoGridSortAsync(RepoGridSort sort);

    /// <summary>Which repository view the dashboard was last showing — cards or the grid.</summary>
    Task<RepoViewMode> GetRepoViewAsync();
    Task SetRepoViewAsync(RepoViewMode view);

    /// <summary>The people grid's last-used sort column and direction.</summary>
    Task<PersonGridSort> GetPersonGridSortAsync();
    Task SetPersonGridSortAsync(PersonGridSort sort);

    /// <summary>Dashboard card order as "accountId|owner/name" keys. Empty = natural (account/repo) order.</summary>
    Task<IReadOnlyList<string>> GetRepoOrderAsync();
    Task SetRepoOrderAsync(IReadOnlyList<string> orderedKeys);
}
