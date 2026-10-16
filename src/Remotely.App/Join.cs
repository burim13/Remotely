using Remotely.Client;
using Remotely.Core.Protocol;

namespace Remotely.App;

/// <summary>"Join with an invite": read it, remember the server, pair this device. Used by Settings, the Computers page and the installer's --invite start-up option.</summary>
internal static class Join
{
    public static async Task<(bool Ok, string Message)> WithInviteAsync(string inviteText, string? deviceName = null)
    {
        if (!Invite.TryParse(inviteText, out var invite, out var problem)) return (false, problem);
        var s = App.Settings;
        var (uri, pin, insecure, name) = (s.ServerUri, s.ServerPin, s.AllowInsecureTransport, s.DeviceName);
        s.ServerUri = invite!.ServerUri;
        s.ServerPin = invite.PinHex;
        s.AllowInsecureTransport = invite.ServerUri.StartsWith("ws://", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(deviceName)) s.DeviceName = deviceName.Trim();
        s.Save();
        var result = await PairAsync(invite.Code);
        if (!result.Ok)
        {
            // Do not leave a half-set-up server behind: the join screen should come back next time.
            (s.ServerUri, s.ServerPin, s.AllowInsecureTransport, s.DeviceName) = (uri, pin, insecure, name);
            s.Save();
        }
        return result;
    }

    public static async Task<(bool Ok, string Message)> PairAsync(string code)
    {
        try
        {
            await App.Connection.PairAsync(code);
            return (true, "Connected. This device is paired with your server.");
        }
        catch (Exception ex) when (ex is CoordinatorException or IOException or System.Net.WebSockets.WebSocketException or HttpRequestException or TimeoutException or OperationCanceledException or UriFormatException)
        {
            var text = ex.Message;
            return (false, text.Contains("pairing", StringComparison.OrdinalIgnoreCase) || text.Contains("code", StringComparison.OrdinalIgnoreCase)
                ? text + " Ask the server for a fresh invite." : "Could not reach your server: " + text);
        }
    }
}
