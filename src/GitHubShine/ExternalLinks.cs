using Microsoft.JSInterop;

namespace GitHubShine;

/// <summary>
/// Target of the click hook in wwwroot/index.html. Links inside HTML the app renders but didn't
/// write — release notes — point off-site, and following one would navigate the WebView away from
/// the app with no way back. They open in the system browser instead.
/// </summary>
public static class ExternalLinks
{
    [JSInvokable]
    public static Task OpenExternalLink(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? Browser.Default.OpenAsync(uri, BrowserLaunchMode.External)
            : Task.CompletedTask;
}
