using Shiny.Mediator;

namespace GitHubMon.Dashboard;

public sealed class PollerInitializer : IMauiInitializeService, IDisposable
{
    readonly IConfigStore config;
    readonly IMediator mediator;
    readonly ILogger<PollerInitializer> logger;
    CancellationTokenSource? cts;
    Task? loop;

    public PollerInitializer(IConfigStore config, IMediator mediator, ILogger<PollerInitializer> logger)
    {
        this.config = config;
        this.mediator = mediator;
        this.logger = logger;
    }

    public void Initialize(IServiceProvider services)
    {
        cts = new CancellationTokenSource();
        loop = Task.Run(() => RunAsync(cts.Token));
    }

    async Task RunAsync(CancellationToken ct)
    {
        await config.ReloadAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
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
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
    }
}
