using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.App.Session.Files;
using Remotely.App.Session.Input;
using Remotely.HostCore;
using Remotely.UI.Localization;
using Remotely.ViewerCore;
using Remotely.ViewerCore.Mapping;
using Windows.ApplicationModel.DataTransfer;

namespace Remotely.App;

/// <summary>
/// The window that shows another PC. Frames arrive on a background thread and are painted in order (tiles must not be dropped; a backlog is replaced by a request for a complete picture).
/// <paramref name="reconnect"/> opens a fresh session to the same PC (it needs the connection parameters, which only the caller has); when it is
/// null a dropped session just shows "Disconnected". <paramref name="monitors"/> overrides the monitor list; by default it comes from the host's hello.
/// </summary>
public sealed partial class SessionWindow : Window
{
    private static readonly TimeSpan ToolbarHideDelay = TimeSpan.FromSeconds(2.5);
    private const int MaxQueuedFrames = 128;

    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly Func<CancellationToken, Task<ViewerSession>>? _reconnect;
    private readonly IMonitorSource? _monitorOverride;
    private readonly string _hostName;
    private readonly object _gate = new();
    private readonly List<FrameInfo> _frames = [];
    private readonly FrameCompositor _compositor = new();
    private readonly TransferQueue _transfers = new();
    private readonly DispatcherTimer _hideTimer = new() { Interval = ToolbarHideDelay };
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ViewerSession _session;
    private IMonitorSource _monitors = new AllMonitorsSource();
    private SessionMonitorSource? _ownedMonitors;
    private bool _renderQueued;
    private bool _closing;
    private bool _clipboardOn;
    private bool _viewOnly;
    private bool _pinned;
    private string? _lastClipboard;
    private double _shownZoom = 1;
    private int _framesShown;

    private CancellationTokenSource? _reconnectCts;
    private bool _reconnecting;
    private bool _autoRetry;
    private int _attempt;
    private DateTime _due;
    private string? _lastReason;

    public SessionWindow(ViewerSession session, string hostName, Func<CancellationToken, Task<ViewerSession>>? reconnect = null, IMonitorSource? monitors = null)
    {
        _session = session;
        _hostName = hostName;
        _reconnect = reconnect;
        _monitorOverride = monitors;
        InitializeComponent();
        Title = string.Format(Loc.Get("Session_WindowTitle"), hostName);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        App.ApplyTheme(this);
        RetryButton.Content = Loc.Get("Session_RetryNow");
        CloseButton.Content = Loc.Get("Session_Close");

        Screen.Input = e => _session.SendInput(e);
        Screen.FocusLost = () => _session.ReleaseAllInput();
        Screen.ViewChanged += OnViewChanged;
        Screen.Loaded += (_, _) => Screen.Focus(FocusState.Programmatic);
        Root.SizeChanged += (_, e) => Toolbar.MaxWidth = Math.Max(240, e.NewSize.Width - 32);
        Root.PointerMoved += (_, e) => { if (e.GetCurrentPoint(Root).Position.Y < 8) ShowToolbar(); };

        Transfers.Bind(_transfers);
        Transfers.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Transfers.ReceiveFolderChanged += folder => _session.DownloadDirectory = folder;

        WireToolbar();
        _hideTimer.Tick += (_, _) => OnHideTick();
        _statsTimer.Tick += (_, _) => UpdateStats();
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); NoticeBar.Visibility = Visibility.Collapsed; };
        _countdownTimer.Tick += (_, _) => OnCountdownTick();

        _clipboardOn = App.Settings.SyncClipboard && session.HasClipboard;
        Attach(session);
        ShowNotice(Loc.Get(session.CanControl ? "Session_ConnectedControl" : "Session_ConnectedViewOnly"));
        ShowToolbar();

        Clipboard.ContentChanged += OnLocalClipboardChanged;
        Closed += async (_, _) =>
        {
            _closing = true;
            Clipboard.ContentChanged -= OnLocalClipboardChanged;
            _monitors.Changed -= OnMonitorsChanged;
            _ownedMonitors?.Dispose();
            _hideTimer.Stop();
            _statsTimer.Stop();
            _noticeTimer.Stop();
            _countdownTimer.Stop();
            _reconnectCts?.Cancel();
            _transfers.Dispose();
            Screen.ReleaseKeys();
            await _session.DisposeAsync();
        };
    }

    // ---- hooks for the session self-test (the same objects the user works with) ----

    internal RemoteScreen RemoteView => Screen;
    internal TransfersPanel TransfersView => Transfers;
    internal bool TransfersVisible => TransfersHost.Visibility == Visibility.Visible;
    internal int FramesShown => Volatile.Read(ref _framesShown);
    internal void SetTransfersVisible(bool on) { TransfersHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed; Toolbar.SetFiles(on); }

    // ---- session wiring ----

    private void Attach(ViewerSession s)
    {
        _ = s.ReceivedStats;   // starts counting frames now
        s.FrameReceived += info => { if (ReferenceEquals(s, _session)) OnFrame(info); };
        s.ClipboardReceived += text => _ui.TryEnqueue(() => { if (ReferenceEquals(s, _session)) OnHostClipboard(text); });
        s.RichClipboardReceived += bundle => _ui.TryEnqueue(async () => { if (ReferenceEquals(s, _session)) await OnHostRichClipboardAsync(bundle); });
        s.HostError += error => _ui.TryEnqueue(() => { if (ReferenceEquals(s, _session)) ShowNotice(string.Format(Loc.Get("Session_OtherPcError"), error)); });
        s.Closed += reason => _ui.TryEnqueue(() => OnSessionClosed(s, reason));
        s.CursorShapeReceived += shape => _ui.TryEnqueue(() => { if (ReferenceEquals(s, _session)) Screen.AddCursorShape(shape); });
        s.CursorMoved += state => _ui.TryEnqueue(() => { if (ReferenceEquals(s, _session)) Screen.SetCursorState(state); });
        s.HostKeyboardLayoutChanged += layout => _ui.TryEnqueue(() => { if (ReferenceEquals(s, _session)) Screen.ConfigureKeyboard(KeyboardModeSetting.Auto, layout, s.SupportsExtendedInput); });

        _monitors.Changed -= OnMonitorsChanged;
        _ownedMonitors?.Dispose();
        _ownedMonitors = _monitorOverride is null ? new SessionMonitorSource(s) : null;
        _monitors = _monitorOverride ?? _ownedMonitors!;
        _monitors.Changed += OnMonitorsChanged;
        ApplyMonitors();

        Toolbar.Configure(_hostName, s.CanControl, s.CanSendSecureAttention, s.HasClipboard, s.HasFiles);
        Toolbar.SetClipboard(_clipboardOn);
        Toolbar.SetConnected(true);
        Screen.ExtendedInput = s.SupportsExtendedInput;
        Screen.ConfigureKeyboard(KeyboardModeSetting.Auto, s.HostKeyboardLayout, s.SupportsExtendedInput);
        ApplyControlState();

        Transfers.Settings.ApplyTo(s);
        Transfers.ListRemoteFiles = ct => s.FileLink.ListAsync(ct);
        Transfers.CanTransfer = s.HasFiles && s.CanControl;
        _transfers.Attach(s.FileLink);

        ConfigureClipboard(s);
        if (s.SupportsTiledFrames) _ = s.EnableTiledFramesAsync();
        _ = s.QueryHostKeyboardLayoutAsync();
        if (s.LatestFrame is { } early) OnFrame(early);   // the first picture can arrive before this window subscribed
    }

    private void ApplyControlState()
    {
        Screen.CanControl = _session.CanControl && !_viewOnly;
        var remoteKeys = Screen.CanControl && Screen.CaptureKeyboard;
        KeyboardText.Text = Loc.Get(!_session.CanControl || _viewOnly ? "Session_Badge_ViewOnly" : remoteKeys ? "Session_Badge_Remote" : "Session_Badge_Local");
        KeyboardDot.Fill = remoteKeys ? Palette.Connected(Root) : Palette.Offline(Root);
    }

    private void WireToolbar()
    {
        Toolbar.MonitorChosen += entry => _ = ChooseMonitorAsync(entry);
        Toolbar.ScaleChosen += mode => { Screen.SetMode(mode); Toolbar.SetScale(mode); Screen.Focus(FocusState.Programmatic); };
        Toolbar.FullScreenChanged += on =>
        {
            AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
            Screen.Focus(FocusState.Programmatic);
        };
        Toolbar.StatsChanged += on =>
        {
            StatsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) { UpdateStats(); _statsTimer.Start(); } else _statsTimer.Stop();
        };
        Toolbar.FilesChanged += on => SetTransfersVisible(on);
        Toolbar.KeyboardCaptureChanged += on => { Screen.CaptureKeyboard = on; ApplyControlState(); Screen.Focus(FocusState.Programmatic); };
        Toolbar.ViewOnlyChanged += on =>
        {
            _viewOnly = on;
            if (on) Screen.ReleaseKeys();   // nothing may stay pressed on the other PC once this window stops controlling it
            ApplyControlState();
            Screen.Focus(FocusState.Programmatic);
        };
        Toolbar.ClipboardChanged += on =>
        {
            _clipboardOn = on;
            ConfigureClipboard(_session);
        };
        Toolbar.PinnedChanged += on => { _pinned = on; ShowToolbar(); };
        Toolbar.SecureAttentionClicked += async () =>
        {
            await _session.SendSecureAttentionAsync();
            ShowNotice(Loc.Get("Session_SasSent"));
            Screen.Focus(FocusState.Programmatic);
        };
        Toolbar.EndClicked += () => Close();
        Toolbar.PointerStateChanged += () =>
        {
            if (Toolbar.IsPointerInside) _hideTimer.Stop(); else ShowToolbar();
        };
        Toolbar.MenuStateChanged += () => { if (!Toolbar.IsMenuOpen) ShowToolbar(); };
    }

    // ---- toolbar auto-hide ----

    private void ShowToolbar()
    {
        Toolbar.Visibility = Visibility.Visible;
        _hideTimer.Stop();
        if (!_pinned && !Toolbar.IsPointerInside) _hideTimer.Start();
    }

    private void OnHideTick()
    {
        _hideTimer.Stop();
        if (_pinned || Toolbar.IsPointerInside || Toolbar.IsMenuOpen || Overlay.Visibility == Visibility.Visible) return;
        Toolbar.Visibility = Visibility.Collapsed;
    }

    // ---- monitors ----

    private void OnMonitorsChanged() => _ui.TryEnqueue(ApplyMonitors);

    private void ApplyMonitors()
    {
        var selected = _monitors.Selected;
        Toolbar.SetMonitors(_monitors.Monitors, selected);
        var desktop = PixelRect.Union(_monitors.Monitors.Where(m => !m.IsAll).Select(m => m.Bounds));
        var shown = selected.IsAll || selected.Bounds.IsEmpty ? (PixelRect?)null : selected.Bounds;
        Screen.SetMonitor(shown, desktop.IsEmpty ? null : desktop);
    }

    private async Task ChooseMonitorAsync(MonitorEntry entry)
    {
        try { await _monitors.SelectAsync(entry); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowNotice(ex.Message); }
    }

    // ---- view state ----

    private void OnViewChanged()
    {
        Toolbar.SetScale(Screen.Mode);
        if (Screen.Mode == ScaleMode.Actual && Math.Abs(Screen.Zoom - _shownZoom) > 0.0001)
            ShowNotice(string.Format(Loc.Get("Session_ZoomNotice"), (Screen.Zoom * 100).ToString("0", CultureInfo.CurrentCulture)));
        _shownZoom = Screen.Zoom;
    }

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeBar.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // ---- stats overlay ----

    private void UpdateStats() =>
        StatsText.Text = SessionStatsText.Format(_session.ReceivedStats.Snapshot(), _session.Stats, Screen.FrameWidth, Screen.FrameHeight);

    // ---- frames ----

    private void OnFrame(FrameInfo info)
    {
        var overflow = false;
        bool start;
        lock (_gate)
        {
            if (info.Tiles is null) _frames.Clear();   // a complete picture makes everything before it irrelevant
            if (_frames.Count >= MaxQueuedFrames)
            {
                _frames.Clear();   // dropping tiles would corrupt the picture, so start over from a complete one
                overflow = true;
            }
            else _frames.Add(info);
            start = !_renderQueued;
            _renderQueued = true;
        }
        if (overflow) _ = _session.RequestKeyFrameAsync();
        if (!start) return;
        _ui.TryEnqueue(async () =>
        {
            while (true)
            {
                List<FrameInfo> batch;
                lock (_gate)
                {
                    if (_frames.Count == 0 || _closing) { _frames.Clear(); _renderQueued = false; return; }
                    batch = [.. _frames];
                    _frames.Clear();
                }
                foreach (var frame in batch)
                {
                    try
                    {
                        var result = await _compositor.ApplyAsync(frame);
                        if (result == CompositeResult.NeedKeyFrame) { _ = _session.RequestKeyFrameAsync(); break; }
                        if (result == CompositeResult.NewBitmap) Screen.SetFrame(_compositor.Source!, _compositor.Width, _compositor.Height);
                        Interlocked.Increment(ref _framesShown);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { /* skip a frame that cannot be decoded */ }
                }
            }
        });
    }

    // ---- connection lost, reconnect ----

    private void OnSessionClosed(ViewerSession s, string? reason)
    {
        if (_closing || !ReferenceEquals(s, _session)) return;
        Screen.CanControl = false;
        Toolbar.SetConnected(false);
        _lastReason = reason;
        _attempt = 0;
        ShowToolbar();
        var why = reason ?? Loc.Get("Session_EndedByOther");
        if (_reconnect is null)
        {
            ShowOverlay(Loc.Get("Session_Disconnected"), why, ring: false, retry: false);
            return;
        }
        _autoRetry = ReconnectSchedule.ShouldAutoRetry(reason);
        if (_autoRetry) ScheduleAttempt();
        else ShowOverlay(Loc.Get("Session_ConnectionLost"), why, ring: false, retry: true);
    }

    private void ScheduleAttempt()
    {
        if (ReconnectSchedule.DelayBefore(_attempt) is not { } delay)
        {
            ShowOverlay(Loc.Get("Session_ReconnectFailed"), _lastReason ?? "", ring: false, retry: true);
            return;
        }
        _due = DateTime.UtcNow.AddSeconds(delay);
        ShowOverlay(Loc.Get("Session_ConnectionLost"), CountdownText(), ring: false, retry: true);
        _countdownTimer.Start();
    }

    private string CountdownText()
    {
        var line = string.Format(Loc.Get("Session_Countdown"), ReconnectSchedule.SecondsLeft(_due, DateTime.UtcNow), _attempt + 1, ReconnectSchedule.MaxAttempts);
        return string.IsNullOrEmpty(_lastReason) ? line : _lastReason + "\n" + line;
    }

    private void OnCountdownTick()
    {
        if (ReconnectSchedule.SecondsLeft(_due, DateTime.UtcNow) == 0)
        {
            _countdownTimer.Stop();
            _ = TryReconnectAsync();
        }
        else
        {
            OverlayText.Text = CountdownText();
        }
    }

    private void OnRetryNow(object sender, RoutedEventArgs e)
    {
        _countdownTimer.Stop();
        if (ReconnectSchedule.DelayBefore(_attempt) is null) _attempt = 0;
        _ = TryReconnectAsync();
    }

    private async Task TryReconnectAsync()
    {
        if (_reconnecting || _closing || _reconnect is null) return;
        _reconnecting = true;
        _reconnectCts = new CancellationTokenSource();
        ShowOverlay(Loc.Get("Session_Reconnecting"), "", ring: true, retry: false);
        try
        {
            var fresh = await _reconnect(_reconnectCts.Token);
            if (_closing)
            {
                await fresh.DisposeAsync();
                return;
            }
            var old = _session;
            _session = fresh;
            _ = old.DisposeAsync().AsTask();
            Attach(fresh);
            HideOverlay();
            ShowNotice(Loc.Get("Session_Reconnected"));
            Screen.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _lastReason = ex.Message;
            _attempt++;
            if (_autoRetry) ScheduleAttempt();
            else ShowOverlay(Loc.Get("Session_ReconnectFailed"), ex.Message, ring: false, retry: true);
        }
        finally { _reconnecting = false; }
    }

    private void ShowOverlay(string title, string text, bool ring, bool retry)
    {
        OverlayTitle.Text = title;
        OverlayText.Text = text;
        OverlayText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        OverlayRing.IsActive = ring;
        OverlayRing.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    private void HideOverlay()
    {
        OverlayRing.IsActive = false;
        Overlay.Visibility = Visibility.Collapsed;
    }

    private void OnDisconnect(object sender, RoutedEventArgs e) => Close();

    // ---- clipboard ----

    /// <summary>Asks the host to offer (or stop offering) its rich clipboard, and sets what to do with large transfers.</summary>
    private void ConfigureClipboard(ViewerSession s)
    {
        if (!s.HasRichClipboard) return;
        s.RichClipboard.ConfirmReceive = info => ConfirmAsync(string.Format(Loc.Get("Session_ClipConfirmReceive"), TransferFormat.Size(info.TotalBytes)));
        s.RichClipboard.ConfirmSend = bundle => ConfirmAsync(string.Format(Loc.Get("Session_ClipConfirmSend"), TransferFormat.Size(bundle.TotalBytes)));
        _ = s.SubscribeRichClipboardAsync(_clipboardOn);
    }

    private Task<bool> ConfirmAsync(string question)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, Title = Loc.Get("Session_ClipConfirmTitle"), Content = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = Loc.Get("Session_ClipAllow"), CloseButtonText = Loc.Get("Session_ClipDeny"), DefaultButton = ContentDialogButton.Close,
                };
                answer.TrySetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { answer.TrySetResult(false); }
        })) answer.TrySetResult(false);
        return answer.Task;
    }

    private async void OnLocalClipboardChanged(object? sender, object e)
    {
        var s = _session;
        if (!_clipboardOn || _closing || !s.HasClipboard || !Screen.CanControl) return;
        if (ClipboardBridge.IsOwnChange()) return;   // our own write of what the other PC sent
        try
        {
            if (s.HasRichClipboard && await ClipboardBridge.ReadAsync(s.RichClipboard.Limits) is { HasRich: true } bundle)
            {
                var outcome = await s.SendRichClipboardAsync(bundle);
                if (outcome.Status is ClipSendStatus.Refused or ClipSendStatus.Failed) ShowNotice(outcome.Message ?? Loc.Get("Session_ClipNotSent"));
                return;
            }
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return;
            var text = await view.GetTextAsync();
            if (string.IsNullOrEmpty(text) || text == _lastClipboard) return;
            _lastClipboard = text;
            await s.SendClipboardAsync(text);
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

    private async Task OnHostRichClipboardAsync(ClipBundle bundle)
    {
        if (!_clipboardOn) return;
        try { await ClipboardBridge.ApplyAsync(bundle); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or InvalidOperationException) { ShowNotice(Loc.Get("Session_ClipNotApplied")); }
    }
}
