using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Remotely.Core.Lan.Consent;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Share;

/// <summary>
/// Always on top while another PC is connected: who it is, since when, "Pause control" (view only) and "Stop sharing". Closing the window with its X does nothing while the
/// session lasts, so the indicator cannot be dismissed by accident; it closes itself when the session ends.
/// </summary>
public sealed partial class SessionBannerWindow : Window
{
    private readonly ShareService _share;
    private readonly LanSessionInfo _info;
    private readonly DispatcherQueueTimer _tick;
    private bool _closing;
    private bool _busy;

    public SessionBannerWindow(ShareService share, LanSessionInfo info)
    {
        _share = share;
        _info = info;
        InitializeComponent();
        Title = Loc.Get("Banner_Title");
        App.ApplyTheme(this);
        StopButton.Content = Loc.Get("Banner_Stop");
        Headline.Text = ShareText.Format("Banner_Who", info.ControllerName, info.Address);
        Refresh(share.State.Current);

        ShareWindows.PlaceTop(this, 640, 120, center: false);
        AppWindow.Closing += (_, args) => { if (!_closing) args.Cancel = true; };
        share.State.Changed += OnStateChanged;

        _tick = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => Refresh(_share.State.Current);
        _tick.Start();
    }

    public bool IsPaused => _share.State.Current.ControlPaused;

    private void OnStateChanged(ShareSnapshot snapshot) => DispatcherQueue.TryEnqueue(() => { if (!_closing) Refresh(snapshot); });

    private void Refresh(ShareSnapshot s)
    {
        var paused = s.ControlPaused;
        PausedStrip.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        ActiveStrip.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        PauseButton.Content = Loc.Get(paused ? "Banner_Resume" : "Banner_Pause");
        var since = ShareText.Format("Banner_Since", ShareText.When(_info.StartedUtc), ShareText.Duration(DateTimeOffset.UtcNow - _info.StartedUtc));
        Detail.Text = paused ? since + "  " + Loc.Get("Banner_PausedNote") : since;
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try { await _share.Coordinator.SetControlPausedAsync(!IsPaused, Loc.Get("Banner_ViewerNotice")); }
        finally { _busy = false; }
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        await _share.StopSharingAsync();
    }

    /// <summary>Called when the session is over.</summary>
    public void CloseForReal()
    {
        if (_closing) return;
        _closing = true;
        _tick.Stop();
        _share.State.Changed -= OnStateChanged;
        Close();
    }
}
