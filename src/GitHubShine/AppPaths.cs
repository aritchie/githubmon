namespace GitHubShine;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitHubShine");

    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "githubshine.db");
}
