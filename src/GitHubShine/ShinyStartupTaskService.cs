using Shiny;

namespace GitHubShine;

/// <summary>
/// Runs Shiny's <see cref="IShinyStartupTask"/>s on the labs macOS/Linux heads.
///
/// Everywhere else <c>UseShiny()</c> handles this: its ShinyMauiInitializationService calls
/// Host.Run(), which invokes Start() on every registered startup task. Shiny.Hosting.Maui has no
/// asset for these heads, so MauiProgram calls AddShinyCoreServices() directly — and that only
/// *registers* the tasks. Nothing was ever starting them.
///
/// What that cost on macOS: Shiny.Core's MacLifecycleExecutor is a startup task, and its Start()
/// is the one place that assigns <c>UNUserNotificationCenter.Current.Delegate</c>. With no
/// delegate, UserNotifications never hands a notification tap back to the running process — the
/// system falls back to its default action for the notification, which is to launch the app by
/// bundle identifier, i.e. a second copy instead of the one already running. It also meant
/// WillPresentNotification never ran, so banners were at the mercy of the system default while the
/// app was foregrounded.
///
/// Ordering matters: this must be registered BEFORE any service that depends on a started task
/// (see JobStartupService, which reports on a job manager this has already started).
///
/// IMauiInitializeService.Initialize runs while the MauiApp is being built, which on macOS happens
/// inside DidFinishLaunching — i.e. on the main thread. Start() touches NSNotificationCenter and
/// UNUserNotificationCenter, so it is called synchronously here rather than pushed to a thread pool.
/// </summary>
public sealed class ShinyStartupTaskService(
    IEnumerable<IShinyStartupTask> tasks,
    ILogger<ShinyStartupTaskService> logger) : IMauiInitializeService
{
    public void Initialize(IServiceProvider services)
    {
        foreach (var task in tasks)
        {
            try
            {
                task.Start();
                logger.LogDebug("[Shiny] started {Task}", task.GetType().Name);
            }
            catch (Exception ex)
            {
                // One bad task shouldn't cost the app the others, or startup itself.
                logger.LogError(ex, "[Shiny] startup task {Task} failed to start", task.GetType().Name);
            }
        }
    }
}
