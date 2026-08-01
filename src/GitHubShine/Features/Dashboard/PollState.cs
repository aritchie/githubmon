namespace GitHubShine.Dashboard;

/// <summary>
/// When the background refresh last actually ran.
///
/// This exists because a job is not a timer. The OS decides when to invoke
/// <see cref="Jobs.RepoPollJob"/> — WorkManager on a 15-minute floor, BGTaskScheduler whenever it
/// feels like it, and the foreground driver every ~30–60s while the app is open — so the job fires
/// far more often than we want to hit the API. The interval therefore has to be enforced by the
/// job itself, against a stored timestamp, rather than by the schedule.
///
/// It's a document-store row, not a key/value pref, for the reason spelled out on
/// <see cref="Sync.AutoSyncPrefs"/>: the desktop heads' key/value store is in-memory, so a
/// timestamp written there would reset on every launch — which for a 3-hour mobile interval would
/// mean "refresh on every cold start" and defeat the whole guard. Single row, fixed id.
/// </summary>
public sealed record PollState(
    string Id,
    DateTimeOffset? LastRunUtc = null)
{
    public const string DefaultId = "default";

    public static PollState Default => new(DefaultId);
}
