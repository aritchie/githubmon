namespace GitHubShine.Updates;

/// <summary>
/// What the user said about update prompts. Lives in the document store for the same reason as
/// <see cref="Sync.AutoSyncPrefs"/>: the desktop key/value store is in-memory, so a "don't show
/// again" written there would be forgotten on the next launch — exactly when it's needed.
/// Single row, fixed id.
/// </summary>
/// <param name="SkippedVersion">
/// The release the startup prompt was told to stop showing for, as its normalised version string.
/// Per-version on purpose: a later release prompts again, rather than one tick silencing every
/// future update. The settings page shows a pending update regardless.
/// </param>
public sealed record UpdatePrefs(
    string Id,
    string? SkippedVersion = null)
{
    public const string DefaultId = "default";

    public static UpdatePrefs Default => new(DefaultId);
}
