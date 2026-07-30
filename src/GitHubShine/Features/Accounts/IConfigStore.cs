
namespace GitHubShine.Accounts;

public interface IConfigStore
{
    IReadOnlyList<MonitoredAccount> Accounts { get; }

    /// <summary>Raised when accounts/repos change — consumers re-read <see cref="Accounts"/>.</summary>
    event EventHandler? Changed;

    /// <summary>Raised when a user preference (repo sort, poll interval) changes. Kept
    /// separate from <see cref="Changed"/> so a pref tweak doesn't trigger a full account refresh.
    /// Notification preferences have their own store and event — see INotificationPrefsStore.</summary>
    event EventHandler? PreferencesChanged;

    Task ReloadAsync(CancellationToken ct = default);
    Task UpsertAccountAsync(MonitoredAccount account, CancellationToken ct = default);
    Task RemoveAccountAsync(string accountId, CancellationToken ct = default);

    Task<TimeSpan> GetPollIntervalAsync();
    Task SetPollIntervalAsync(TimeSpan interval);

    /// <summary>How the repo list is ordered in the dashboard sidebar and the tray menu.</summary>
    Task<RepoSort> GetRepoSortAsync();
    Task SetRepoSortAsync(RepoSort sort);

    /// <summary>Dashboard card order as "accountId|owner/name" keys. Empty = natural (account/repo) order.</summary>
    Task<IReadOnlyList<string>> GetRepoOrderAsync();
    Task SetRepoOrderAsync(IReadOnlyList<string> orderedKeys);
}
