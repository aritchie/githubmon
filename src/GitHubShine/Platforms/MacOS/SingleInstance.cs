using AppKit;
using Foundation;

namespace GitHubShine.Platforms.MacOS;

/// <summary>
/// Keeps the macOS head to a single running copy.
///
/// Tapping a notification is supposed to reach the running process through Shiny's
/// UNUserNotificationCenter delegate (see ShinyStartupTaskService / NotificationEntryDelegate), and
/// when it does, that works. But macOS has more than one way to decide the tap means "launch the
/// app" instead: LaunchServices resolves the bundle identifier, and every build of this project
/// registers another bundle with the same identifier (bin/Debug, bin/Release, the per-RID
/// intermediates, the copy on the Desktop...). Whenever the delegate path doesn't win, the result
/// is a second, fully independent copy of the app polling the same database. That has now
/// regressed more than once, so rather than rely on the delegate alone, a second launch — from a
/// notification, Finder, the Dock or `open -n` — hands off to the copy that's already running and
/// exits before NSApplication.Main.
///
/// The hand-off is a distributed notification rather than just activating the other process: the
/// running copy may be a tray-only accessory app with no window to activate, so it has to recreate
/// its window itself (MainWindowLauncher.ShowOrCreate). deliverImmediately matters — AppKit
/// suspends distributed notification delivery to inactive apps, which a tray app nearly always is.
///
/// Dev-loop note: with the deployed copy running, launching a Debug build now surfaces the
/// deployed copy and exits (logging why to stdout) — quit it first.
/// </summary>
static class SingleInstance
{
    static NSObject? showObserver;

    /// <summary>
    /// Called from Main before NSApplication.Main. Returns true when another copy is running and
    /// has been asked to show itself, in which case this process should exit.
    /// </summary>
    public static bool HandOffToRunningInstance()
    {
        var bundleId = NSBundle.MainBundle.BundleIdentifier;
        if (String.IsNullOrWhiteSpace(bundleId))
            return false;

        var selfPid = NSRunningApplication.CurrentApplication.ProcessIdentifier;
        foreach (var app in NSRunningApplication.GetRunningApplications(bundleId))
        {
            // Only defer to a copy that has finished launching: two copies started at the same
            // moment would otherwise each see the other and both exit.
            if (app.ProcessIdentifier == selfPid || app.Terminated || !app.FinishedLaunching)
                continue;

            Console.WriteLine($"[SingleInstance] {bundleId} is already running (pid {app.ProcessIdentifier}); asking it to show and exiting");
            NSDistributedNotificationCenter.DefaultCenter.PostNotificationName(GetShowRequestName(bundleId), null, null, true);
            app.Activate(NSApplicationActivationOptions.ActivateAllWindows);
            return true;
        }
        return false;
    }

    /// <summary>Called once the app has launched, so later launches can hand off to it.</summary>
    public static void ListenForHandOff()
    {
        var bundleId = NSBundle.MainBundle.BundleIdentifier;
        if (String.IsNullOrWhiteSpace(bundleId) || showObserver != null)
            return;

        showObserver = NSDistributedNotificationCenter.DefaultCenter.AddObserver(
            new NSString(GetShowRequestName(bundleId)),
            _ => MainWindowLauncher.ShowOrCreate()
        );
    }

    static string GetShowRequestName(string bundleId) => bundleId + ".show-requested";
}
