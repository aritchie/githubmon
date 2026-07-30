using Shiny;
using Shiny.DocumentDb;
using Shiny.Extensions.Stores;

namespace GitHubShine.Notifications;

public interface INotificationPrefsStore
{
    /// <summary>
    /// The last loaded preferences, for callers that can't await (the tray menu builds
    /// synchronously). Returns the defaults until the first load completes — see
    /// <see cref="NotificationPrefsInitializer"/>, which primes it at startup.
    /// </summary>
    NotificationPrefs Current { get; }

    /// <summary>Raised after <see cref="SaveAsync"/> — the settings page and tray menu re-read.</summary>
    event EventHandler? Changed;

    /// <summary>The saved preferences, or the defaults when nothing has been saved yet. Cached after the first read.</summary>
    Task<NotificationPrefs> GetAsync(CancellationToken ct = default);

    Task SaveAsync(NotificationPrefs prefs, CancellationToken ct = default);

    /// <summary>Shorthand for "should this category post right now" — false when muted or switched off.</summary>
    Task<bool> IsEnabledAsync(NotificationCategory category, CancellationToken ct = default);
}

[Singleton]
public sealed class NotificationPrefsStore(
    IDocumentStore store,
    [FromKeyedServices(StoreKeys.Default)] IKeyValueStore legacyPrefs) : INotificationPrefsStore
{
    // Mute used to live here, which is exactly the problem this type fixes: the desktop
    // key/value store is in-memory, so the setting was lost on every restart.
    const string LegacyMutedKey = "muted";

    readonly SemaphoreSlim gate = new(1, 1);
    volatile bool loaded;

    public NotificationPrefs Current { get; private set; } = NotificationPrefs.Default;
    public event EventHandler? Changed;

    public async Task<NotificationPrefs> GetAsync(CancellationToken ct = default)
    {
        if (this.loaded)
            return this.Current;

        await this.gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this.loaded)
                return this.Current;

            var doc = await store
                .Get<NotificationPrefs>(NotificationPrefs.DefaultId, cancellationToken: ct)
                .ConfigureAwait(false);

            if (doc is null)
            {
                // One-time migration of the legacy mute flag, then write the row so the
                // categories start from a persisted baseline rather than re-migrating.
                doc = NotificationPrefs.Default with { Muted = legacyPrefs.Get(LegacyMutedKey, false) };
                await store.Upsert(doc, cancellationToken: ct).ConfigureAwait(false);
                legacyPrefs.Remove(LegacyMutedKey);
            }

            this.Current = doc;
            this.loaded = true;
            return doc;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task SaveAsync(NotificationPrefs prefs, CancellationToken ct = default)
    {
        // Single-row table: ignore whatever id the caller built the record with.
        var toSave = prefs with { Id = NotificationPrefs.DefaultId };
        await store.Upsert(toSave, cancellationToken: ct).ConfigureAwait(false);
        this.Current = toSave;
        this.loaded = true;
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> IsEnabledAsync(NotificationCategory category, CancellationToken ct = default)
        => (await this.GetAsync(ct).ConfigureAwait(false)).IsEnabled(category);
}

/// <summary>
/// Loads the preferences once at startup so the synchronous readers (the tray menu) don't
/// render the defaults on the user's first right-click.
/// </summary>
public sealed class NotificationPrefsInitializer(
    INotificationPrefsStore prefs,
    ILogger<NotificationPrefsInitializer> logger) : IMauiInitializeService
{
    public void Initialize(IServiceProvider services)
        => _ = Task.Run(async () =>
        {
            try
            {
                await prefs.GetAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Non-fatal: GetAsync retries on the next read, and until then the
                // defaults (everything on, nothing muted) apply.
                logger.LogWarning(ex, "Couldn't preload notification preferences");
            }
        });
}
