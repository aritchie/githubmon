using Shiny;
using Shiny.Notifications;

namespace GitHubShine.Notifications;

public interface INotificationHub
{
    /// <summary>The last known permission state, or null until it has been checked once.</summary>
    AccessState? Current { get; }

    /// <summary>False when the platform can't provide notifications at all (missing Shiny platform binding).</summary>
    bool IsSupported { get; }

    /// <summary>Reads the current permission state without prompting.</summary>
    Task<AccessState> CheckAsync();

    /// <summary>Requests permission, prompting at most once per process. Cheap to call before every send.</summary>
    Task<AccessState> EnsureAsync(CancellationToken ct = default);

    /// <summary>Forces a fresh request, ignoring the cached result — used by the Settings button.</summary>
    Task<AccessState> RequestAsync();

    /// <summary>Sends after ensuring permission. Returns false (and logs) rather than throwing.</summary>
    Task<bool> SendAsync(Notification notification);
}

/// <summary>
/// The single place the app touches Shiny notifications.
///
/// Two things were wrong before, and this type fixes both:
///
/// 1. <b>Nothing registered <see cref="IPlatform"/>.</b> Shiny's NotificationManager takes an
///    IPlatform, normally supplied by Shiny.Hosting.Maui's UseShiny(). That package ships no
///    net10.0-macos asset, so on this app's macOS head it would resolve its platform-less net10.0
///    build and register nothing at all. MauiProgram now registers the per-head platform type
///    directly. Without it, resolving INotificationManager threw
///    "Unable to resolve service for type 'Shiny.IPlatform'".
///
/// 2. <b>Nothing requested authorization.</b> Every Apple head routes through
///    UNUserNotificationCenter, which silently drops notifications until RequestAuthorization has
///    been granted — no error, nothing shown.
///
/// INotificationManager is resolved lazily rather than injected, because a DI failure while
/// constructing it used to take down the app's entire service initialisation at startup.
/// </summary>
[Singleton]
public sealed class NotificationHub(IServiceProvider services, ILogger<NotificationHub> logger) : INotificationHub
{
    readonly SemaphoreSlim gate = new(1, 1);
    AccessState? cached;
    INotificationManager? manager;
    bool resolved;

    public AccessState? Current => this.cached;

    public bool IsSupported => this.Resolve() is not null;

    INotificationManager? Resolve()
    {
        if (this.resolved)
            return this.manager;

        this.resolved = true;
        try
        {
            this.manager = services.GetService<INotificationManager>();
            if (this.manager is null)
                logger.LogWarning("[Notifications] no INotificationManager is registered on this platform");
        }
        catch (Exception ex)
        {
            // Missing IPlatform or any other unmet dependency — degrade instead of crashing.
            logger.LogError(ex, "[Notifications] the notification manager could not be created");
            this.manager = null;
        }
        return this.manager;
    }

    public async Task<AccessState> CheckAsync()
    {
        if (this.Resolve() is not { } mgr)
            return AccessState.NotSupported;

        try
        {
            var state = await mgr.GetCurrentAccess(AccessRequestFlags.Notification).ConfigureAwait(false);
            this.cached = state;
            return state;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Notifications] couldn't read the permission state");
            return AccessState.Unknown;
        }
    }

    public async Task<AccessState> EnsureAsync(CancellationToken ct = default)
    {
        if (this.cached == AccessState.Available)
            return AccessState.Available;

        await this.gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return this.cached is { } already ? already : await this.RequestCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<AccessState> RequestAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            this.cached = null;
            return await this.RequestCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    async Task<AccessState> RequestCoreAsync()
    {
        if (this.Resolve() is not { } mgr)
        {
            this.cached = AccessState.NotSupported;
            return AccessState.NotSupported;
        }

        try
        {
            var state = await mgr.RequestAccess(AccessRequestFlags.Notification).ConfigureAwait(false);
            this.cached = state;

            if (state == AccessState.Available)
                logger.LogInformation("[Notifications] permission granted");
            else
                logger.LogWarning("[Notifications] permission is {State} — alerts will not be shown", state);

            return state;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Notifications] requesting permission failed");
            this.cached = AccessState.Unknown;
            return AccessState.Unknown;
        }
    }

    public async Task<bool> SendAsync(Notification notification)
    {
        if (this.Resolve() is not { } mgr)
            return false;

        try
        {
            var state = await this.EnsureAsync().ConfigureAwait(false);
            if (state != AccessState.Available)
            {
                logger.LogDebug("[Notifications] skipping '{Title}' — permission is {State}", notification.Title, state);
                return false;
            }

            await mgr.Send(notification).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Notifications] failed to send '{Title}'", notification.Title);
            return false;
        }
    }
}

/// <summary>
/// Asks for notification permission at startup so the OS prompt appears once, in context, rather
/// than the first time a background poll happens to find a failed build.
/// </summary>
public sealed class NotificationAccessInitializer(INotificationHub hub) : IMauiInitializeService
{
    public void Initialize(IServiceProvider services)
        => _ = Task.Run(() => hub.EnsureAsync());
}
