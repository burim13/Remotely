using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Remotely.Core.Lan;
using Remotely.ViewerCore;
using Windows.ApplicationModel.DataTransfer;

namespace Remotely.App;

public sealed partial class HomePage : Page
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _tick;
    private CancellationTokenSource? _connectCts;

    public HomePage()
    {
        InitializeComponent();
        _tick = _ui.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => RefreshToken();
        _tick.Start();
        RefreshNetwork();
    }

    private void RefreshNetwork()
    {
        var addresses = LanNetwork.LocalAddresses();
        NetworkText.Text = addresses.Count == 0
            ? "This PC is not connected to a network."
            : "Same-network connections. This PC: " + string.Join(", ", addresses);
    }

    // ---- share ----

    private async void OnShareToggled(object sender, RoutedEventArgs e)
    {
        if (ShareSwitch.IsOn)
        {
            var error = App.Share.Start(_ => { });
            if (error is not null)
            {
                ShareSwitch.IsOn = false;
                ShowBar(ShareBar, InfoBarSeverity.Error, error);
                return;
            }
            var host = App.Share.Host!;
            host.TokenChanged += () => _ui.TryEnqueue(RefreshToken);
            host.ControllerConnected += (name, address) => _ui.TryEnqueue(() => ShowBar(ShareBar, InfoBarSeverity.Success, $"{name} ({address}) is connected to this PC."));
            host.ControllerLeft += () => _ui.TryEnqueue(() => { ShareBar.IsOpen = false; RefreshToken(); });
            host.Notice += text => _ui.TryEnqueue(() => ShowBar(ShareBar, InfoBarSeverity.Warning, text));
            TokenPanel.Visibility = Visibility.Visible;
            RefreshToken();
        }
        else
        {
            TokenPanel.Visibility = Visibility.Collapsed;
            ShareBar.IsOpen = false;
            await App.Share.StopAsync();
        }
    }

    private void RefreshToken()
    {
        var host = App.Share.Host;
        if (host is null) return;
        TokenText.Text = host.CurrentToken.Display;
        if (host.IsConnected) { TokenInfo.Text = "Someone is connected. A new token will appear when they leave."; return; }
        var left = host.TokenExpiresUtc - DateTimeOffset.UtcNow;
        TokenInfo.Text = left.TotalSeconds > 0
            ? $"Works once. Changes in {(int)left.TotalMinutes}:{left.Seconds:00}."
            : "Getting a new token…";
    }

    private void OnCopyToken(object sender, RoutedEventArgs e)
    {
        if (App.Share.Host is not { } host) return;
        var data = new DataPackage();
        data.SetText(host.CurrentToken.Display);
        Clipboard.SetContent(data);
    }

    private void OnNewToken(object sender, RoutedEventArgs e)
    {
        App.Share.Host?.NewToken();
        RefreshToken();
    }

    // ---- connect ----

    private void OnTokenKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) OnConnect(sender, e);
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_connectCts is not null) return;
        ConnectBar.IsOpen = false;
        _connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ConnectButton.IsEnabled = false;
        Busy.IsActive = true;
        try
        {
            var found = await LanConnector.ConnectAsync(TokenBox.Text, Environment.MachineName, AddressBox.Text, _connectCts.Token);
            ViewerSession session;
            try { session = await ViewerSession.OpenAsync(found.Channel, found.HostName, _connectCts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new LanConnectException("Connected, but the other PC did not start the session: " + ex.Message); }
            TokenBox.Text = "";
            new SessionWindow(session, string.IsNullOrEmpty(found.HostName) ? found.Endpoint.Address.ToString() : found.HostName).Activate();
        }
        catch (LanConnectException ex) { ShowBar(ConnectBar, InfoBarSeverity.Error, ex.Message); }
        catch (OperationCanceledException) { ShowBar(ConnectBar, InfoBarSeverity.Error, "The other PC did not answer in time. Check the token and that both PCs are on the same network."); }
        finally
        {
            _connectCts.Dispose();
            _connectCts = null;
            Busy.IsActive = false;
            ConnectButton.IsEnabled = true;
        }
    }

    private static void ShowBar(InfoBar bar, InfoBarSeverity severity, string text)
    {
        bar.Severity = severity;
        bar.Message = text;
        bar.IsOpen = true;
    }
}
