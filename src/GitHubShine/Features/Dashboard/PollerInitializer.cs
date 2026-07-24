using Shiny.Mediator;

namespace GitHubShine.Dashboard;

public sealed class PollerInitializer : IMauiInitializeService, IDisposable
{
    readonly IConfigStore config;
    readonly IMediator mediator;
    readonly RateLimitMonitor rateLimits;
    readonly ILogger<PollerInitializer> logger;
    CancellationTokenSource? cts;
    Task? loop;
    volatile TaskCompletionSource wakeSignal = NewSignal();

    public PollerInitializer(IConfigStore config, IMediator mediator, RateLimitMonitor rateLimits, ILogger<PollerInitializer> logger)
    {
        this.config = config;
        this.mediator = mediator;
        this.rateLimits = rateLimits;
        this.logger = logger;
    }

    static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Initialize(IServiceProvider services)
    {
        cts = new CancellationTokenSource();
        loop = Task.Run(() => RunAsync(cts.Token));
    }

    // Accounts/repos changed (added, edited, removed) — don't make the user wait
    // out the poll interval to see fresh data on the dashboard.
    void OnConfigChanged(object? sender, EventArgs e)
        => wakeSignal.TrySetResult();

    async Task RunAsync(CancellationToken ct)
    {
        await config.ReloadAsync(ct).ConfigureAwait(false);
        config.Changed += OnConfigChanged;

        var firstPass = true;
        while (!ct.IsCancellationRequested)
        {
            // Arm the wake signal BEFORE refreshing so a config change that lands
            // mid-refresh still triggers an immediate follow-up cycle.
            wakeSignal = NewSignal();
            var wake = wakeSignal.Task;

            logger.LogDebug("[Poller] cycle force={Force}", !firstPass);
            try
            {
                // The first cycle paints instantly from the persistent snapshot cache (Force:false
                // → cache hit, no network). Every cycle after forces a live refresh — cheap in
                // practice because ETag conditional requests return 304s that don't cost budget.
                await mediator.Send(new RefreshAllCommand(Force: !firstPass), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refresh cycle failed");
            }

            if (firstPass)
            {
                // Immediately follow the cached paint with a live refresh instead of waiting out
                // the interval.
                firstPass = false;
                continue;
            }

            var interval = await config.GetPollIntervalAsync().ConfigureAwait(false);
            var delay = rateLimits.NextDelay(interval);
            try { await Task.WhenAny(Task.Delay(delay, ct), wake).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        config.Changed -= OnConfigChanged;
        cts?.Cancel();
        cts?.Dispose();
    }
}
