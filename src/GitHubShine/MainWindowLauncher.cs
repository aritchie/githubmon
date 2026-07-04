namespace GitHubShine;

/// <summary>
/// Shows the main window, recreating it if the user closed it. Used by the tray
/// menu and (on macOS) the dock-icon reopen handler — the app keeps running in
/// the tray after the last window closes.
/// </summary>
public static class MainWindowLauncher
{
    public static void ShowOrCreate()
    {
        var app = Application.Current;
        if (app is null)
            return;

        app.Dispatcher.Dispatch(() =>
        {
#if MACOS
            // We may be running as a tray-only accessory app — rejoin the Dock
            // before showing UI.
            AppKit.NSApplication.SharedApplication.ActivationPolicy = AppKit.NSApplicationActivationPolicy.Regular;
#endif
            var window = app.Windows.FirstOrDefault();
            if (window is null)
            {
                app.OpenWindow(new Window(new MainPage())
                {
                    Title = "GitHub Shine",
                    Width = 1100,
                    Height = 760
                });
            }
#if WINDOWS
            // The window is hidden (not destroyed) on close/minimize-to-tray, so
            // un-hide it directly — ActivateWindow alone won't reveal a hidden AppWindow.
            else if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
            {
                Platforms.Windows.TrayWindowBehavior.Restore(native);
            }
#endif
            else
            {
                app.ActivateWindow(window);
            }

#if MACOS
            // Bring the app forward — with no key window, ActivateWindow alone
            // doesn't necessarily un-background the process.
            AppKit.NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
#endif
        });
    }
}
