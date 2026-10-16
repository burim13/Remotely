using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Client;
using Windows.ApplicationModel.DataTransfer;

namespace Remotely.App;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        var s = App.Settings;
        DeviceName.Text = s.DeviceName;
        SyncClipboard.IsOn = s.SyncClipboard;
        foreach (ComboBoxItem item in Theme.Items) if ((string)item.Tag == s.Theme) Theme.SelectedItem = item;
        Theme.SelectedItem ??= Theme.Items[0];
        Server.Text = s.ServerUri;
        Pin.Text = s.ServerPin;
        ServerState.Text = s.IsConfigured ? "Connected to " + s.ServerUri : "No server yet. Connections on this network do not need one.";
        IdText.Text = App.Connection.Identity.DeviceId;
        VersionText.Text = "Remotely " + (typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "");
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.SyncClipboard = SyncClipboard.IsOn;
        s.Theme = (string)((ComboBoxItem)Theme.SelectedItem).Tag;
        if (App.Main is { } main) App.ApplyTheme(main);

        if (!string.IsNullOrWhiteSpace(Invite.Text))
        {
            Begin("Connecting…");
            var (ok, message) = await Join.WithInviteAsync(Invite.Text, DeviceName.Text);
            if (ok) { s.Save(); Invite.Text = ""; ServerState.Text = "Connected to " + s.ServerUri; }
            End(message, ok);
            return;
        }
        if (!string.IsNullOrWhiteSpace(Server.Text) && (Server.Text.Trim() != s.ServerUri || !string.IsNullOrWhiteSpace(Code.Text)))
        {
            var server = Server.Text.Trim();
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("wss" or "ws"))
            {
                End("Enter the server address, starting with wss://", false);
                return;
            }
            if (uri.Scheme == "wss" && Pin.Text.Trim().Length != 64)
            {
                End("The server key pin is 64 characters. Copy it from the server's start-up message.", false);
                return;
            }
            s.ServerUri = server;
            s.ServerPin = Pin.Text.Trim().ToLowerInvariant();
            s.DeviceName = string.IsNullOrWhiteSpace(DeviceName.Text) ? Environment.MachineName : DeviceName.Text.Trim();
            s.AllowInsecureTransport = uri.Scheme == "ws";
            s.Save();
            Begin("Connecting…");
            try
            {
                await App.Connection.PairAsync(Code.Text.Trim());
                Code.Text = "";
                ServerState.Text = "Connected to " + s.ServerUri;
                End("Connected. This device is paired with your server.", true);
            }
            catch (Exception ex) when (ex is CoordinatorException or IOException or System.Net.WebSockets.WebSocketException or HttpRequestException or TimeoutException or OperationCanceledException)
            {
                End(ex.Message.Contains("pairing", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("code", StringComparison.OrdinalIgnoreCase)
                    ? ex.Message : "Could not connect: " + ex.Message, false);
            }
            return;
        }
        s.DeviceName = string.IsNullOrWhiteSpace(DeviceName.Text) ? Environment.MachineName : DeviceName.Text.Trim();
        s.Save();
        End("Saved.", true);
    }

    private void Begin(string text)
    {
        SaveButton.IsEnabled = false;
        Busy.IsActive = true;
        Bar.Severity = InfoBarSeverity.Informational;
        Bar.Message = text;
        Bar.IsOpen = true;
    }

    private void End(string text, bool ok)
    {
        SaveButton.IsEnabled = true;
        Busy.IsActive = false;
        Bar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        Bar.Message = text;
        Bar.IsOpen = true;
    }

    private void OnCopyId(object sender, RoutedEventArgs e)
    {
        var data = new DataPackage();
        data.SetText(App.Connection.Identity.DeviceId);
        Clipboard.SetContent(data);
    }
}
