using Microsoft.UI.Windowing;
using WinUIWindow = Microsoft.UI.Xaml.Window;

namespace GitHubShine.Platforms.Windows;

/// <summary>
/// Windows "close / minimize to tray": the window hides (the app keeps running
/// behind the tray icon) instead of exiting on close or dropping to the taskbar
/// on minimize. <see cref="ForceClose"/> lets the tray's Quit command bypass the
/// close interception so the app can actually terminate.
/// </summary>
static class TrayWindowBehavior
{
    public static bool ForceClose;

    public static void HookToTray(WinUIWindow nativeWindow)
    {
        var appWindow = nativeWindow.AppWindow;
        if (appWindow is null)
            return;

        // Closing is cancelable — swallow the close and hide instead, unless Quit
        // has asked to really exit.
        appWindow.Closing += (sender, e) =>
        {
            if (ForceClose)
                return;
            e.Cancel = true;
            sender.Hide();
        };

        // AppWindow exposes no minimize event, so react to any state change: the
        // moment the window is minimized, hide it to the tray instead.
        appWindow.Changed += (sender, _) =>
        {
            if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                sender.Hide();
        };
    }

    /// <summary>Brings a hidden/minimized window back to the foreground.</summary>
    public static void Restore(WinUIWindow nativeWindow)
    {
        var appWindow = nativeWindow.AppWindow;
        appWindow?.Show();
        if (appWindow?.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        nativeWindow.Activate();
    }
}
