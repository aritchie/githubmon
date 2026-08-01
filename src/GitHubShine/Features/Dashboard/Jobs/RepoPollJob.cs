using Shiny.Jobs;
using Shiny.Mediator;

namespace GitHubShine.Dashboard.Jobs;

/// <summary>
/// The background GitHub refresh — one cycle of what <c>PollerInitializer</c> used to loop.
///
/// Replacing that loop matters most on mobile: it was a plain <c>Task.Run</c> that only lived as
/// long as the process, so the moment iOS suspended the app, polling — and therefore every
/// notification that depends on it — stopped, with nothing to wake it. As a Shiny job the OS
/// schedulers drive it instead (BGTaskScheduler / WorkManager), and the desktop heads keep an
/// equivalent in-process timer via Shiny's plain-.NET JobManager.
///
/// The job does NOT assume the schedule is the interval — see <see cref="IPollSchedule"/>. It is
/// invoked far more often than it should refresh and gates itself on a stored timestamp.
///
/// Deliberately not <c>Shiny.Jobs.Job</c> (the base class with <c>MinimumTime</c>): that throttle
/// keeps its last-run time in memory, which is exactly the state a background job loses. It would
/// pass on every cold start.
/// </summary>
public sealed class RepoPollJob(
    IMediator mediator,
    IConfigStore config,
    IPollSchedule schedule,
    ILogger<RepoPollJob> logger) : IJob
{
    public async Task Run(CancellationToken cancelToken)
    {
        var due = await schedule.IsDueAsync(cancelToken).ConfigureAwait(false);
        if (!due.IsDue)
        {
            logger.LogDebug("[Poll] skipped — {Reason}", due.Reason);
            return;
        }

        // A background invocation gets a cold process: the accounts list is populated by
        // ReloadAsync, not by the constructor, so without this there is nothing to poll.
        if (config.Accounts.Count == 0)
            await config.ReloadAsync(cancelToken).ConfigureAwait(false);

        if (config.Accounts.Count == 0)
        {
            logger.LogDebug("[Poll] skipped — no accounts configured");
            return;
        }

        logger.LogDebug("[Poll] refreshing {Count} account(s)", config.Accounts.Count);
        await mediator.Send(new RefreshAllCommand(Force: true), cancelToken).ConfigureAwait(false);

        // Only a completed refresh restarts the interval. If the send threw, the exception
        // propagates to the job manager (which logs it and reports it on JobRunResult) and the
        // timestamp is left alone, so the next invocation retries rather than waiting out a full
        // interval on the back of a failure.
        await schedule.MarkRanAsync(cancelToken).ConfigureAwait(false);
    }
}
