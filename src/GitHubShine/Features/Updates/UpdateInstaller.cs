namespace GitHubShine.Updates;

public interface IUpdateInstaller
{
    /// <summary>True when <see cref="InstallAsync"/> replaces the running app rather than handing the download to the user.</summary>
    bool CanInstallInPlace { get; }

    Task<UpdateInstallResult> InstallAsync(AvailableUpdate update, IProgress<string> progress, CancellationToken ct);
}

public enum UpdateInstallOutcome
{
    Nothing,

    /// <summary>The new version is in place and the app is restarting.</summary>
    Restarting,

    /// <summary>The download (or release page) was opened for the user to finish by hand.</summary>
    HandedOff,

    Failed
}

public sealed record UpdateInstallResult(UpdateInstallOutcome Outcome, string? Message = null)
{
    public static UpdateInstallResult Nothing { get; } = new(UpdateInstallOutcome.Nothing);
    public static UpdateInstallResult Restarting { get; } = new(UpdateInstallOutcome.Restarting);
    public static UpdateInstallResult HandedOff(string message) => new(UpdateInstallOutcome.HandedOff, message);
    public static UpdateInstallResult Failed(string message) => new(UpdateInstallOutcome.Failed, message);
}

/// <summary>
/// Every head without an in-place installer: opens the release page, where the user picks the
/// download. Windows and Linux land here — the release workflow only publishes a macOS package —
/// and so does the mobile head, though the UI never offers it there.
/// </summary>
public sealed class BrowserUpdateInstaller(IBrowser browser) : IUpdateInstaller
{
    public bool CanInstallInPlace => false;

    public async Task<UpdateInstallResult> InstallAsync(AvailableUpdate update, IProgress<string> progress, CancellationToken ct)
    {
        await browser.OpenAsync(update.PageUrl, BrowserLaunchMode.External).ConfigureAwait(false);
        return UpdateInstallResult.HandedOff("Opened the release page in your browser");
    }
}
