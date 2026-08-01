using Shiny;
using Shiny.Jobs;

namespace GitHubShine.Jobs;

/// <summary>
/// Brings the job manager to life at startup.
///
/// This is not optional plumbing — without it nothing runs. <c>AddJob</c> registers
/// <see cref="IJobManager"/> as a singleton, and DI is lazy: if no one ever resolves it, it is
/// never constructed, so its timer never starts and its platform schedulers are never registered.
/// Normally Shiny's own hosting (<c>UseShiny()</c>) does that resolution during startup, but this
/// app can't use it — Shiny.Hosting.Maui ships no net10.0-macos asset, which is why
/// <c>Shiny.IPlatform</c> is hand-registered in MauiProgram too. Same gap, same fix.
///
/// Found by running the desktop head and noticing the jobs never logged: the only thing touching
/// the poll state was the config-change handler.
///
/// <see cref="AbstractJobManager.RequestAccess"/> is what asks the OS for background execution —
/// on iOS it registers the BGTaskScheduler identifiers declared in Info.plist, and returns
/// <c>NotSetup</c> when that declaration is missing, which is worth logging loudly because the
/// symptom otherwise is simply "notifications stopped when the app was closed".
/// </summary>
public sealed class JobStartupService(
    IJobManager jobs,
    ILogger<JobStartupService> logger) : IMauiInitializeService
{
    public void Initialize(IServiceProvider services)
        => _ = Task.Run(async () =>
        {
            try
            {
                // The actual start. Shiny.Jobs' JobManager implements IShinyStartupTask, and its
                // Start() is what arms the recurring timer that runs foreground jobs — normally
                // invoked by Shiny's hosting, which this app doesn't use. Resolving the manager
                // alone constructs it but leaves it idle: the jobs log "registered" and then
                // nothing ever fires. Verified by running the desktop head before and after.
                if (jobs is IShinyStartupTask startup)
                    startup.Start();

                var registered = jobs.GetJobs();
                logger.LogInformation(
                    "[Jobs] {Count} job(s) registered: {Jobs}",
                    registered.Count,
                    string.Join(", ", registered.Keys.Select(k => k.Name)));

                if (jobs is AbstractJobManager manager)
                {
                    var access = await manager.RequestAccess().ConfigureAwait(false);
                    if (access == AccessState.Available)
                        logger.LogInformation("[Jobs] background execution available");
                    else
                        logger.LogWarning(
                            "[Jobs] background execution unavailable ({Access}) — refreshes will only run while the app is open",
                            access);
                }
            }
            catch (Exception ex)
            {
                // A job manager that won't start shouldn't take the app down with it; the
                // foreground paths (startup refresh, manual refresh) still work.
                logger.LogError(ex, "[Jobs] failed to start the job manager");
            }
        });
}
