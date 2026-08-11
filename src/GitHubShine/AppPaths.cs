namespace GitHubShine;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitHubShine");

    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "githubshine.db");

    /// <summary>
    /// Bare git repos the sync feature keeps as a local staging area between source and target.
    /// Disposable — deleting it only costs the next sync a full re-fetch.
    /// </summary>
    public static string SyncCacheDirectory { get; } = Path.Combine(DataDirectory, "sync-cache");

    /// <summary>
    /// Where the clone feature puts working copies on mobile. Phones have no folder picker worth
    /// offering (and nowhere outside the sandbox to write anyway), so instead of asking, iOS and
    /// Android always clone here; desktop keeps the folder the user chose in <c>ClonePrefs</c>.
    /// </summary>
    public static string CloneDirectory { get; } = Path.Combine(DataDirectory, "clones");
}
