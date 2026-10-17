using System.Net;
using System.Net.Sockets;
using Remotely.Core.Crypto;

namespace Remotely.Core.Lan;

public sealed class LanConnectException(string message) : Exception(message);

/// <summary>Where a connection attempt is, for a progress display.</summary>
public enum LanConnectStep { FindingPc, Reaching, Verifying, WaitingForApproval }

/// <summary>The PC that types the token: finds the other PC on the network from the token's label and opens the encrypted connection.</summary>
public static class LanConnector
{
    public sealed record Result(IMessageChannel Channel, string HostName, IPEndPoint Endpoint);

    /// <param name="hostAddress">Optional address of the other PC ("192.168.1.20" or "192.168.1.20:47810"), for networks that block discovery.</param>
    public static async Task<Result> ConnectAsync(string tokenText, string controllerName, string? hostAddress, CancellationToken ct,
        IReadOnlyList<IPEndPoint>? extraDiscoveryTargets = null, int discoveryPort = LanNetwork.DiscoveryPort, TimeSpan? discoveryTimeout = null,
        LanDiscovery.Nearby? knownPc = null, IProgress<LanConnectStep>? progress = null)
    {
        if (!LanToken.TryParse(tokenText, out var token, out var problem)) throw new LanConnectException(problem);

        IPEndPoint endpoint;
        var hostName = "";
        if (!string.IsNullOrWhiteSpace(hostAddress))
        {
            var text = hostAddress.Trim();
            if (LooksLikeIPv6(text)) throw new LanConnectException("That is an IPv6 address. Same-network connections use IPv4 addresses, which look like 192.168.1.20. Check that both PCs have an IPv4 address.");
            if (!TryParseEndpoint(text, out endpoint)) throw new LanConnectException("That address is not valid. It looks like 192.168.1.20.");
        }
        else if (knownPc is not null && knownPc.Id == token!.Id)
        {
            endpoint = knownPc.Endpoint;
            hostName = knownPc.HostName;
        }
        else
        {
            if (extraDiscoveryTargets is null && LanNetwork.LocalAddresses().Count == 0)
                throw new LanConnectException(HasOnlyIPv6()
                    ? "This PC has only an IPv6 network address. Same-network connections need IPv4, so the other PC cannot be found. Use a network that gives out IPv4 addresses."
                    : "This PC is not connected to a network, so the other PC cannot be found.");
            progress?.Report(LanConnectStep.FindingPc);
            var found = await LanDiscovery.FindAsync(token!.Id, discoveryTimeout ?? TimeSpan.FromSeconds(4), ct, extraDiscoveryTargets, discoveryPort).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();   // FindAsync reports a cancel as "nobody found"; a cancel must stay a cancel
            if (found is null) throw new LanConnectException($"No PC on this network is showing a token starting {token.Id}. Check the token, that the other PC is showing it, and that both PCs are on the same network.");
            endpoint = found.Endpoint;
            hostName = found.HostName;
        }

        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            progress?.Report(LanConnectStep.Reaching);
            await client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            progress?.Report(LanConnectStep.Verifying);
            var wire = new FramedStream(client.GetStream());
            var session = await LanSecureSession.InitiateAsync(wire, token!, controllerName, timeout.Token).ConfigureAwait(false);
            return new Result(session, hostName, endpoint);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new LanConnectException($"Could not reach the PC at {endpoint.Address}. Is it still showing the token, and on the same network?");
        }
        catch (Exception ex) when (ex is LanAuthException or HandshakeException)
        {
            client.Dispose();
            throw new LanConnectException("That PC did not accept the token. It may have been used already, expired or mistyped. Look at the token it shows now.");
        }
        catch { client.Dispose(); throw; }
    }

    private static bool LooksLikeIPv6(string text) =>
        text.StartsWith('[') || text.Count(c => c == ':') > 1 || (IPAddress.TryParse(text, out var a) && a.AddressFamily == AddressFamily.InterNetworkV6);

    private static bool HasOnlyIPv6() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n =>
            n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
            && n.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal));

    private static bool TryParseEndpoint(string text, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.None, LanNetwork.TcpPort);
        var parts = text.Split(':');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork) return false;
        var port = LanNetwork.TcpPort;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out port) || port is < 1 or > 65535)) return false;
        endpoint = new IPEndPoint(address, port);
        return true;
    }
}
