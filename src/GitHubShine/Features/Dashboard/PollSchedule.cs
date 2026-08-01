using Shiny;
using Shiny.DocumentDb;
using GitHubShine.Providers;

namespace GitHubShine.Dashboard;

/// <summary>
/// Decides whether a background refresh is due, and records when one ran.
///
/// The job schedule and the refresh interval are deliberately two different things. The OS invokes
/// the job on its own terms (see <see cref="PollState"/>); this type is what turns those arbitrary
/// invocations into "refresh at most every N" by measuring against the stored timestamp.
/// </summary>
public interface IPollSchedule
{
    /// <summary>The interval between live refreshes on this platform.</summary>
    Task<TimeSpan> GetIntervalAsync();

    /// <summary>Whether a refresh is due now. Returns the reason when it isn't, for logging.</summary>
    Task<PollDue> IsDueAsync(CancellationToken ct = default);

    /// <summary>Records that a refresh just completed, restarting the interval.</summary>
    Task MarkRanAsync(CancellationToken ct = default);
}

public readonly record struct PollDue(bool IsDue, string? Reason)
{
    public static PollDue Yes => new(true, null);
    public static PollDue No(string reason) => new(false, reason);
}

[Singleton]
public sealed class PollSchedule(
    IDocumentStore store,
    IConfigStore config,
    RateLimitMonitor rateLimits,
    AppPlatformInfo platform) : IPollSchedule
{
    /// <summary>
    /// Mobile refreshes on a fixed long interval regardless of the desktop setting. Three reasons:
    /// the OS schedulers won't honour anything shorter reliably anyway (WorkManager's floor is 15
    /// minutes and BGTaskScheduler makes no promises at all), a phone waking to hit the API every
    /// two minutes is a battery and rate-limit problem, and notifications only need to be timely
    /// enough to be useful — not instant.
    /// </summary>
    public static readonly TimeSpan MobileInterval = TimeSpan.FromHours(3);

    public Task<TimeSpan> GetIntervalAsync()
        => platform.IsMobile
            ? Task.FromResult(MobileInterval)
            : config.GetPollIntervalAsync();

    public async Task<PollDue> IsDueAsync(CancellationToken ct = default)
    {
        // Budget nearly spent: hold off until it refills rather than spending the reserve the
        // user needs for interactive work. Checked before the interval so a rate-limited app
        // doesn't churn through the due check every time the job fires.
        if (rateLimits.DeferUntilUtc() is { } until)
            return PollDue.No($"rate limit reserve low, holding until {until:u}");

        var state = await this.LoadAsync(ct).ConfigureAwait(false);
        if (state.LastRunUtc is not { } last)
            return PollDue.Yes;

        var interval = await this.GetIntervalAsync().ConfigureAwait(false);
        var elapsed = DateTimeOffset.UtcNow - last;

        // A clock that moved backwards (timezone change, NTP correction, a restored backup from
        // the future) would otherwise wedge the poller until real time caught up.
        if (elapsed < TimeSpan.Zero)
            return PollDue.Yes;

        return elapsed >= interval
            ? PollDue.Yes
            : PollDue.No($"last run {elapsed.TotalMinutes:F0}m ago, interval {interval.TotalMinutes:F0}m");
    }

    public async Task MarkRanAsync(CancellationToken ct = default)
    {
        var state = await this.LoadAsync(ct).ConfigureAwait(false);
        // patchIfUpdate:false — this is a whole-document read/modify/write, not a merge.
        await store
            .Upsert(state with { LastRunUtc = DateTimeOffset.UtcNow }, patchIfUpdate: false, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    async Task<PollState> LoadAsync(CancellationToken ct)
        => await store.Get<PollState>(PollState.DefaultId, cancellationToken: ct).ConfigureAwait(false)
           ?? PollState.Default;
}
