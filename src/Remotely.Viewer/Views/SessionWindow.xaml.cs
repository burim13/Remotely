using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.Viewer.Views;

public partial class SessionWindow : Window
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly DeviceInfo _host;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly object _frameGate = new();
    private ViewerSession? _session;
    private (BitmapSource Image, FrameInfo Info)? _latest;
    private bool _renderQueued;
    private int _framesThisSecond, _fps;
    private string? _lastClipboard;
    private bool _clipboardOn;
    private bool _closing;
    private string _desktop = "";
    private IntPtr _hwnd;

    internal SessionWindow(DeviceInfo host)
    {
        _host = host;
        InitializeComponent();
        Shell.WindowFrame.Round(this);
        HostName.Text = host.Name;
        Title = $"Remotely – {host.Name}";
        _clipboardOn = App.Current.Settings.SyncClipboard;
        _statsTimer.Tick += (_, _) => { _fps = _framesThisSecond; _framesThisSecond = 0; UpdateStats(); };
        _statsTimer.Start();
        Screen.Input = e => _session?.SendInput(e);
        Screen.FocusLost = () => _session?.ReleaseAllInput();
        Screen.Drop += OnDrop;
        Screen.DragOver += (_, e) => e.Effects = _session is { HasFiles: true } && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        Loaded += async (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            AddClipboardFormatListener(_hwnd);
            await ConnectAsync();
        };
    }

    // ---- connecting ----

    private async Task ConnectAsync()
    {
        ShowOverlay("Connecting…", "Setting up an encrypted connection through your server.", retry: false);
        try
        {
            var client = await App.Current.Connection.EnsureAsync();
            var session = await ViewerSession.OpenAsync(client, App.Current.Connection.Identity, _host.DeviceId);
            session.DownloadDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            session.FrameReceived += OnFrame;
            session.ClipboardReceived += OnHostClipboard;
            session.HostError += e => Dispatcher.BeginInvoke(() => Status.Text = "Host: " + e);
            session.Closed += reason => Dispatcher.BeginInvoke(() => OnSessionClosed(reason));
            _session = session;

            Screen.CanControl = session.CanControl;
            SasButton.IsEnabled = session.CanSendSecureAttention;
            UploadButton.IsEnabled = DownloadButton.IsEnabled = session.HasFiles;
            ClipboardButton.IsEnabled = session.HasClipboard;
            UpdateClipboardButton();
            ModeText.Text = session.CanControl ? "Control" : "View only";
            Overlay.Visibility = Visibility.Collapsed;
            Status.Text = session.CanControl
                ? "Connected. Your keyboard and mouse control this PC while this window has focus."
                : "Connected in view-only mode. Ask the owner of that PC to allow control.";
            Screen.Focus();
        }
        catch (Exception ex) when (ex is CoordinatorException or HandshakeException or IOException or TimeoutException or System.Net.WebSockets.WebSocketException or HttpRequestException or OperationCanceledException)
        {
            ShowOverlay("Could not connect", Friendly(ex), retry: true);
            ModeText.Text = "Offline";
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        HandshakeException h when h.Message.Contains("not trusted", StringComparison.OrdinalIgnoreCase)
            => "That PC does not trust this device yet. On that PC, run:  Remotely.Host trust " + App.Current.Connection.Identity.DeviceId,
        CoordinatorException c => c.Message,
        _ => ex.Message,
    };

    private void ShowOverlay(string title, string text, bool retry)
    {
        OverlayTitle.Text = title;
        OverlayText.Text = text;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    private void OnSessionClosed(string? reason)
    {
        if (_closing) return;
        _session = null;
        Screen.CanControl = false;
        SasButton.IsEnabled = UploadButton.IsEnabled = DownloadButton.IsEnabled = ClipboardButton.IsEnabled = false;
        ModeText.Text = "Disconnected";
        ShowOverlay("Disconnected", reason ?? "The other PC ended the session.", retry: true);
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (_session is not null) await _session.DisposeAsync();
        _session = null;
        await ConnectAsync();
    }

    // ---- frames ----

    private void OnFrame(FrameInfo info)
    {
        // Decode off the UI thread, and keep only the newest frame if the UI is behind.
        BitmapSource image;
        try
        {
            using var stream = new MemoryStream(info.Jpeg);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            image = decoder.Frames[0];
            image.Freeze();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException) { return; }

        lock (_frameGate)
        {
            _latest = (image, info);
            if (_renderQueued) return;
            _renderQueued = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            (BitmapSource Image, FrameInfo Info)? frame;
            lock (_frameGate) { frame = _latest; _latest = null; _renderQueued = false; }
            if (frame is null) return;
            Screen.SetFrame(frame.Value.Image);
            _framesThisSecond++;
            if (_desktop != frame.Value.Info.Desktop || Stats.Text.Length == 0)
            {
                _desktop = frame.Value.Info.Desktop;
                UpdateStats(frame.Value.Info);
            }
        });
    }

    private FrameInfo? _lastInfo;

    private void UpdateStats(FrameInfo? info = null)
    {
        _lastInfo = info ?? _lastInfo;
        if (_lastInfo is null) return;
        var screen = string.Equals(_lastInfo.Desktop, "Winlogon", StringComparison.OrdinalIgnoreCase) ? "Lock / sign-in screen" : "Desktop";
        Stats.Text = $"{screen} · {_lastInfo.Width}×{_lastInfo.Height} · {_fps} fps";
    }

    // ---- clipboard ----

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) _ = OnLocalClipboardChangedAsync();
        return IntPtr.Zero;
    }

    private async Task OnLocalClipboardChangedAsync()
    {
        if (!_clipboardOn || _session is not { HasClipboard: true } session) return;
        string? text = null;
        for (var i = 0; i < 5 && text is null; i++)
        {
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch (COMException) { await Task.Delay(40); } // another program holds the clipboard for a moment
        }
        if (string.IsNullOrEmpty(text) || text == _lastClipboard) return;
        _lastClipboard = text;
        try { await session.SendClipboardAsync(text); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void OnHostClipboard(string text) => Dispatcher.BeginInvoke(() =>
    {
        if (!_clipboardOn || text == _lastClipboard) return;
        _lastClipboard = text; // remember it first so our own listener does not send it straight back
        try { Clipboard.SetText(text); }
        catch (COMException) { /* clipboard busy; the next change will come through */ }
    });

    private void OnToggleClipboard(object sender, RoutedEventArgs e)
    {
        _clipboardOn = !_clipboardOn;
        UpdateClipboardButton();
        Screen.Focus();
    }

    private void UpdateClipboardButton()
    {
        ClipboardButton.ToolTip = _clipboardOn ? "Clipboard sharing is on (click to turn off)" : "Clipboard sharing is off (click to turn on)";
        ClipboardButton.Opacity = _clipboardOn ? 1 : 0.5;
    }

    // ---- toolbar ----

    private async void OnSas(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        await _session.SendSecureAttentionAsync();
        Status.Text = "Sent Ctrl+Alt+Del.";
        Screen.Focus();
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Send files to " + _host.Name, Multiselect = true };
        if (dialog.ShowDialog(this) == true) await UploadAsync(dialog.FileNames);
        Screen.Focus();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await UploadAsync(files);
    }

    private async Task UploadAsync(IEnumerable<string> files)
    {
        if (_session is not { HasFiles: true } session) return;
        foreach (var file in files.Where(File.Exists))
        {
            var name = Path.GetFileName(file);
            try
            {
                var progress = new Progress<double>(p => Status.Text = $"Sending {name}… {p:P0}");
                var saved = await session.UploadFileAsync(file, progress);
                Status.Text = $"Sent {name}. It is in the Received folder on {_host.Name} as {saved}.";
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
            {
                Status.Text = $"Could not send {name}: {ex.Message}";
            }
        }
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_session is not { HasFiles: true } session) return;
        try
        {
            var files = await session.ListSharedFilesAsync();
            if (files.Length == 0)
            {
                Status.Text = $"{_host.Name} has nothing in its shared folder. Put files in Documents\\Remotely\\Share on that PC to make them available.";
                return;
            }
            var picker = new FilesWindow(files) { Owner = this };
            if (picker.ShowDialog() != true || picker.Chosen is not { } chosen) return;
            Status.Text = $"Downloading {chosen}…";
            var path = await session.DownloadFileAsync(chosen);
            Status.Text = $"Saved to {path}";
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            Status.Text = "Download failed: " + ex.Message;
        }
        Screen.Focus();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override async void OnClosed(EventArgs e)
    {
        _closing = true;
        _statsTimer.Stop();
        if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd);
        base.OnClosed(e);
        if (_session is not null) await _session.DisposeAsync();
    }
}
