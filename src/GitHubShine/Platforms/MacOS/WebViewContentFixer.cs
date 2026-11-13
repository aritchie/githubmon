using AppKit;
using Foundation;
using WebKit;

namespace GitHubShine.Platforms.MacOS;

/// <summary>
/// Since maui-labs preview.14 the WKWebView is grown from 0x0 to its window size in a way that
/// skips autoresizing, and AppKit then freezes WebKit's internal WKFlippedView at 0x0 through an
/// autoresizing-mask constraint (e.g. <c>H:[WKFlippedView]-(1100)-|</c>). That view's layer clips
/// the whole rendered page, so the window shows only the webview's under-page colour: a black
/// screen while Blazor runs fine underneath. Snap the content view back to the webview's bounds,
/// and again whenever the webview's frame changes. Idempotent.
/// </summary>
public static class WebViewContentFixer
{
    static readonly HashSet<nint> Observed = new();

    public static void Apply()
    {
        foreach (var window in NSApplication.SharedApplication.DangerousWindows)
        {
            if (window.ContentView?.Superview is { } frame)
                Find(frame);
        }
    }

    static void Find(NSView view)
    {
        if (view is WKWebView webView)
        {
            Fix(webView);
            if (Observed.Add(webView.Handle))
            {
                webView.PostsFrameChangedNotifications = true;
                NSNotificationCenter.DefaultCenter.AddObserver(
                    NSView.FrameChangedNotification,
                    _ => Fix(webView),
                    webView
                );
            }
            return;
        }

        foreach (var sub in view.Subviews)
            Find(sub);
    }

    static void Fix(WKWebView webView)
    {
        foreach (var sub in webView.Subviews)
        {
            if (sub.Class?.Name == "WKFlippedView" && sub.Frame != webView.Bounds)
            {
                sub.Frame = webView.Bounds;
                Console.WriteLine("[WebViewContentFixer] WKFlippedView resized to {0}", webView.Bounds.Size);
            }
        }
    }
}
