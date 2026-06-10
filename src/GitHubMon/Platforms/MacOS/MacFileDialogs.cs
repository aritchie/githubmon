using AppKit;
using GitHubMon.Settings;

namespace GitHubMon.Platforms.MacOS;

/// <summary>Native AppKit save/open panels. Panel APIs must run on the main thread.</summary>
public sealed class MacFileDialogs : IFileDialogs
{
    public Task<string?> PickSavePathAsync(string suggestedFileName)
        => OnMainThread(() =>
        {
            var panel = new NSSavePanel
            {
                Title = "Back up GitHubMon",
                NameFieldStringValue = suggestedFileName,
                CanCreateDirectories = true
            };
            return panel.RunModal() == 1 ? panel.Url?.Path : null;
        });

    public Task<string?> PickOpenPathAsync()
        => OnMainThread(() =>
        {
            var panel = NSOpenPanel.OpenPanel;
            panel.Title = "Restore GitHubMon backup";
            panel.CanChooseFiles = true;
            panel.CanChooseDirectories = false;
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
