using Shiny.Notifications;

namespace GitHubShine.Notifications;

/// <summary>
/// What happens when the user taps one of our notifications: surface the window we already have.
///
/// This is the second half of the macOS "tapping a notification opens another instance" fix — see
/// <see cref="ShinyStartupTaskService"/> for the first (without it Shiny's UNUserNotificationCenter
/// delegate is never assigned and this is never called at all). The app can legitimately be sitting
/// in the menu bar as an accessory with no window and no Dock icon at that point;
/// <see cref="MainWindowLauncher.ShowOrCreate"/> is what rejoins the Dock, recreates the window if
/// the user closed it, and pulls the process to the front.
///
/// Deliberately no per-notification routing: the events that raise alerts span builds, inbox, PRs,
/// issues, stars and forks (see NotificationDispatchHandler) and there is no per-event
/// destination to navigate to yet.
/// </summary>
public sealed class NotificationEntryDelegate(ILogger<NotificationEntryDelegate> logger) : INotificationDelegate
{
    public Task OnEntry(NotificationResponse response)
    {
        logger.LogDebug("[Notifications] entry from '{Title}'", response.Notification?.Title);
        MainWindowLauncher.ShowOrCreate();
        return Task.CompletedTask;
    }
}
