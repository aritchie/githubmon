using AppKit;
using GitHubShine.Settings;

namespace GitHubShine.Platforms.MacOS;

/// <summary>Native AppKit save/open panels. Panel APIs must run on the main thread.</summary>
public sealed class MacFileDialogs : IFileDialogs
{
    public Task<string?> PickSavePathAsync(string suggestedFileName)
        => OnMainThread(() =>
        {
            // NSSavePanel runs out-of-process on macOS 10.15+; use the SavePanel
            // factory method instead of the deprecated public constructor.
            var panel = NSSavePanel.SavePanel;
            panel.Title = "Back up GitHub Shine";
            panel.NameFieldStringValue = suggestedFileName;
            panel.CanCreateDirectories = true;
            return panel.RunModal() == 1 ? panel.Url?.Path : null;
        });

    public Task<string?> PickOpenPathAsync()
        => OnMainThread(() =>
        {
            var panel = NSOpenPanel.OpenPanel;
            panel.Title = "Restore GitHub Shine backup";
            panel.CanChooseFiles = true;
            panel.CanChooseDirectories = false;
            panel.AllowsMultipleSelection = false;
            return panel.RunModal() == 1 ? panel.Url?.Path : null;
        });

    public Task<string?> PickFolderAsync()
        => OnMainThread(() =>
        {
            var panel = NSOpenPanel.OpenPanel;
            panel.Title = "Choose a folder for the repo archives";
            panel.Prompt = "Choose";
            panel.CanChooseFiles = false;
            panel.CanChooseDirectories = true;
            panel.CanCreateDirectories = true;
            panel.AllowsMultipleSelection = false;
            return panel.RunModal() == 1 ? panel.Url?.Path : null;
        });

    static Task<string?> OnMainThread(Func<string?> work)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
