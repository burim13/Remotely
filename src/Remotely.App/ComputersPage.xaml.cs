using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;
using Remotely.ViewerCore;

namespace Remotely.App;

/// <summary>The server-based side: computers paired with your own server, reachable from any network.</summary>
public sealed partial class ComputersPage : Page
{
    private readonly DispatcherQueueTimer _timer;
    private bool _refreshing;
    private bool _connecting;

    public ComputersPage()
    {
        InitializeComponent();
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(10);
        _timer.Tick += async (_, _) => { if (Visibility == Visibility.Visible && XamlRoot is not null) await RefreshAsync(); };
        _timer.Start();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        JoinPanel.Visibility = App.Settings.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        if (!App.Settings.IsConfigured) { List.Children.Clear(); EmptyText.Visibility = Visibility.Collapsed; return; }
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var client = await App.Connection.EnsureAsync();
            var all = await client.ListDevicesAsync();
            var self = App.Connection.Identity.DeviceId;
            var rows = all.Where(d => d.CanHost || d.DeviceId == self)
                .OrderByDescending(d => d.Online).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            List.Children.Clear();
            foreach (var d in rows) List.Children.Add(BuildRow(d, d.DeviceId == self));
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_connecting) Bar.IsOpen = false;
        }
        catch (Exception ex) when (ex is CoordinatorException or IOException or System.Net.WebSockets.WebSocketException or HttpRequestException or TimeoutException or UriFormatException or OperationCanceledException)
        {
            Show(ex is CoordinatorException ? ex.Message : "Can't reach your server: " + ex.Message);
        }
        finally { _refreshing = false; }
    }

    private UIElement BuildRow(DeviceInfo device, bool self)
    {
        var canConnect = device.Online && device.CanHost && !self;
        var subtitle = self ? "This PC" : device.Online ? "Online" : "Offline";
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
            Fill = device.Online ? Palette.Connected(this) : Palette.Offline(this),
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = device.Name, FontWeight = Microsoft.UI.Text.FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = subtitle, Opacity = 0.7, FontSize = 12 });
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(dot);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (canConnect)
        {
            var go = new TextBlock { Text = "Connect", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            go.Foreground = Palette.AccentText(this);
            Grid.SetColumn(go, 2);
            grid.Children.Add(go);
        }
        var button = new Button
        {
            Content = grid, IsEnabled = canConnect, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(8),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, device.Name);
        button.Click += async (_, _) => await ConnectAsync(device);
        return button;
    }

    private async Task ConnectAsync(DeviceInfo device)
    {
        if (_connecting) return;
        _connecting = true;
        Show($"Connecting to {device.Name}…", InfoBarSeverity.Informational);
        try
        {
            var client = await App.Connection.EnsureAsync();
            var session = await ViewerSession.OpenAsync(client, App.Connection.Identity, device.DeviceId);
            Bar.IsOpen = false;
            new SessionWindow(session, device.Name).Activate();
        }
        catch (Exception ex) when (ex is CoordinatorException or HandshakeException or IOException or TimeoutException or System.Net.WebSockets.WebSocketException or HttpRequestException or OperationCanceledException)
        {
            Show(Friendly(ex));
        }
        finally { _connecting = false; }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        HandshakeException h when h.Message.Contains("not trusted", StringComparison.OrdinalIgnoreCase)
            => "That PC does not trust this device yet. On that PC, run:  Remotely.Host trust " + App.Connection.Identity.DeviceId,
        CoordinatorException c => c.Message,
        _ => ex.Message,
    };

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        JoinButton.IsEnabled = false;
        JoinBusy.IsActive = true;
        var (ok, message) = await Join.WithInviteAsync(InviteBox.Text);
        JoinBusy.IsActive = false;
        JoinButton.IsEnabled = true;
        JoinMessage.Text = message;
        JoinMessage.Foreground = ok ? Palette.Connected(this) : Palette.Error(this);
        JoinMessage.Visibility = Visibility.Visible;
        if (ok) await RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Show(string text, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        Bar.Severity = severity;
        Bar.Message = text;
        Bar.IsOpen = true;
    }
}
