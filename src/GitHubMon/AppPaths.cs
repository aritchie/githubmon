namespace GitHubMon;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitHubMon");

    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "githubmon.db");
}
