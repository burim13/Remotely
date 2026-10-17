using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.UI.Localization;
using Windows.Storage.Pickers;

namespace Remotely.App;

/// <summary>
/// The simple send-a-file / get-a-file actions behind the toolbar's Files menu, as they were before the session window was reworked.
/// The file transfer panel (agent A8, <c>Session/Files/**</c>) replaces <see cref="OnUploadRequested"/> and <see cref="OnDownloadRequested"/>.
/// </summary>
public sealed partial class SessionWindow
{
    private async void OnUploadRequested()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        ShowNotice(string.Format(Loc.Get("Session_Sending"), file.Name));
        try
        {
            var result = await _session.UploadFileAsync(file.Path, new Progress<double>(p => ShowNotice(string.Format(Loc.Get("Session_SendingProgress"), file.Name, p.ToString("P0")))));
            ShowNotice(string.Format(Loc.Get("Session_Sent"), result));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { ShowNotice(string.Format(Loc.Get("Session_SendFailed"), ex.Message)); }
    }

    private async void OnDownloadRequested()
    {
        try
        {
            var files = await _session.ListSharedFilesAsync();
            if (files.Length == 0)
            {
                ShowNotice(string.Format(Loc.Get("Session_ShareEmpty"), _hostName));
                return;
            }
            var list = new ListView { ItemsSource = files.Select(f => f.Name).ToList(), SelectionMode = ListViewSelectionMode.Single, MinWidth = 360, MaxHeight = 320 };
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = string.Format(Loc.Get("Session_FilesOn"), _hostName), Content = list,
                PrimaryButtonText = Loc.Get("Session_GetFileButton"), CloseButtonText = Loc.Get("Session_Cancel"), DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not string chosen) return;
            ShowNotice(string.Format(Loc.Get("Session_Downloading"), chosen));
            var path = await _session.DownloadFileAsync(chosen);
            ShowNotice(string.Format(Loc.Get("Session_Saved"), path));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            ShowNotice(string.Format(Loc.Get("Session_DownloadFailed"), ex.Message));
        }
    }
}
