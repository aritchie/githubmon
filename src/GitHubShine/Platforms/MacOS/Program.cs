using AppKit;
using Foundation;
using Microsoft.Maui.Platform.MacOS.Hosting;

namespace GitHubShine;

[Register("Program")]
public class Program : MacOSMauiApplication
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override void DidFinishLaunching(NSNotification notification)
	{
		base.DidFinishLaunching(notification);

		// When the last real window closes, drop out of the Dock and live only in
		// the menu-bar tray (accessory apps have no Dock icon and no Cmd+Tab entry).
		// MainWindowLauncher flips the policy back to Regular when reopening.
		NSWindow.Notifications.ObserveWillClose((_, args) =>
		{
			var closingHandle = (args.Notification.Object as NSWindow)?.Handle ?? ObjCRuntime.NativeHandle.Zero;
			// Defer a tick so visibility reflects the post-close state.
			CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(() =>
			{
				var anyRealWindow = false;
				foreach (NSWindow w in NSApplication.SharedApplication.DangerousWindows)
				{
					if (w.Handle != closingHandle && w.IsVisible && w.CanBecomeMainWindow)
					{
						anyRealWindow = true;
						break;
					}
				}
				if (!anyRealWindow)
					NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Accessory;
			});
		});
	}

	// Closing the main window leaves the app alive in the tray/dock; the tray's
	// Quit item (or Cmd+Q) is the real exit.
	[Export("applicationShouldTerminateAfterLastWindowClosed:")]
	public bool ShouldTerminateAfterLastWindowClosed(NSApplication sender) => false;

	// Dock icon clicked with no visible windows — bring the dashboard back.
	[Export("applicationShouldHandleReopen:hasVisibleWindows:")]
	public bool ShouldHandleReopen(NSApplication sender, bool hasVisibleWindows)
	{
		if (!hasVisibleWindows)
			MainWindowLauncher.ShowOrCreate();
		return true;
	}

	public static void Main(string[] args)
	{
		NSApplication.Init();
		NSApplication.SharedApplication.Delegate = new Program();
		NSApplication.Main(args);
	}
}
