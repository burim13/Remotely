using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Remotely.Core.Lan;
using Remotely.Core.Lan.Consent;
using Remotely.UI.Localization;
using Remotely.ViewerCore;

namespace Remotely.App.Views.Connect;

/// <summary>
/// The "Connect to a PC" card: one token field with live typo hints, PCs that are sharing nearby, recent PCs, an address fallback,
/// progress with cancel, and a way to the connection check when it fails. Host it on any page (ConnectPage, HomePage) and handle
/// <see cref="DiagnoseRequested"/> to open <see cref="DiagnosisPage"/>.
/// </summary>
public sealed partial class ConnectCard : UserControl
{
    private readonly IRecentPcs _recent = new JsonRecentPcs(Path.Combine(ViewerSettings.Dir, "recent-pcs.json"));
    private readonly Button _diagnoseButton = new() { Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _nearbyCts;
    private bool _userCancelled;
    private IReadOnlyList<LanDiscovery.Nearby> _nearby = [];

    /// <summary>Raised when the person asks why a connection failed. Arguments: the address typed (or null) and the token's 4-character label (or null).</summary>
    public event Action<string?, string?>? DiagnoseRequested;

    /// <summary>Puts a token into the box (used by <c>--connect-token</c> and by deep links).</summary>
    public void SetToken(string token) => TokenBox.Text = token;

    public ConnectCard()
    {
        InitializeComponent();
        TitleText.Text = Loc.Get("Connect_Title");
        IntroText.Text = Loc.Get("Connect_Intro");
        AutomationProperties.SetName(TokenBox, Loc.Get("Connect_TokenName"));
        NearbyTitle.Text = Loc.Get("Connect_NearbyTitle");
        NearbyStatus.Text = Loc.Get("Connect_NearbySearching");
        AddressExpander.Header = Loc.Get("Connect_AddressHeader");
        AddressHelp.Text = Loc.Get("Connect_AddressHelp");
        AutomationProperties.SetName(AddressBox, Loc.Get("Connect_AddressName"));
        RecentTitle.Text = Loc.Get("Connect_RecentTitle");
        ConnectButton.Content = Loc.Get("Connect_Button");
        CancelButton.Content = Loc.Get("Connect_Cancel");
        _diagnoseButton.Content = Loc.Get("Connect_Diagnose");
        _diagnoseButton.Click += (_, _) => DiagnoseRequested?.Invoke(NullIfBlank(AddressBox.Text), LabelOf(TokenBox.Text));
        ConnectBar.ActionButton = _diagnoseButton;
        ShowRecent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowRecent();
        _nearbyCts?.Cancel();
        _nearbyCts = new CancellationTokenSource();
        _ = NearbyLoopAsync(_nearbyCts.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _nearbyCts?.Cancel();
        _nearbyCts = null;
    }

    // ---- token field ----

    private void OnTokenChanged(object sender, TextChangedEventArgs e)
    {
        var feedback = TokenHints.Check(TokenBox.Text);
        HintError.Visibility = !feedback.IsValid ? Visibility.Visible : Visibility.Collapsed;
        HintError.Text = !feedback.IsValid ? feedback.Message : "";
        HintInfo.Visibility = feedback.IsValid && feedback.Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HintInfo.Text = feedback.IsValid ? feedback.Message : "";
    }

    private void OnTokenKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) OnConnect(sender, e);
    }

    // ---- nearby PCs ----

    private async Task NearbyLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_connectCts is null)
                {
                    var found = await LanDiscovery.ListAsync(TimeSpan.FromSeconds(2), ct);
                    if (ct.IsCancellationRequested) return;
                    var own = LanNetwork.LocalAddresses();
                    ShowNearby([.. found.Where(n => !System.Net.IPAddress.IsLoopback(n.Endpoint.Address) && !own.Contains(n.Endpoint.Address))]);
                }
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // The nearby list is a convenience on top of typing the token. If the network stack misbehaves, the list stays as it was and connecting still works.
        }
    }

    private void ShowNearby(IReadOnlyList<LanDiscovery.Nearby> list)
    {
        _nearby = list;
        NearbyList.Items.Clear();
        NearbyStatus.Text = list.Count == 0 ? Loc.Get("Connect_NearbyEmpty") : Loc.Get("Connect_NearbyHint");
        foreach (var n in list)
        {
            var row = new Grid { ColumnSpacing = 12, Tag = n };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lockIcon = new FontIcon { Glyph = "", FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(lockIcon, Loc.Get("Connect_NearbyLock"));
            row.Children.Add(lockIcon);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(n.HostName) ? n.Endpoint.Address.ToString() : n.HostName });
            text.Children.Add(new TextBlock { Text = string.Format(Loc.Get("Connect_NearbyLabel"), n.Id), Opacity = 0.7, FontSize = 12 });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            NearbyList.Items.Add(row);
        }
    }

    private void OnNearbyClick(object sender, ItemClickEventArgs e)
    {
        if ((e.ClickedItem as FrameworkElement)?.Tag is not LanDiscovery.Nearby n) return;
        AddressBox.Text = "";
        TokenBox.Text = n.Id + "-";
        TokenBox.Focus(FocusState.Programmatic);
        TokenBox.Select(TokenBox.Text.Length, 0);
    }

    // ---- recent PCs ----

    private void ShowRecent()
    {
        var list = _recent.List();
        RecentList.Items.Clear();
        RecentEmpty.Text = Loc.Get("Connect_RecentEmpty");
        RecentEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var pc in list)
        {
            var row = new Grid { ColumnSpacing = 12, Tag = pc };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(pc.Name) ? pc.Address : pc.Name });
            text.Children.Add(new TextBlock { Text = pc.Address + "  ·  " + string.Format(Loc.Get("Connect_RecentUsed"), pc.LastUsedUtc.ToLocalTime().ToString("g")), Opacity = 0.7, FontSize = 12 });
            row.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (!string.IsNullOrEmpty(pc.HostDeviceId))
            {
                var reconnect = new Button { Content = Loc.Get("Connect_Reconnect"), Tag = pc, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                ToolTipService.SetToolTip(reconnect, Loc.Get("Connect_ReconnectTip"));
                reconnect.Click += OnReconnect;
                buttons.Children.Add(reconnect);
            }
            var remove = new Button { Content = Loc.Get("Connect_RecentRemove"), Tag = pc.Address };
            remove.Click += (s, _) => { _recent.Remove((string)((Button)s).Tag); ShowRecent(); };
            buttons.Children.Add(remove);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            RecentList.Items.Add(row);
        }
    }

    private void OnRecentClick(object sender, ItemClickEventArgs e)
    {
        if ((e.ClickedItem as FrameworkElement)?.Tag is not RecentPc pc) return;
        AddressBox.Text = pc.Address;
        AddressExpander.IsExpanded = true;
        TokenBox.Focus(FocusState.Programmatic);
    }

    // ---- connect ----

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _userCancelled = true;
        _connectCts?.Cancel();
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var typed = TokenBox.Text;
        var address = NullIfBlank(AddressBox.Text);
        var known = TokenHints.MatchNearby(typed, _nearby);
        var feedback = TokenHints.Check(typed);
        await RunAsync((ct, progress) => LanControllerFlow.ConnectWithTokenAsync(typed, Environment.MachineName, address, Identity(), ct, progress, known), canDiagnose: feedback.IsComplete);
    }

    private async void OnReconnect(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecentPc pc) return;
        var identity = Identity();
        if (identity is null) return;
        await RunAsync((ct, _) => LanControllerFlow.ResumeAsync(pc, identity, ct), canDiagnose: true);
    }

    /// <summary>This PC's device key (the one the server mode uses). Null if it cannot be read: the token flow then works like the first release, and the other PC can only allow once.</summary>
    private static Remotely.Core.Crypto.DeviceIdentity? Identity()
    {
        try { return App.Connection.Identity; }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException) { return null; }
    }

    private async Task RunAsync(Func<CancellationToken, IProgress<LanConnectStep>, Task<ControllerConnection>> connect, bool canDiagnose)
    {
        if (_connectCts is not null) return;
        ConnectBar.IsOpen = false;
        // No overall timeout here: the flow limits the connection stage (30 s) and the wait for the other person's decision (60 s) separately.
        var cts = _connectCts = new CancellationTokenSource();
        _userCancelled = false;
        SetBusy(true);
        var progress = new Progress<LanConnectStep>(step =>
        {
            if (!ReferenceEquals(_connectCts, cts)) return;
            StatusText.Text = Loc.Get(step switch
            {
                LanConnectStep.FindingPc => "Connect_StepFinding",
                LanConnectStep.Reaching => "Connect_StepReaching",
                LanConnectStep.WaitingForApproval => "Connect_StepWaiting",
                _ => "Connect_StepVerifying",
            });
        });
        try
        {
            var found = await connect(cts.Token, progress);
            ViewerSession session;
            try { session = await ViewerSession.OpenAsync(found.Channel, found.HostName, cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                found.Channel.Dispose();
                throw new LanConnectException(string.Format(Loc.Get("Connect_SessionFailed"), ex.Message));
            }
            var shownName = string.IsNullOrEmpty(found.HostName) ? found.Endpoint.Address.ToString() : found.HostName;
            var hostId = found.Resumed ? RememberedId(found.Endpoint) : found.Remembered ? found.Host?.DeviceId : null;
            _recent.Record(found.HostName, found.Endpoint.ToString(), hostId);
            TokenBox.Text = "";
            ShowRecent();
            var self = Identity();
            var recent = new RecentPc(found.HostName, found.Endpoint.ToString(), DateTimeOffset.UtcNow, hostId);
            // A token works once, so only a PC that remembers this one can be reopened if the connection drops.
            Func<CancellationToken, Task<ViewerSession>>? reopen = hostId is not null && self is not null
                ? async ct =>
                {
                    var again = await LanControllerFlow.ResumeAsync(recent, self, ct);
                    return await ViewerSession.OpenAsync(again.Channel, shownName, ct);
                }
                : null;
            new SessionWindow(session, shownName, reopen).Activate();
        }
        catch (ConsentDeniedException) { ShowError(Loc.Get("Connect_Denied"), canDiagnose: false); }
        catch (LanConnectException ex) { ShowError(ex.Message, canDiagnose); }
        catch (OperationCanceledException)
        {
            if (_userCancelled) StatusText.Text = Loc.Get("Connect_Cancelled");
            else ShowError(Loc.Get("Connect_Timeout"), canDiagnose: true);
        }
        finally
        {
            cts.Dispose();
            _connectCts = null;
            SetBusy(false, keepStatus: _userCancelled);
        }
    }

    private string? RememberedId(System.Net.IPEndPoint endpoint) =>
        _recent.List().FirstOrDefault(p => string.Equals(p.Address, endpoint.ToString(), StringComparison.OrdinalIgnoreCase))?.HostDeviceId;

    private void SetBusy(bool busy, bool keepStatus = false)
    {
        Busy.IsActive = busy;
        ConnectButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) StatusText.Text = Loc.Get("Connect_StepFinding");
        else if (!keepStatus) StatusText.Text = "";
    }

    private void ShowError(string message, bool canDiagnose)
    {
        ConnectBar.Message = message;
        _diagnoseButton.Visibility = canDiagnose ? Visibility.Visible : Visibility.Collapsed;
        ConnectBar.IsOpen = true;
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>The first 4 characters of what was typed when they can be a label, otherwise null.</summary>
    private static string? LabelOf(string? typed)
    {
        var compact = new string((typed ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return compact.Length >= LanToken.IdLength && compact[..LanToken.IdLength].All(c => LanToken.Alphabet.Contains(c)) ? compact[..LanToken.IdLength] : null;
    }
}
