using Shiny.Mediator;

namespace GitHubShine.Dashboard;

/// <summary>
/// The two jobs-can't-do-this halves of the old <c>PollerInitializer</c> loop.
///
/// <para>1. The instant paint. The first cycle used to send <c>RefreshAllCommand(Force:false)</c>,
/// which hits the persistent mediator cache and returns without touching the network, so the
/// dashboard has cards on it the moment the window opens. A scheduled job can't cover this: it is
/// a UI-startup concern, not a refresh, and it must happen whether or not a refresh is due.</para>
///
/// <para>2. The wake. Adding or editing an account should show fresh data immediately rather than
/// after the interval — three hours of it on mobile. Interactive refreshes bypass the schedule
/// entirely, the same way the tray's "Refresh now" and the dashboard's refresh button already do.
/// This one also stamps the schedule, since having just refreshed is a reason not to refresh
/// again a moment later.</para>
///
/// The periodic live refresh itself belongs to <see cref="Jobs.RepoPollJob"/>.
/// </summary>
public sealed class PollStartupService(
    IConfigStore config,
    IMediator mediator,
    IPollSchedule schedule,
    ILogger<PollStartupService> logger) : IMauiInitializeService, IDisposable
{
    CancellationTokenSource? cts;

    public void Initialize(IServiceProvider services)
    {
        this.cts = new CancellationTokenSource();
        _ = Task.Run(() => this.StartAsync(this.cts.Token));
    }

    async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await config.ReloadAsync(ct).ConfigureAwait(false);
            config.Changed += this.OnConfigChanged;

            // Cache-only: no network, no notifications, no schedule stamp.
            await mediator.Send(new RefreshAllCommand(Force: false), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Poll] startup paint failed");
        }
    }

    // Accounts/repos changed (added, edited, removed) — don't make the user wait out the
    // interval to see fresh data on the dashboard.
    void OnConfigChanged(object? sender, EventArgs e)
        => _ = Task.Run(async () =>
        {
            var ct = this.cts?.Token ?? CancellationToken.None;
            try
            {
                await mediator.Send(new RefreshAllCommand(Force: true), ct).ConfigureAwait(false);
                await schedule.MarkRanAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Poll] refresh after config change failed");
            }
        });

    public void Dispose()
    {
        config.Changed -= this.OnConfigChanged;
        this.cts?.Cancel();
        this.cts?.Dispose();
    }
}
