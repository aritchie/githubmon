using System.Text.Json.Serialization;

namespace GitHubShine.Sync;

/// <summary>
/// How (and whether) the background sync runs. Lives in the document store rather than the
/// key/value prefs store — the desktop heads' key/value store is in-memory, so a preference
/// written there wouldn't survive a restart, and a raw SQLite backup carries this along with
/// the mappings it configures. Single row, fixed id.
/// </summary>
public sealed record AutoSyncPrefs(
    string Id,
    bool Enabled = true,
    // Literal rather than MinimumIntervalHours: a record's parameter defaults are bound outside
    // the type, so they can't see its own constants.
    int IntervalHours = 3,
    bool SyncAll = false,
    DateTimeOffset? LastRunUtc = null,
    // Persisted rather than in-memory because the pass now runs as a job: the process that
    // recorded a durable failure (git missing, no accounts) is usually gone by the next
    // invocation, and an in-memory back-off would be forgotten every time — turning "check back
    // in an hour" into "retry on every single job tick".
    DateTimeOffset? RetryAfterUtc = null)
{
    public const string DefaultId = "default";

    /// <summary>
    /// A sync is a full fetch/push cycle per mapping — network- and disk-heavy, and nothing like
    /// as cheap as a dashboard poll. Three hours is the floor as well as the default.
    /// </summary>
    public const int MinimumIntervalHours = 3;

    public static AutoSyncPrefs Default => new(DefaultId);

    /// <summary>The configured interval, clamped to <see cref="MinimumIntervalHours"/>.</summary>
    [JsonIgnore]
    public TimeSpan Interval => TimeSpan.FromHours(Math.Max(MinimumIntervalHours, this.IntervalHours));
}
