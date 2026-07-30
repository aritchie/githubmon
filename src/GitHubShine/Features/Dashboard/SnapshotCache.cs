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

    /// <summary>
    /// Workflows whose latest completed run is green after the state last recorded for them was
    /// red. Writes the new state as it goes, so each red-to-green transition is reported once.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowRunSummary>> RecoveredRunsAsync(RepoSnapshot snap, CancellationToken ct)
    {
        var recovered = new List<WorkflowRunSummary>();
        var now = DateTimeOffset.UtcNow;

        // Newest *completed* run per workflow — a queued or running one says nothing about the
        // outcome yet, and the snapshot holds several runs per workflow.
        var latestPerWorkflow = snap.RecentWorkflowRuns
            .Where(r => !r.IsInProgress)
            .GroupBy(r => r.WorkflowName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).First());

        foreach (var run in latestPerWorkflow)
        {
            // Cancelled/skipped is neither red nor green — leave the recorded state alone rather
            // than letting a cancelled run count as a recovery from the failure before it.
            if (!run.IsFailed && !run.IsSuccess)
                continue;

            var id = WorkflowStateId(snap.AccountId, snap.Repo, run.WorkflowName);
            var prev = await store.Get<SeenWorkflowState>(id, cancellationToken: ct).ConfigureAwait(false);
            if (prev is not null && prev.Failing == run.IsFailed)
                continue; // unchanged — nothing to write, nothing to announce

            // No previous state = first sighting: record the baseline only. Announcing a
            // recovery here would fire on every first-ever green run.
            if (prev is not null && run.IsSuccess)
                recovered.Add(run);

            await store.Upsert(new SeenWorkflowState(id, run.IsFailed, now), cancellationToken: ct).ConfigureAwait(false);
        }
        return recovered;
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
    static string WorkflowStateId(string accountId, MonitoredRepo repo, string workflowName)
        => $"{accountId}|{repo.FullName}|wf:{workflowName}";
}
