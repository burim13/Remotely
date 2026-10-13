using System.Windows;
using System.Windows.Controls;
using Remotely.Client;

namespace Remotely.Viewer.Views;

public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        Shell.WindowFrame.Round(this);
        var s = App.Current.Settings;
        Server.Text = s.ServerUri;
        Pin.Text = s.ServerPin;
        DeviceName.Text = s.DeviceName;
        SyncClipboard.IsChecked = s.SyncClipboard;
        foreach (ComboBoxItem item in Theme.Items) if ((string)item.Tag == s.Theme) Theme.SelectedItem = item;
        if (Theme.SelectedItem is null) Theme.SelectedIndex = 0;
        IdText.Text = App.Current.Connection.Identity.DeviceId;
        IdCard.Visibility = Visibility.Visible;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var s = App.Current.Settings;
        var server = Server.Text.Trim();
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("wss" or "ws"))
        {
            Show("Enter the server address, starting with wss://", error: true);
            return;
        }
        if (uri.Scheme == "wss" && Pin.Text.Trim().Length != 64)
        {
            Show("The server key pin is 64 characters. Copy it from the server's start-up message.", error: true);
            return;
        }
        s.ServerUri = server;
        s.ServerPin = Pin.Text.Trim().ToLowerInvariant();
        s.DeviceName = string.IsNullOrWhiteSpace(DeviceName.Text) ? Environment.MachineName : DeviceName.Text.Trim();
        s.SyncClipboard = SyncClipboard.IsChecked == true;
        s.Theme = (string)((ComboBoxItem)Theme.SelectedItem).Tag;
        s.AllowInsecureTransport = uri.Scheme == "ws";
        s.Save();
        App.Current.ApplyTheme();

        SaveButton.IsEnabled = false;
        Show("Connecting…", error: false);
        try
        {
            await App.Current.Connection.PairAsync(Code.Text.Trim());
            Code.Text = "";
            Show("Connected. This device is paired with your server.", error: false);
            await Task.Delay(700);
            Close();
        }
        catch (Exception ex) when (ex is CoordinatorException or IOException or System.Net.WebSockets.WebSocketException or HttpRequestException or TimeoutException or OperationCanceledException)
        {
            Show(ex.Message.Contains("pairing", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("code", StringComparison.OrdinalIgnoreCase)
                ? ex.Message : "Could not connect: " + ex.Message, error: true);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void Show(string text, bool error)
    {
        Message.Text = text;
        Message.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Brush.Error" : "Brush.Connected");
        Message.Visibility = Visibility.Visible;
    }

    private void OnCopyId(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(App.Current.Connection.Identity.DeviceId); Show("Device id copied.", error: false); }
        catch (System.Runtime.InteropServices.COMException) { Show("The clipboard is busy; try again.", error: true); }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
