namespace GitHubShine.Sync;

/// <summary>How a mapping's target compares with its source, as far as we can tell without a fetch.</summary>
public enum SyncFreshness
{
    /// <summary>No last-push time for the source yet — nothing to compare against.</summary>
    Unknown,
    /// <summary>This sync has never run.</summary>
    NeverSynced,
    /// <summary>The source was pushed after the last successful run.</summary>
    Behind,
    /// <summary>The last run is newer than the source's last push.</summary>
    UpToDate
}

/// <summary>
/// The rules the sync page and the background runner both need: whether a mapping can run at all,
/// and whether it looks behind. Shared so a badge on the page and a decision in
/// <see cref="AutoSyncRunner"/> can never disagree about what "behind" means.
/// </summary>
public static class SyncStatus
{
    /// <summary>
    /// Compares the source's last push with the mapping's last run. It's a heuristic, not a ref
    /// comparison: a push to a branch the mapping doesn't carry still reads as "behind", so it can
    /// say "go and sync" when nothing would move. It never claims up-to-date when there are
    /// commits to collect, which is the direction that matters for a backup.
    /// </summary>
    public static SyncFreshness Evaluate(DateTimeOffset? lastRunUtc, DateTimeOffset? sourcePushedAt)
    {
        if (lastRunUtc is null)
            return SyncFreshness.NeverSynced;
        if (sourcePushedAt is null)
            return SyncFreshness.Unknown;
        return sourcePushedAt > lastRunUtc ? SyncFreshness.Behind : SyncFreshness.UpToDate;
    }

    /// <summary>
    /// Works out up-front what <see cref="RepoSyncEngine"/> would only discover mid-run: a missing
    /// account or a source repo that's no longer monitored. Null means the mapping is runnable.
    /// </summary>
    public static string? BlockingIssue(SyncMapping mapping, IReadOnlyList<MonitoredAccount> accounts)
    {
        var source = accounts.FirstOrDefault(a => a.Id == mapping.SourceAccountId);
        if (source is null)
            return "The source account for this sync no longer exists.";

        if (!Monitors(source, mapping.Source))
            return $"{mapping.Source.FullName} isn't monitored on {source.Label} any more.";

        var target = accounts.FirstOrDefault(a => a.Id == mapping.TargetAccountId);
        if (target is null)
            return "The destination account for this sync no longer exists.";

        if (string.Equals(RepoUrl(source, mapping.Source), RepoUrl(target, mapping.TargetRepo), StringComparison.OrdinalIgnoreCase))
            return "The source and destination are the same repo — this sync can't do anything.";

        return null;
    }

    public static bool Monitors(MonitoredAccount account, MonitoredRepo repo)
        => account.Repos.Any(r =>
            string.Equals(r.Owner, repo.Owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Name, repo.Name, StringComparison.OrdinalIgnoreCase));

    public static string RepoUrl(MonitoredAccount account, MonitoredRepo repo)
        => $"{account.WebBaseUrl}/{repo.Owner}/{repo.Name}";
}
