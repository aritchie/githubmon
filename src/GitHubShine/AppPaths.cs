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
}
