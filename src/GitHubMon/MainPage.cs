using GitHubMon.Components;
#if MACOS
using Microsoft.Maui.Platform.MacOS.Controls;
#else
using Microsoft.AspNetCore.Components.WebView.Maui;
#endif

namespace GitHubMon;

/// <summary>
/// Hosts the entire Blazor UI. The maui-labs AppKit backend ships its own
/// BlazorWebView control (MacOSBlazorWebView); Windows and the labs GTK4 backend
/// use the standard Microsoft.AspNetCore.Components.WebView.Maui control.
/// </summary>
public class MainPage : ContentPage
{
    public MainPage()
    {
#if MACOS
        var webView = new MacOSBlazorWebView { HostPage = "wwwroot/index.html" };
        webView.RootComponents.Add(new BlazorRootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Routes)
        });

        // The labs handler installs its titlebar drag overlay once the webview
        // lands in a window; patch it shortly after so the window buttons work.
        // Second pass covers slow window setup. Idempotent either way.
        Loaded += (_, _) =>
        {
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300), Platforms.MacOS.TitlebarOverlayFixer.Apply);
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(2), Platforms.MacOS.TitlebarOverlayFixer.Apply);
        };
#else
        var webView = new BlazorWebView { HostPage = "wwwroot/index.html" };
        webView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Routes)
        });
#endif
        Content = webView;
    }
}
