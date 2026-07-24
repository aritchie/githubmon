using Shiny;

namespace GitHubShine.Providers;

/// <summary>
/// Tracks the most recent <c>X-RateLimit-*</c> headers seen on any provider response (fed by
/// <see cref="ConditionalGetHandler"/>) and turns them into a poll-delay recommendation. When the
/// remaining budget is healthy the poller runs at the user's configured interval (with a little
/// jitter); when it dips below a reserve we stretch the wait out to the reset time so background
/// polling stops eating into headroom the user needs for interactive actions.
/// </summary>
[Singleton(AsSelf = true)]
public sealed class RateLimitMonitor
{
    // Keep this many calls in reserve for user-initiated work (opening a repo, downloading an
    // archive) rather than spending the whole hourly budget on background polling.
    const int ReserveThreshold = 100;

    volatile Snapshot? latest;

    public Snapshot? Latest => latest;

    public void Report(int remaining, int? limit, DateTimeOffset? reset)
        => this.latest = new Snapshot(remaining, limit, reset, DateTimeOffset.UtcNow);

    /// <summary>
    /// How long the poller should wait before its next cycle, given the user's configured interval.
    /// </summary>
    public TimeSpan NextDelay(TimeSpan interval)
    {
        var snap = this.latest;
        if (snap is { Remaining: <= ReserveThreshold, Reset: { } reset })
        {
            // Budget is nearly spent — idle until it refills (plus a small buffer past the reset).
            var untilReset = reset - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            if (untilReset > interval)
                return untilReset;
        }
        return Jitter(interval);
    }

    // +/-10% so multiple repos/accounts (and app restarts) don't all fire on the exact same tick.
    static TimeSpan Jitter(TimeSpan interval)
    {
        var delta = interval.TotalMilliseconds * 0.1;
        var offset = (Random.Shared.NextDouble() * 2 - 1) * delta;
        return TimeSpan.FromMilliseconds(Math.Max(1000, interval.TotalMilliseconds + offset));
    }

    public sealed record Snapshot(int Remaining, int? Limit, DateTimeOffset? Reset, DateTimeOffset ObservedAt);
}
