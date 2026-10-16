using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Remotely.HostCore;
using Remotely.ViewerCore;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Remotely.App;

/// <summary>The window that shows another PC. Frames arrive on a background thread; only the newest one is decoded and shown.</summary>
public sealed partial class SessionWindow : Window
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly ViewerSession _session;
    private readonly string _hostName;
    private readonly object _gate = new();
    private FrameInfo? _pending;
    private bool _renderQueued;
    private bool _closing;
    private bool _clipboardOn;
    private string? _lastClipboard;

    public SessionWindow(ViewerSession session, string hostName)
    {
        _session = session;
        _hostName = hostName;
        InitializeComponent();
        Title = $"Remotely – {hostName}";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        Screen.CanControl = session.CanControl;
        Screen.Input = e => _session.SendInput(e);
        Screen.FocusLost = () => _session.ReleaseAllInput();
        App.ApplyTheme(this);
        _clipboardOn = App.Settings.SyncClipboard && session.HasClipboard;
        UploadButton.IsEnabled = DownloadButton.IsEnabled = session.HasFiles;
        SasButton.IsEnabled = session.CanSendSecureAttention;
        ClipboardButton.IsEnabled = session.HasClipboard;
        ClipboardButton.IsChecked = _clipboardOn;
        Status.Text = session.CanControl
            ? "Connected. Your keyboard and mouse control the other PC while this window has focus."
            : "Connected in view-only mode.";

        session.FrameReceived += OnFrame;
        session.ClipboardReceived += text => _ui.TryEnqueue(() => OnHostClipboard(text));
        session.HostError += error => _ui.TryEnqueue(() => Status.Text = "Other PC: " + error);
        session.Closed += reason => _ui.TryEnqueue(() => OnClosed(reason));
        Clipboard.ContentChanged += OnLocalClipboardChanged;
        Closed += async (_, _) =>
        {
            _closing = true;
            Clipboard.ContentChanged -= OnLocalClipboardChanged;
            await _session.DisposeAsync();
        };
        Screen.Loaded += (_, _) => Screen.Focus(FocusState.Programmatic);
    }

    // ---- frames ----

    private void OnFrame(FrameInfo info)
    {
        lock (_gate)
        {
            _pending = info;   // a slow UI only ever shows the newest frame
            if (_renderQueued) return;
            _renderQueued = true;
        }
        _ui.TryEnqueue(async () =>
        {
            FrameInfo? frame;
            lock (_gate) { frame = _pending; _pending = null; _renderQueued = false; }
            if (frame is null || _closing) return;
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(frame.Jpeg.AsBuffer());
                stream.Seek(0);
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                Screen.SetFrame(image, frame.Width, frame.Height);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { /* skip a frame that cannot be decoded */ }
        });
    }

    private void OnClosed(string? reason)
    {
        if (_closing) return;
        Screen.CanControl = false;
        UploadButton.IsEnabled = DownloadButton.IsEnabled = SasButton.IsEnabled = ClipboardButton.IsEnabled = false;
        OverlayTitle.Text = "Disconnected";
        OverlayText.Text = reason ?? "The other PC ended the session.";
        Overlay.Visibility = Visibility.Visible;
    }

    private void OnDisconnect(object sender, RoutedEventArgs e) => Close();

    // ---- clipboard ----

    private async void OnLocalClipboardChanged(object? sender, object e)
    {
        if (!_clipboardOn || _closing || !_session.HasClipboard) return;
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return;
            var text = await view.GetTextAsync();
            if (string.IsNullOrEmpty(text) || text == _lastClipboard) return;
            _lastClipboard = text;
            await _session.SendClipboardAsync(text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { /* another app holds the clipboard right now */ }
    }

    private void OnHostClipboard(string text)
    {
        if (!_clipboardOn || text == _lastClipboard) return;
        _lastClipboard = text;
        var data = new DataPackage();
        data.SetText(text);
        try { Clipboard.SetContent(data); } catch (System.Runtime.InteropServices.COMException) { }
    }

    private void OnClipboardToggle(object sender, RoutedEventArgs e) => _clipboardOn = ClipboardButton.IsChecked == true;

    // ---- files ----

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        Status.Text = $"Sending {file.Name}…";
        try
        {
            var result = await _session.UploadFileAsync(file.Path, new Progress<double>(p => Status.Text = $"Sending {file.Name}… {p:P0}"));
            Status.Text = $"Sent {result} to the other PC.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Status.Text = "Could not send the file: " + ex.Message; }
    }

    private async void OnSas(object sender, RoutedEventArgs e)
    {
        await _session.SendSecureAttentionAsync();
        Status.Text = "Sent Ctrl+Alt+Del.";
        Screen.Focus(FocusState.Programmatic);
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = await _session.ListSharedFilesAsync();
            if (files.Length == 0)
            {
                Status.Text = $"{_hostName} has nothing in its shared folder. Put files in Documents\\Remotely\\Share on that PC to make them available.";
                return;
            }
            var list = new ListView { ItemsSource = files.Select(f => f.Name).ToList(), SelectionMode = ListViewSelectionMode.Single, MinWidth = 360, MaxHeight = 320 };
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = $"Files on {_hostName}", Content = list,
                PrimaryButtonText = "Get file", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not string chosen) return;
            Status.Text = $"Downloading {chosen}…";
            var path = await _session.DownloadFileAsync(chosen);
            Status.Text = $"Saved to {path}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            Status.Text = "Download failed: " + ex.Message;
        }
    }
}
