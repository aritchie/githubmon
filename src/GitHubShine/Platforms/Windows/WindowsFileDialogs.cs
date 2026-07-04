using Windows.Storage.Pickers;

namespace GitHubShine.Platforms.Windows;

/// <summary>
/// Native WinUI file/folder pickers. Each picker must be seeded with the app
/// window's HWND (unpackaged Win32 apps have no implicit owner window) and
/// invoked on the UI thread, so every call marshals onto the main thread.
/// </summary>
public sealed class WindowsFileDialogs : IFileDialogs
{
    public Task<string?> PickSavePathAsync(string suggestedFileName) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedFileName
            };
            // FileSavePicker throws without at least one file-type choice.
            picker.FileTypeChoices.Add("GitHub Shine backup", new List<string> { ".db" });
            InitializeWithWindow(picker);
            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        });

    public Task<string?> PickOpenPathAsync() =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            // FileOpenPicker throws without at least one filter; offer .db and all files.
            picker.FileTypeFilter.Add(".db");
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow(picker);
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        });

    public Task<string?> PickFolderAsync() =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow(picker);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        });

    static void InitializeWithWindow(object picker)
    {
        var mauiWindow = Application.Current?.Windows.FirstOrDefault();
        if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window platformWindow)
            throw new InvalidOperationException("No active window to host the file picker.");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(platformWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
