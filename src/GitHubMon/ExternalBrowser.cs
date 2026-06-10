using System.Diagnostics;
using Shiny;

namespace GitHubMon;

public interface IExternalBrowser
{
    void Open(string url);
}

// MAUI Essentials Browser/Launcher throw "Not supported in portable version" on
// the maui-labs macOS/Linux backends, so shell out instead: UseShellExecute
// routes to `open` on macOS, `xdg-open` on Linux, and ShellExecute on Windows.
[Singleton]
public sealed class ExternalBrowser(ILogger<ExternalBrowser> logger) : IExternalBrowser
{
    public void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to open {Url}", url);
        }
    }
}
