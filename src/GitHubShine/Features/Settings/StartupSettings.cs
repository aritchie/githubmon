#if !MOBILE
using Shiny;
#endif

namespace GitHubShine.Settings;

/// <summary>
/// Whether the app launches itself when the user logs in. Thin app-side face on Shiny's
/// <c>IStartupService</c> (Windows <c>HKCU\…\Run</c>, macOS <c>SMAppService</c> login items,
/// Linux <c>~/.config/autostart</c>), which only ships on the desktop heads — the shared Blazor
/// settings page injects this on every head and asks <see cref="IsSupported"/> before rendering.
/// <para>
/// There is deliberately no stored preference behind this: the OS list IS the state, and it's a
/// list the user can edit outside the app (System Settings › Login Items, Task Manager › Startup).
/// A local copy would only ever be a second, staler answer to the same question.
/// </para>
/// </summary>
public interface IStartupSettings
{
    /// <summary>False on mobile, on macOS 12 and earlier, and for MSIX-packaged Windows builds.</summary>
    bool IsSupported { get; }

    Task<StartupState> GetAsync(CancellationToken ct = default);

    /// <summary>Adds or removes the app from the OS startup list, returning the state the OS settled on.</summary>
    Task<StartupState> SetAsync(bool runAtLogin, CancellationToken ct = default);

    /// <summary>
    /// Opens the OS screen that owns this list, for the states the app can't resolve on its own.
    /// False when there's nothing to open (Linux, mobile).
    /// </summary>
    Task<bool> OpenSystemSettingsAsync();
}

/// <summary>What the operating system says about our startup entry.</summary>
public enum StartupState
{
    /// <summary>No startup list this app can manage — mobile, or an unsupported desktop configuration.</summary>
    NotSupported,

    /// <summary>Not in the startup list.</summary>
    Off,

    /// <summary>In the list and armed.</summary>
    On,

    /// <summary>
    /// Registered, but macOS wants the user to confirm it in System Settings › General › Login Items
    /// before it will fire. The normal result of the very first registration.
    /// </summary>
    NeedsApproval,

    /// <summary>In the list but switched off outside the app (Task Manager › Startup, or <c>Hidden=true</c>).</summary>
    DisabledByUser,

    /// <summary>Blocked by device management policy — nothing the app or the user can do about it.</summary>
    DisabledByPolicy
}

#if MOBILE
/// <summary>
/// iOS and Android have no user-managed launch-at-login list, so the setting is simply absent —
/// the settings page hides the card on <see cref="IsSupported"/>.
/// </summary>
[Singleton]
public sealed class UnsupportedStartupSettings : IStartupSettings
{
    public bool IsSupported => false;
    public Task<StartupState> GetAsync(CancellationToken ct = default) => Task.FromResult(StartupState.NotSupported);
    public Task<StartupState> SetAsync(bool runAtLogin, CancellationToken ct = default) => Task.FromResult(StartupState.NotSupported);
    public Task<bool> OpenSystemSettingsAsync() => Task.FromResult(false);
}
#else
/// <summary>Windows / macOS / Linux, over Shiny.Extensions.MauiHosting's <see cref="IStartupService"/>.</summary>
[Singleton]
public sealed class DesktopStartupSettings(
    IStartupService startup,
    ILogger<DesktopStartupSettings> logger) : IStartupSettings
{
    public bool IsSupported => startup.IsSupported;

    public Task<StartupState> GetAsync(CancellationToken ct = default)
        => this.RunAsync(() => startup.GetState(ct), "read");

    public Task<StartupState> SetAsync(bool runAtLogin, CancellationToken ct = default)
        => this.RunAsync(
            () => runAtLogin ? startup.Register(ct) : startup.Unregister(ct),
            runAtLogin ? "enable" : "disable"
        );

    public async Task<bool> OpenSystemSettingsAsync()
    {
        try
        {
            return await startup.OpenSettings().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't open the OS startup settings");
            return false;
        }
    }

    // Touching the registry / SMAppService / the autostart directory can fail for reasons that
    // aren't the user's problem (a locked-down HKCU, a read-only home). Report it as "off" and log
    // it rather than throwing out of a settings toggle.
    async Task<StartupState> RunAsync(Func<Task<StartupServiceState>> operation, string what)
    {
        try
        {
            return Map(await operation().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't {What} launch at login", what);
            return StartupState.Off;
        }
    }

    static StartupState Map(StartupServiceState state) => state switch
    {
        StartupServiceState.Enabled => StartupState.On,
        StartupServiceState.NotRegistered => StartupState.Off,
        StartupServiceState.RequiresApproval => StartupState.NeedsApproval,
        StartupServiceState.DisabledByUser => StartupState.DisabledByUser,
        StartupServiceState.DisabledByPolicy => StartupState.DisabledByPolicy,
        _ => StartupState.NotSupported
    };
}
#endif
