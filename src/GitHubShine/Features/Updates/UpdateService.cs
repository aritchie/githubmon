using System.Reflection;
using Shiny;
using Shiny.DocumentDb;

namespace GitHubShine.Updates;

public enum UpdateCheckState
{
    /// <summary>No check has run yet this session.</summary>
    Idle,
    Checking,
    UpToDate,
    Available,
    Failed
}

public interface IUpdateService
{
    /// <summary>
    /// False on mobile, where updates come from the app stores and a side-loaded package could
    /// never be installed anyway.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>The running app's version, normalised like release versions.</summary>
    Version CurrentVersion { get; }

    UpdateCheckState State { get; }

    /// <summary>The newer release, when <see cref="State"/> is <see cref="UpdateCheckState.Available"/>.</summary>
    AvailableUpdate? Available { get; }

    /// <summary>Why the last check failed, for the settings page.</summary>
    string? Error { get; }

    DateTimeOffset? LastCheckedUtc { get; }

    bool IsInstalling { get; }

    /// <summary>What the installer is doing right now — "Downloading… 42%".</summary>
    string? InstallStatus { get; }

    /// <summary>True when <see cref="InstallAsync"/> replaces the app in place; false when it only hands over the download.</summary>
    bool CanInstallInPlace { get; }

    /// <summary>Raised whenever any of the above changes. May fire off the UI thread.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Asks the release feed for something newer. Without <paramref name="force"/> a result less
    /// than an hour old is reused, so re-opening the settings page doesn't spend the anonymous
    /// API budget.
    /// </summary>
    Task CheckAsync(bool force = false, CancellationToken ct = default);

    /// <summary>
    /// True once per session, when the startup prompt should open: an update is available and the
    /// user hasn't asked to stop hearing about this version. Calling it consumes the prompt.
    /// </summary>
    Task<bool> TryClaimStartupPromptAsync(CancellationToken ct = default);

    Task<bool> IsSkippedAsync(AvailableUpdate update, CancellationToken ct = default);

    /// <summary>"Don't show again" — stops the startup prompt for this version only.</summary>
    Task SetSkippedAsync(AvailableUpdate update, bool skipped, CancellationToken ct = default);

    /// <summary>
    /// Installs <see cref="Available"/>. On success in place this restarts the app and does not
    /// return; otherwise the result says what happened instead.
    /// </summary>
    Task<UpdateInstallResult> InstallAsync(CancellationToken ct = default);
}

[Singleton]
public sealed class UpdateService(
    IDocumentStore store,
    IUpdateInstaller installer,
    ILogger<UpdateService> logger) : IUpdateService
{
    static readonly TimeSpan freshFor = TimeSpan.FromHours(1);

    readonly SemaphoreSlim checkGate = new(1, 1);
    bool promptClaimed;

#if MOBILE
    public bool IsSupported => false;
#else
    public bool IsSupported => true;
#endif

    public Version CurrentVersion { get; } = ReadCurrentVersion();
    public UpdateCheckState State { get; private set; }
    public AvailableUpdate? Available { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastCheckedUtc { get; private set; }
    public bool IsInstalling { get; private set; }
    public string? InstallStatus { get; private set; }
    public bool CanInstallInPlace => installer.CanInstallInPlace;
    public event EventHandler? Changed;

    public async Task CheckAsync(bool force = false, CancellationToken ct = default)
    {
        if (!this.IsSupported)
            return;

        await this.checkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && this.LastCheckedUtc is { } last && DateTimeOffset.UtcNow - last < freshFor)
                return;

            this.State = UpdateCheckState.Checking;
            this.Error = null;
            this.RaiseChanged();

            try
            {
                var newest = await ReleaseFeed.GetNewestAsync(ct).ConfigureAwait(false);
                if (newest is not null && newest.Version > this.CurrentVersion)
                {
                    this.Available = newest;
                    this.State = UpdateCheckState.Available;
                    logger.LogInformation("[Update] {Tag} is available (running {Current})", newest.Tag, this.CurrentVersion);
                }
                else
                {
                    this.Available = null;
                    this.State = UpdateCheckState.UpToDate;
                }
                this.LastCheckedUtc = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                this.State = this.Available is null ? UpdateCheckState.Idle : UpdateCheckState.Available;
                throw;
            }
            catch (Exception ex)
            {
                // Keep whatever was found earlier — a flaky network shouldn't make a known update vanish.
                logger.LogWarning(ex, "[Update] release check failed");
                this.Error = ex.Message;
                this.State = this.Available is null ? UpdateCheckState.Failed : UpdateCheckState.Available;
            }
            this.RaiseChanged();
        }
        finally
        {
            this.checkGate.Release();
        }
    }

    public async Task<bool> TryClaimStartupPromptAsync(CancellationToken ct = default)
    {
        if (this.promptClaimed || this.Available is not { } update)
            return false;

        if (await this.IsSkippedAsync(update, ct).ConfigureAwait(false))
            return false;

        // Re-checked after the await: the layout can ask twice in quick succession (first render
        // and the Changed that the check itself raised).
        if (this.promptClaimed)
            return false;

        this.promptClaimed = true;
        return true;
    }

    public async Task<bool> IsSkippedAsync(AvailableUpdate update, CancellationToken ct = default)
    {
        var prefs = await this.GetPrefsAsync(ct).ConfigureAwait(false);
        return prefs.SkippedVersion == update.Version.ToString();
    }

    public async Task SetSkippedAsync(AvailableUpdate update, bool skipped, CancellationToken ct = default)
    {
        var prefs = await this.GetPrefsAsync(ct).ConfigureAwait(false);
        await store
            .Upsert(prefs with { Id = UpdatePrefs.DefaultId, SkippedVersion = skipped ? update.Version.ToString() : null }, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    public async Task<UpdateInstallResult> InstallAsync(CancellationToken ct = default)
    {
        if (this.Available is not { } update || this.IsInstalling)
            return UpdateInstallResult.Nothing;

        this.IsInstalling = true;
        this.InstallStatus = "Starting…";
        this.RaiseChanged();
        try
        {
            var progress = new Progress<string>(status =>
            {
                this.InstallStatus = status;
                this.RaiseChanged();
            });
            return await installer.InstallAsync(update, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[Update] installing {Tag} failed", update.Tag);
            return UpdateInstallResult.Failed(ex.Message);
        }
        finally
        {
            this.IsInstalling = false;
            this.InstallStatus = null;
            this.RaiseChanged();
        }
    }

    async Task<UpdatePrefs> GetPrefsAsync(CancellationToken ct)
        => await store.Get<UpdatePrefs>(UpdatePrefs.DefaultId, cancellationToken: ct).ConfigureAwait(false)
           ?? UpdatePrefs.Default;

    void RaiseChanged() => this.Changed?.Invoke(this, EventArgs.Empty);

    static Version ReadCurrentVersion()
    {
        // AppInfo reads the platform's own idea of the version (CFBundleShortVersionString on
        // macOS — what the release workflow stamps). The assembly version is the fallback for a
        // head where Essentials can't answer.
        string? raw = null;
        try
        {
            raw = AppInfo.Current.VersionString;
        }
        catch { }

        if (String.IsNullOrWhiteSpace(raw))
        {
            raw = typeof(UpdateService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
        }
        return ReleaseFeed.ParseVersion(raw) ?? new Version(0, 0, 0);
    }
}

/// <summary>
/// Kicks off the release check shortly after launch. The prompt itself is the layout's job — it
/// owns navigation — and it asks <see cref="IUpdateService.TryClaimStartupPromptAsync"/> once this
/// raises Changed.
/// </summary>
public sealed class UpdateStartupService(
    IUpdateService updates,
    ILogger<UpdateStartupService> logger) : IMauiInitializeService
{
    public void Initialize(IServiceProvider services)
    {
        if (!updates.IsSupported)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Let the window and the dashboard's own startup traffic go first.
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                await updates.CheckAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Update] startup check failed");
            }
        });
    }
}
