using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Remotely.Core.Lan;
using Remotely.Core.Lan.Consent;
using Remotely.UI.Localization;
using Windows.ApplicationModel.DataTransfer;

namespace Remotely.App.Views.Share;

/// <summary>
/// The "Share this PC" card: switch, plain-language state, the big token with its expiry ring, copy and new token, a QR code of the token for a phone (inside an expander, only while a
/// token is on offer), this PC's addresses, and the two consent options. It needs no page around it: put it in any panel. It starts the question window, banner and toasts the first time it loads.
/// </summary>
public sealed partial class ShareCard : UserControl
{
    private static readonly TimeSpan TokenLifetime = new LanHostOptions().TokenLifetime;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _tick;
    private bool _syncing;
    private string? _qrDrawnFor;
    private DateTimeOffset _copiedUntil;

    public ShareCard()
    {
        InitializeComponent();
        ApplyText();
        _tick = _ui.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => RefreshToken();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void ApplyText()
    {
        TitleText.Text = Loc.Get("Share_Title");
        IntroText.Text = Loc.Get("Share_Intro");
        ShareSwitch.OnContent = Loc.Get("Share_Switch_On");
        ShareSwitch.OffContent = Loc.Get("Share_Switch_Off");
        TokenLabel.Text = Loc.Get("Share_Token_Label");
        CopyButton.Content = Loc.Get("Share_Copy");
        NewTokenButton.Content = Loc.Get("Share_NewToken");
        QrExpander.Header = Loc.Get("Share_Qr_Header");
        QrHelp.Text = Loc.Get("Share_Qr_Help");
        AddressLabel.Text = Loc.Get("Share_Addresses");
        AskSwitch.Header = Loc.Get("Share_Ask_Label");
        AskSwitch.OnContent = Loc.Get("Switch_On");
        AskSwitch.OffContent = Loc.Get("Switch_Off");
        AskHelp.Text = Loc.Get("Share_Ask_Help");
        RememberedSwitch.Header = Loc.Get("Share_Remembered_Label");
        RememberedSwitch.OnContent = Loc.Get("Switch_On");
        RememberedSwitch.OffContent = Loc.Get("Switch_Off");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TokenText, Loc.Get("Share_Token_Label"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ShareSwitch, Loc.Get("Share_Title"));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Share.AttachUi(_ui);
        App.Share.State.Changed += OnStateChanged;
        App.Share.Coordinator.Notice += OnNotice;
        _syncing = true;
        AskSwitch.IsOn = App.Share.Coordinator.Options.AskBeforeConnect;
        RememberedSwitch.IsOn = App.Share.Coordinator.Options.AllowRemembered;
        _syncing = false;
        _tick.Start();
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _tick.Stop();
        App.Share.State.Changed -= OnStateChanged;
        App.Share.Coordinator.Notice -= OnNotice;
    }

    private void OnStateChanged(ShareSnapshot snapshot) => _ui.TryEnqueue(Render);

    private void OnNotice(string text) => _ui.TryEnqueue(() => ShowNotice(InfoBarSeverity.Warning, text));

    private void ShowNotice(InfoBarSeverity severity, string text)
    {
        NoticeBar.Severity = severity;
        NoticeBar.Message = text;
        NoticeBar.IsOpen = true;
    }

    private void Render()
    {
        var snapshot = App.Share.State.Current;
        var sharing = snapshot.State is ShareState.Ready or ShareState.Connecting or ShareState.AwaitingConsent or ShareState.InSession;
        _syncing = true;
        ShareSwitch.IsOn = sharing;
        _syncing = false;
        StateText.Text = ShareText.State(snapshot);
        var offersToken = snapshot.State is ShareState.Ready or ShareState.Connecting;
        TokenPanel.Visibility = offersToken ? Visibility.Visible : Visibility.Collapsed;
        QrExpander.Visibility = snapshot.State == ShareState.Ready ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot.State == ShareState.Error) ShowNotice(InfoBarSeverity.Error, snapshot.ErrorText ?? "");
        else if (sharing || snapshot.State == ShareState.Off) { if (NoticeBar.Severity == InfoBarSeverity.Error) NoticeBar.IsOpen = false; }
        RefreshToken();
    }

    private void RefreshToken()
    {
        if (CopyButton.Content is string && DateTimeOffset.UtcNow >= _copiedUntil) CopyButton.Content = Loc.Get("Share_Copy");
        var host = App.Share.Host;
        if (host is null || TokenPanel.Visibility != Visibility.Visible) return;
        var token = host.CurrentToken.Display;
        TokenText.Text = token;
        var left = host.TokenExpiresUtc - DateTimeOffset.UtcNow;
        TokenInfo.Text = left.TotalSeconds > 0
            ? ShareText.Format("Share_ExpiresIn", $"{(int)left.TotalMinutes}:{left.Seconds:00}")
            : Loc.Get("Share_Refreshing");
        ExpiryRing.Value = Math.Clamp(left.TotalSeconds / TokenLifetime.TotalSeconds * 100, 0, 100);
        var addresses = LanNetwork.LocalAddresses();
        AddressText.Text = addresses.Count == 0
            ? Loc.Get("Share_NoNetwork")
            : string.Join(Environment.NewLine, addresses.Select(a => $"{a}:{host.TcpPort}"));
        if (QrExpander.IsExpanded && QrExpander.Visibility == Visibility.Visible && _qrDrawnFor != token) DrawQr(token);
    }

    private void OnQrExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (App.Share.Host is { } host && _qrDrawnFor != host.CurrentToken.Display) DrawQr(host.CurrentToken.Display);
    }

    /// <summary>The picture is the token as plain text, so a phone camera shows it and the person types it on the other PC. Black on white whatever the theme, with a 4-module quiet zone.</summary>
    private void DrawQr(string text)
    {
        const double cell = 6;
        var qr = QrCode.Encode(text);
        var group = new GeometryGroup();
        for (var y = 0; y < qr.Size; y++)
            for (var x = 0; x < qr.Size; x++)
                if (qr[x, y]) group.Children.Add(new RectangleGeometry { Rect = new Windows.Foundation.Rect(x * cell, y * cell, cell, cell) });
        QrCanvas.Children.Clear();
        QrCanvas.Width = QrCanvas.Height = qr.Size * cell;
        QrCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = group, Fill = new SolidColorBrush(Colors.Black) });
        QrCanvas.Margin = new Thickness(4 * cell - 8);   // the white border adds 8 on each side
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(QrCanvas, ShareText.Format("Share_Qr_Name", text));
        _qrDrawnFor = text;
    }

    private async void OnShareToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (ShareSwitch.IsOn)
        {
            var error = App.Share.Start(_ => { });
            if (error is not null) ShowNotice(InfoBarSeverity.Error, error);
        }
        else await App.Share.StopAsync();
        Render();
    }

    private void OnCopyToken(object sender, RoutedEventArgs e)
    {
        if (App.Share.Host is not { } host) return;
        var data = new DataPackage();
        data.SetText(host.CurrentToken.Display);
        Clipboard.SetContent(data);
        CopyButton.Content = Loc.Get("Share_Copied");
        _copiedUntil = DateTimeOffset.UtcNow.AddSeconds(2);
    }

    private void OnNewToken(object sender, RoutedEventArgs e)
    {
        App.Share.Host?.NewToken();
        RefreshToken();
    }

    private void OnAskToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Share.Coordinator.SetAskBeforeConnect(AskSwitch.IsOn);
        if (!AskSwitch.IsOn) ShowNotice(InfoBarSeverity.Warning, Loc.Get("Share_Ask_OffWarning"));
    }

    private void OnRememberedToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Share.Coordinator.SetAllowRemembered(RememberedSwitch.IsOn);
    }
}
