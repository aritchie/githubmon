using GitHubShine.Components;
#if MACOS
using Microsoft.Maui.Platforms.MacOS.Controls;
#else
using Microsoft.AspNetCore.Components.WebView.Maui;
#endif

namespace GitHubShine;

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
        // WebViewContentFixer undoes the zero-size web content view that blanks the window.
        Loaded += (_, _) =>
        {
            GitHubShine.Platforms.MacOS.WebViewContentFixer.Apply();
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300), () =>
            {
                GitHubShine.Platforms.MacOS.TitlebarOverlayFixer.Apply();
                GitHubShine.Platforms.MacOS.WebViewContentFixer.Apply();
            });
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(2), () =>
            {
                GitHubShine.Platforms.MacOS.TitlebarOverlayFixer.Apply();
                GitHubShine.Platforms.MacOS.WebViewContentFixer.Apply();
            });
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
