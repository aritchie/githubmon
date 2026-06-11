using System.Collections.Concurrent;
using Shiny;
using Shiny.DocumentDb;

namespace GitHubShine.Dashboard;

[Singleton(AsSelf = true)]
public sealed class SnapshotCache(IDocumentStore store)
{
    readonly ConcurrentDictionary<string, RepoSnapshot> snapshots = new();

    public IReadOnlyDictionary<string, RepoSnapshot> All => snapshots;

    public RepoSnapshot? TryGet(string accountId, MonitoredRepo repo)
        => snapshots.TryGetValue(Key(accountId, repo), out var snap) ? snap : null;

    public RepoSnapshot? PutSnapshot(RepoSnapshot snap)
    {
        var key = Key(snap.AccountId, snap.Repo);
        snapshots.TryGetValue(key, out var prev);
        snapshots[key] = snap;
        return prev;
    }

    public async Task<IReadOnlyList<WorkflowRunSummary>> NewlyFailedRunsAsync(RepoSnapshot snap, CancellationToken ct)
    {
        var fresh = new List<WorkflowRunSummary>();
        var now = DateTimeOffset.UtcNow;
        foreach (var run in snap.RecentWorkflowRuns)
        {
            if (!run.IsFailed) continue;
            var id = FailedRunId(snap.AccountId, snap.Repo, run.Id);
            var existing = await store.Get<SeenFailedRun>(id, cancellationToken: ct).ConfigureAwait(false);
            if (existing is not null) continue;
            await store.Insert(new SeenFailedRun(id, now), cancellationToken: ct).ConfigureAwait(false);
            fresh.Add(run);
        }
        return fresh;
    }

    public async Task<IReadOnlyList<InboxItem>> RecordInboxAsync(string accountId, IReadOnlyList<InboxItem> items, CancellationToken ct)
    {
        var fresh = new List<InboxItem>();
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            var id = InboxItemId(accountId, item.ThreadId);
            var existing = await store.Get<SeenInboxItem>(id, cancellationToken: ct).ConfigureAwait(false);
            if (existing is not null) continue;
            await store.Insert(new SeenInboxItem(id, now), cancellationToken: ct).ConfigureAwait(false);
            fresh.Add(item);
        }
        return fresh;
    }

    static string Key(string accountId, MonitoredRepo repo) => $"{accountId}|{repo.FullName}";
    static string FailedRunId(string accountId, MonitoredRepo repo, long runId) => $"{accountId}|{repo.FullName}|{runId}";
    static string InboxItemId(string accountId, string threadId) => $"{accountId}|{threadId}";
}
