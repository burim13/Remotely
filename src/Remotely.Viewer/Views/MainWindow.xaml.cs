using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;

namespace Remotely.Viewer.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        Shell.WindowFrame.Round(this);
        ThisDevice.Text = $"This device: {App.Current.Settings.DeviceName} · {DeviceIds.Short(App.Current.Connection.Identity.DeviceId)}";
        Loaded += async (_, _) =>
        {
            if (App.Current.InviteOnStart is { Length: > 0 } invite)
            {
                var (ok, message) = await Join.WithInviteAsync(invite);
                if (!ok) { BannerText.Text = message; Banner.Visibility = Visibility.Visible; }
            }
            if (!App.Current.Settings.IsConfigured) { JoinPanel.Visibility = Visibility.Visible; InviteBox.Focus(); return; }
            if (App.Current.PairOnStart is { Length: > 0 } code)
            {
                var (ok, message) = await Join.PairAsync(code);
                if (!ok) { BannerText.Text = "Pairing failed: " + message; Banner.Visibility = Visibility.Visible; }
            }
            await RefreshAsync();
            if (App.Current.ConnectOnStart is { Length: > 0 } wanted && Devices.ItemsSource is IEnumerable<Row> rows
                && rows.FirstOrDefault(r => r.CanConnect && r.DeviceId.StartsWith(wanted.Replace("-", ""), StringComparison.OrdinalIgnoreCase)) is { } match)
                new SessionWindow(match.Device).Show();
        };
        _timer.Tick += async (_, _) => { if (IsActive) await RefreshAsync(); };
        _timer.Start();
    }

    private sealed class Row(DeviceInfo device, bool self)
    {
        public string DeviceId { get; } = device.DeviceId;
        public string Name { get; } = device.Name;
        public bool CanConnect { get; } = device.Online && device.CanHost && !self;
        public string Subtitle { get; } = self ? "This PC" : device.Online ? "Online" : "Offline";
        public Brush StatusBrush { get; } = device.Online ? (Brush)Application.Current.FindResource("Brush.Connected") : (Brush)Application.Current.FindResource("Brush.Hairline");
        public Visibility ConnectVisibility { get; } = device.Online && !self ? Visibility.Visible : Visibility.Collapsed;
        public DeviceInfo Device { get; } = device;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || !App.Current.Settings.IsConfigured) return;
        _refreshing = true;
        try
        {
            var client = await App.Current.Connection.EnsureAsync();
            var all = await client.ListDevicesAsync();
            var self = App.Current.Connection.Identity.DeviceId;
            var rows = all.Where(d => d.CanHost || d.DeviceId == self)
                .OrderByDescending(d => d.Online).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => new Row(d, d.DeviceId == self)).ToList();
            Devices.ItemsSource = rows;
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Banner.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is CoordinatorException or IOException or System.Net.WebSockets.WebSocketException or HttpRequestException or TimeoutException or UriFormatException or OperationCanceledException)
        {
            BannerText.Text = ex is CoordinatorException ? ex.Message : "Can't reach your server: " + ex.Message;
            Banner.Visibility = Visibility.Visible;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is Row { CanConnect: true } row)
            new SessionWindow(row.Device).Show();
    }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        JoinButton.IsEnabled = false;
        ShowJoin("Connecting…", error: false);
        var (ok, message) = await Join.WithInviteAsync(InviteBox.Text);
        JoinButton.IsEnabled = true;
        ShowJoin(message, error: !ok);
        if (!ok) return;
        JoinPanel.Visibility = Visibility.Collapsed;
        ThisDevice.Text = $"This device: {App.Current.Settings.DeviceName} · {DeviceIds.Short(App.Current.Connection.Identity.DeviceId)}";
        await RefreshAsync();
    }

    private void ShowJoin(string text, bool error)
    {
        JoinMessage.Text = text;
        JoinMessage.Foreground = (Brush)FindResource(error ? "Brush.Error" : "Brush.Connected");
        JoinMessage.Visibility = Visibility.Visible;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var window = new SetupWindow { Owner = this };
        window.ShowDialog();
        ThisDevice.Text = $"This device: {App.Current.Settings.DeviceName} · {DeviceIds.Short(App.Current.Connection.Identity.DeviceId)}";
        if (App.Current.Settings.IsConfigured) JoinPanel.Visibility = Visibility.Collapsed;
        _ = RefreshAsync();
    }

    private void OnCopyId(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(App.Current.Connection.Identity.DeviceId); ThisDevice.Text = "Device id copied."; }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy: user can click again */ }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
