using Shiny.Mediator;

namespace GitHubMon.Dashboard;

public sealed class PollerInitializer : IMauiInitializeService, IDisposable
{
    readonly IConfigStore config;
    readonly IMediator mediator;
    readonly ILogger<PollerInitializer> logger;
    CancellationTokenSource? cts;
    Task? loop;
    volatile TaskCompletionSource wakeSignal = NewSignal();

    public PollerInitializer(IConfigStore config, IMediator mediator, ILogger<PollerInitializer> logger)
    {
        this.config = config;
        this.mediator = mediator;
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

        while (!ct.IsCancellationRequested)
        {
            // Arm the wake signal BEFORE refreshing so a config change that lands
            // mid-refresh still triggers an immediate follow-up cycle.
            wakeSignal = NewSignal();
            var wake = wakeSignal.Task;

            try
            {
                await mediator.Send(new RefreshAllCommand(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refresh cycle failed");
            }

            var interval = await config.GetPollIntervalAsync().ConfigureAwait(false);
            try { await Task.WhenAny(Task.Delay(interval, ct), wake).ConfigureAwait(false); }
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
