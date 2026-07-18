#if MOBILE
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
#endif

namespace GitHubShine.Settings;

public interface IFileDialogs
{
    /// <summary>Prompts for a save location. Returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickSavePathAsync(string suggestedFileName);

    /// <summary>Prompts for an existing file. Returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickOpenPathAsync();

    /// <summary>Prompts for a destination folder. Returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickFolderAsync();

    /// <summary>
    /// Delivers a file that was just written (to a path from <see cref="PickSavePathAsync"/>) to the user.
    /// Mobile has no "save to location" dialog, so it writes to a private temp path and then hands the
    /// file to the OS share sheet ("Save to Files", Drive, AirDrop, …), returning true. Desktop platforms
    /// already wrote to the user's chosen location, so this is a no-op returning false.
    /// </summary>
    Task<bool> PresentSavedFileAsync(string path, string title) => Task.FromResult(false);
}

#if MOBILE
/// <summary>
/// iOS/Android file dialogs. Open uses the native document picker (FilePicker); save writes to a
/// private cache path that is then handed to the share sheet via <see cref="PresentSavedFileAsync"/>.
/// </summary>
public sealed class MobileFileDialogs : IFileDialogs
{
    public Task<string?> PickSavePathAsync(string suggestedFileName)
        // No native "save as" dialog on mobile — stage in the cache, then share (PresentSavedFileAsync).
        => Task.FromResult<string?>(Path.Combine(FileSystem.Current.CacheDirectory, suggestedFileName));

    public async Task<string?> PickOpenPathAsync()
    {
        // FilePicker copies the chosen file into the app sandbox and FullPath points at that
        // local copy — directly readable by the SQLite ATTACH in RestoreFromAsync.
        var result = await FilePicker.Default.PickAsync();
        return result?.FullPath;
    }

    // Folder picking isn't part of the mobile backup/restore flow.
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

    public async Task<bool> PresentSavedFileAsync(string path, string title)
    {
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = title,
            File = new ShareFile(path)
        });
        return true;
    }
}
#endif

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
