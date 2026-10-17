using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Client;
using Remotely.Core.Storage;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Settings;

/// <summary>Join a server with an invite or by hand (moved here from the old single Settings page). Hidden behind a message when the DisableServerMode policy is on.</summary>
internal static class ServerBlock
{
    public static UIElement Create(PolicyOverrides policy)
    {
        var title = SettingsUi.Subtitle(Loc.Get("Settings_Server_Title"));
        if (policy.DisableServerMode)
            return SettingsUi.CardOf(title, SettingsUi.Secondary(Loc.Get("Settings_Server_Disabled")), SettingsUi.Secondary(Loc.Get("Settings_PolicyManaged")));

        var s = App.Settings;
        var state = SettingsUi.Secondary(StateText());
        var invite = new TextBox { Header = Loc.Get("Settings_Server_Invite"), PlaceholderText = Loc.Get("Settings_Server_InvitePlaceholder") };
        var server = new TextBox { Header = Loc.Get("Settings_Server_Address"), PlaceholderText = "wss://server.example:8443", Text = s.ServerUri };
        var pin = new TextBox { Header = Loc.Get("Settings_Server_Pin"), Text = s.ServerPin };
        var code = new TextBox { Header = Loc.Get("Settings_Server_Code"), PlaceholderText = "ABCD-EFGH" };
        var bar = new InfoBar { IsOpen = false, IsClosable = false };
        var busy = new ProgressRing { IsActive = false, Width = 24, Height = 24 };
        var connect = new Button { Content = Loc.Get("Settings_Server_Connect"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };

        void Begin()
        {
            connect.IsEnabled = false;
            busy.IsActive = true;
            bar.Severity = InfoBarSeverity.Informational;
            bar.Message = Loc.Get("Settings_Server_Connecting");
            bar.IsOpen = true;
        }

        void End(string message, bool ok)
        {
            connect.IsEnabled = true;
            busy.IsActive = false;
            bar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            bar.Message = message;
            bar.IsOpen = true;
            state.Text = StateText();
        }

        connect.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(invite.Text))
            {
                Begin();
                var (ok, message) = await Join.WithInviteAsync(invite.Text, s.DeviceName);
                if (ok) { invite.Text = ""; server.Text = s.ServerUri; pin.Text = s.ServerPin; }
                End(message, ok);
                return;
            }
            var address = server.Text.Trim();
            if (address.Length == 0) { End(Loc.Get("Settings_Server_NothingToDo"), false); return; }
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("wss" or "ws")) { End(Loc.Get("Settings_Server_BadAddress"), false); return; }
            if (uri.Scheme == "wss" && pin.Text.Trim().Length != 64) { End(Loc.Get("Settings_Server_BadPin"), false); return; }
            s.ServerUri = address;
            s.ServerPin = pin.Text.Trim().ToLowerInvariant();
            s.AllowInsecureTransport = uri.Scheme == "ws";
            try { s.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { End(Loc.Get("Settings_Status_SaveFailed"), false); return; }
            Begin();
            try
            {
                await App.Connection.PairAsync(code.Text.Trim());
                code.Text = "";
                End(Loc.Get("Settings_Server_Connected"), true);
            }
            catch (Exception ex) when (ex is CoordinatorException or IOException or System.Net.WebSockets.WebSocketException or HttpRequestException or TimeoutException or OperationCanceledException)
            {
                End(SettingsUi.Format("Settings_Server_Failed", ex.Message), false);
            }
        };

        var manual = new Expander
        {
            Header = Loc.Get("Settings_Server_Manual"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 10, Children = { server, pin, code } },
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { connect, busy } };
        return SettingsUi.CardOf(title, state, invite, manual, actions, bar);
    }

    private static string StateText() => App.Settings.IsConfigured
        ? SettingsUi.Format("Settings_Server_State", App.Settings.ServerUri)
        : Loc.Get("Settings_Server_None");
}
