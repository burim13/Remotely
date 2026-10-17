using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Remotely.Core.Admin;

namespace Remotely.Core.ServerManager;

public enum CertificateHealth { Ok, ExpiringSoon, Expired }

public static class ServerInfo
{
    public const int CertificateWarningDays = 30;

    public static CertificateHealth AssessCertificate(DateTimeOffset expiresUtc, DateTimeOffset now) =>
        expiresUtc <= now ? CertificateHealth.Expired
        : expiresUtc - now <= TimeSpan.FromDays(CertificateWarningDays) ? CertificateHealth.ExpiringSoon
        : CertificateHealth.Ok;

    /// <summary>The text of "Copy server info" for support requests. Contains the public key pin (not a secret) and counts; never an invite, a pairing code or a device list.</summary>
    public static string SupportText(ServerStatus? status, ServiceState service, string managerVersion, string os)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Remotely Server Manager " + managerVersion);
        sb.AppendLine("System: " + os);
        sb.AppendLine("Service: " + service);
        if (status is null)
        {
            sb.AppendLine("Server: not reachable from the Manager");
            return sb.ToString();
        }
        sb.AppendLine("Server version: " + status.Version);
        sb.AppendLine("Started (UTC): " + status.StartedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.AppendLine("Address: " + status.Address + ":" + status.Port.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("TLS: " + (status.TlsEnabled ? "on" : "off"));
        if (status.TlsEnabled)
        {
            sb.AppendLine("Certificate pin (SHA-256): " + status.CertificatePin);
            sb.AppendLine("Certificate expires (UTC): " + status.CertificateExpiresUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        sb.AppendLine($"Devices: {status.DevicesTotal} paired, {status.DevicesOnline} online, {status.SessionsActive} active sessions");
        return sb.ToString();
    }
}

public sealed record SettingsProblem(string Field, string MessageKey);

public static class SettingsValidator
{
    /// <summary>Checks that the Settings page's values are sane before they are sent. The limits are the Manager's own; the server may enforce others and says so itself.</summary>
    public static IReadOnlyList<SettingsProblem> Validate(ServerSettings s)
    {
        var problems = new List<SettingsProblem>();
        var address = s.Address.Trim();
        if (address.Length == 0 || address.Length > 253 || address.Any(char.IsWhiteSpace) || address.Contains('/') || address.Contains("://", StringComparison.Ordinal))
            problems.Add(new SettingsProblem("Address", "Settings_BadAddress"));
        if (s.Port is < 1 or > 65535) problems.Add(new SettingsProblem("Port", "Settings_BadPort"));
        if (s.MaxDevices is < 1 or > 100000) problems.Add(new SettingsProblem("MaxDevices", "Settings_BadMaxDevices"));
        if (s.MaxSessionsPerDevice is < 1 or > 1000) problems.Add(new SettingsProblem("MaxSessions", "Settings_BadMaxSessions"));
        return problems;
    }
}

public enum ReachabilityVerdict { NotListening, ThisPcOnly, AnswersAtAddress }

public interface ITcpProbe
{
    Task<bool> CanConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default);
}

public sealed class TcpProbe : ITcpProbe
{
    public async Task<bool> CanConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException or NotSupportedException or ArgumentException) { return false; }
    }
}

/// <summary>
/// What the Dashboard can truthfully say about reachability from this PC: does something listen on the port here, and does it answer at the address devices are
/// told to use. It cannot say whether the internet can get in; that needs a test from outside, which the setup wizard explains.
/// </summary>
public static class Reachability
{
    public static async Task<ReachabilityVerdict> CheckAsync(ITcpProbe probe, string address, int port, CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromSeconds(3);
        if (!await probe.CanConnectAsync("127.0.0.1", port, timeout, ct).ConfigureAwait(false)
            && !await probe.CanConnectAsync("::1", port, timeout, ct).ConfigureAwait(false))
            return ReachabilityVerdict.NotListening;
        if (string.IsNullOrWhiteSpace(address)) return ReachabilityVerdict.ThisPcOnly;
        return await probe.CanConnectAsync(address.Trim(), port, timeout, ct).ConfigureAwait(false)
            ? ReachabilityVerdict.AnswersAtAddress
            : ReachabilityVerdict.ThisPcOnly;
    }
}
