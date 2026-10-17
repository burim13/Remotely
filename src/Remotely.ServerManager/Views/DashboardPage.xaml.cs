using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Remotely.Core.ServerManager;

namespace Remotely.ServerManager.Views;

public sealed partial class DashboardPage : Page
{
    private readonly InfoBar _bar = new() { IsOpen = false, IsClosable = false };
    private readonly Border _dotHost = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _headline = Ui.Text("", "SubtitleTextBlockStyle");
    private readonly TextBlock _detail = Ui.Secondary("");
    private readonly TextBlock _online = Ui.Text("–", size: 28, semiBold: true);
    private readonly TextBlock _onlineSub = Ui.Secondary("");
    private readonly TextBlock _sessions = Ui.Text("–", size: 28, semiBold: true);
    private readonly TextBlock _address = Ui.Text("–", size: 20, semiBold: true);
    private readonly TextBlock _addressSub = Ui.Secondary("");
    private readonly TextBlock _cert = Ui.Text("–", size: 20, semiBold: true);
    private readonly TextBlock _certSub = Ui.Secondary("");
    private readonly TextBlock _reach = Ui.Secondary("");
    private readonly TextBlock _pin = Ui.Text("–", "MonoTextStyle");
    private readonly StackPanel _recent = new() { Spacing = 6 };

    public DashboardPage()
    {
        InitializeComponent();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Ui.SmallButton(Ui.T("Common_Refresh"), async (_, _) => await RefreshAsync()));
        buttons.Children.Add(Ui.SmallButton(Ui.T("Dash_CopyInfo"), (_, _) => CopyInfo()));

        var head = new Grid { ColumnSpacing = 14 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(_dotHost);
        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(_headline);
        texts.Children.Add(_detail);
        Grid.SetColumn(texts, 1);
        head.Children.Add(texts);
        Grid.SetColumn(buttons, 2);
        head.Children.Add(buttons);

        var tiles = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddTile(tiles, 0, 0, Ui.T("Dash_DevicesOnline"), _online, _onlineSub);
        AddTile(tiles, 0, 1, Ui.T("Dash_Sessions"), _sessions, null);
        AddTile(tiles, 1, 0, Ui.T("Dash_Address"), _address, _addressSub);
        AddTile(tiles, 1, 1, Ui.T("Dash_Certificate"), _cert, _certSub);

        var reachPanel = new StackPanel { Spacing = 6 };
        reachPanel.Children.Add(Ui.Text(Ui.T("Dash_Reach"), "BodyStrongTextBlockStyle"));
        reachPanel.Children.Add(_reach);
        reachPanel.Children.Add(Ui.Secondary(Ui.T("Dash_ReachOutside")));

        var pinPanel = new StackPanel { Spacing = 6 };
        pinPanel.Children.Add(Ui.Text(Ui.T("Dash_Pin"), "BodyStrongTextBlockStyle"));
        pinPanel.Children.Add(Ui.Secondary(Ui.T("Dash_PinHelp")));
        pinPanel.Children.Add(_pin);
        pinPanel.Children.Add(Ui.SmallButton(Ui.T("Dash_CopyPin"), (_, _) => { if (App.Monitor.Latest?.Status is { } s) Ui.CopyText(s.CertificatePin); }));

        var recentPanel = new StackPanel { Spacing = 8 };
        recentPanel.Children.Add(Ui.Text(Ui.T("Dash_Recent"), "BodyStrongTextBlockStyle"));
        recentPanel.Children.Add(_recent);

        Root.Children.Add(Ui.Text(Ui.T("Nav_Dashboard"), "TitleTextBlockStyle"));
        Root.Children.Add(_bar);
        Root.Children.Add(Ui.Card(head));
        Root.Children.Add(tiles);
        Root.Children.Add(Ui.Card(reachPanel));
        Root.Children.Add(Ui.Card(pinPanel));
        Root.Children.Add(Ui.Card(recentPanel));

        SetDot("DotNeutralStyle");
        Loaded += OnLoaded;
        Unloaded += (_, _) => App.SnapshotChanged -= OnSnapshot;
    }

    private static void AddTile(Grid grid, int row, int column, string label, TextBlock value, TextBlock? sub)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Ui.Text(label, "CaptionTextBlockStyle"));
        stack.Children.Add(value);
        if (sub is not null) stack.Children.Add(sub);
        var card = Ui.Card(stack);
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    private void SetDot(string styleKey)
    {
        var dot = Ui.Dot(styleKey);
        dot.Width = 16;
        dot.Height = 16;
        _dotHost.Child = dot;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.SnapshotChanged -= OnSnapshot;
        App.SnapshotChanged += OnSnapshot;
        if (App.Monitor.Latest is { } latest) Apply(latest);
        await RefreshAsync();
    }

    private void OnSnapshot(MonitorSnapshot snapshot) => Apply(snapshot);

    private async Task RefreshAsync()
    {
        await App.PollAsync();
        var snapshot = App.Monitor.Latest;
        if (snapshot?.Status is not { } status)
        {
            _recent.Children.Clear();
            return;
        }
        if (App.Args.Fake) _reach.Text = Ui.T("Dash_ReachFake");
        else
        {
            _reach.Text = Ui.T("Dash_ReachChecking");
            var verdict = await Reachability.CheckAsync(new TcpProbe(), status.Address, status.Port);
            _reach.Text = verdict switch
            {
                ReachabilityVerdict.AnswersAtAddress => Ui.T("Dash_ReachOk", status.Address, status.Port),
                ReachabilityVerdict.ThisPcOnly => Ui.T("Dash_ReachLocal", status.Address, status.Port),
                _ => Ui.T("Dash_ReachNone", status.Port),
            };
        }
        try
        {
            var names = snapshot.Devices.ToDictionary(d => d.DeviceId, d => d.Name);
            var entries = await App.Admin.GetAuditAsync(null, 6);
            _recent.Children.Clear();
            foreach (var entry in entries)
            {
                var who = names.TryGetValue(entry.DeviceId, out var n) ? n : DeviceView.ShortId(entry.DeviceId);
                _recent.Children.Add(Ui.Secondary($"{Ui.When(entry.TimeUtc)}   {Ui.Label("Kind_", entry.Kind)}   {who}   {Ui.Label("Result_", entry.Result)}"));
            }
            if (entries.Count == 0) _recent.Children.Add(Ui.Secondary(Ui.T("Dash_NoActivity")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _recent.Children.Clear();
            _recent.Children.Add(Ui.Secondary(Ui.Describe(ex)));
        }
    }

    private void Apply(MonitorSnapshot snapshot)
    {
        var headline = Headline.Of(snapshot);
        var (text, style) = headline switch
        {
            ServerHeadline.Running => (Ui.T("Dash_Running"), "DotGoodStyle"),
            ServerHeadline.NotAnswering => (Ui.T("Dash_NotAnswering"), "DotWarnStyle"),
            ServerHeadline.Stopped => (Ui.T("Dash_Stopped"), "DotBadStyle"),
            ServerHeadline.NotInstalled => (Ui.T("Dash_NotInstalled"), "DotNeutralStyle"),
            _ => (Ui.T("Dash_Unknown"), "DotNeutralStyle"),
        };
        _headline.Text = text;
        SetDot(style);

        if (snapshot.Status is { } s)
        {
            _bar.IsOpen = false;
            _detail.Text = Ui.T("Dash_Detail", s.Version, Ui.When(s.StartedUtc));
            _online.Text = s.DevicesOnline.ToString();
            _onlineSub.Text = Ui.T("Dash_OfTotal", s.DevicesTotal);
            _sessions.Text = s.SessionsActive.ToString();
            _address.Text = s.Address + ":" + s.Port;
            _addressSub.Text = s.TlsEnabled ? Ui.T("Dash_TlsOn") : Ui.T("Dash_TlsOff");
            if (s.TlsEnabled)
            {
                var health = ServerInfo.AssessCertificate(s.CertificateExpiresUtc, DateTimeOffset.UtcNow);
                _cert.Text = s.CertificateExpiresUtc.ToLocalTime().ToString("d");
                _certSub.Text = health switch
                {
                    CertificateHealth.Expired => Ui.T("Dash_CertExpired"),
                    CertificateHealth.ExpiringSoon => Ui.T("Dash_CertSoon", (int)(s.CertificateExpiresUtc - DateTimeOffset.UtcNow).TotalDays),
                    _ => Ui.T("Dash_CertOk"),
                };
                _pin.Text = s.CertificatePin;
            }
            else
            {
                _cert.Text = "–";
                _certSub.Text = Ui.T("Dash_TlsOff");
                _pin.Text = "–";
            }
        }
        else
        {
            _detail.Text = Ui.T("Dash_ServiceIs", Ui.T("Service_" + snapshot.Service));
            _online.Text = _sessions.Text = _address.Text = _cert.Text = "–";
            _onlineSub.Text = _addressSub.Text = _certSub.Text = "";
            _pin.Text = "–";
            if (snapshot.Error is { Length: > 0 } error) Ui.Show(_bar, InfoBarSeverity.Warning, error);
        }
    }

    private void CopyInfo()
    {
        var snapshot = App.Monitor.Latest;
        Ui.CopyText(ServerInfo.SupportText(snapshot?.Status, snapshot?.Service ?? ServiceState.Unknown,
            typeof(DashboardPage).Assembly.GetName().Version?.ToString(3) ?? "", Environment.OSVersion.VersionString));
        Ui.Show(_bar, InfoBarSeverity.Success, Ui.T("Dash_InfoCopied"));
    }
}
