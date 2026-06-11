namespace GitHubShine.Settings;

public interface IFileDialogs
{
    /// <summary>Prompts for a save location. Returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickSavePathAsync(string suggestedFileName);

    /// <summary>Prompts for an existing file. Returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickOpenPathAsync();

    /// <summary>Prompts for a destination folder. Returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickFolderAsync();
}

/// <summary>
/// No-dialog fallback for backends without a native panel implementation
/// (labs GTK4): save goes to ~/Downloads, open picks the newest
/// githubshine-backup* file from ~/Downloads.
/// </summary>
public sealed class DownloadsFolderFileDialogs : IFileDialogs
{
    static string Downloads => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public Task<string?> PickSavePathAsync(string suggestedFileName)
    {
        Directory.CreateDirectory(Downloads);
        return Task.FromResult<string?>(Path.Combine(Downloads, suggestedFileName));
    }

    public Task<string?> PickOpenPathAsync()
    {
        if (!Directory.Exists(Downloads))
            return Task.FromResult<string?>(null);
        var newest = Directory.GetFiles(Downloads, "githubshine-backup*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return Task.FromResult(newest);
    }

    public Task<string?> PickFolderAsync()
    {
        Directory.CreateDirectory(Downloads);
        return Task.FromResult<string?>(Downloads);
    }
}
