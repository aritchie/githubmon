using Shiny;
using Shiny.Jobs;

namespace GitHubShine.Jobs;

/// <summary>
/// Brings the job manager to life at startup, and reports whether the OS will actually run it.
///
/// Resolving the manager is not optional plumbing — without it nothing runs. <c>AddJob</c>
/// registers <see cref="IJobManager"/> as a singleton, and DI is lazy: if no one ever resolves it,
/// it is never constructed, so its timer never starts and its platform schedulers are never
/// registered. Found by running the desktop head and noticing the jobs never logged: the only
/// thing touching the poll state was the config-change handler.
///
/// Starting it is <see cref="ShinyStartupTaskService"/>'s job, not this one's — JobManager is an
/// <c>IShinyStartupTask</c> like any other, and this used to reach in and Start() only that one by
/// hand, leaving Shiny's own platform lifecycle task unstarted on the labs heads.
///
/// <see cref="AbstractJobManager.RequestAccess"/> is what reports whether background execution is
/// available — on iOS it answers <c>NotSetup</c> when the BGTaskScheduler declaration in
/// Info.plist is missing, which is worth logging loudly because the symptom otherwise is simply
/// "notifications stopped when the app was closed". It only reads state, so it is safe to call
/// from anywhere at any time.
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
