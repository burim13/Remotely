using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Remotely.Core.Lan;

public enum DiagnosisStep { Adapter, Subnet, DiscoveryReply, ConnectionPort }
public enum StepState { Passed, Failed, Skipped }
public enum TcpOutcome { Connected, Refused, TimedOut, Unreachable }

public sealed record DiagnosisStepResult(DiagnosisStep Step, StepState State, string Message);

/// <summary>The outcome of a diagnosis: every step, which one failed first (if any), and a sentence for the person.</summary>
public sealed record DiagnosisReport(IReadOnlyList<DiagnosisStepResult> Steps, DiagnosisStep? FailedStep, string Summary)
{
    public bool Succeeded => FailedStep is null;
}

public sealed record AdapterInfo(string Name, IPAddress? IPv4, IPAddress? Mask, bool HasIPv6);

/// <summary>What the diagnosis needs from the machine and the network, so it can be tested with fakes.</summary>
public interface IDiagnosisEnvironment
{
    IReadOnlyList<AdapterInfo> Adapters();
    /// <summary>Sends discovery packets and returns every PC that answered. With a label, only a PC showing that label answers.</summary>
    Task<IReadOnlyList<LanDiscovery.Nearby>> DiscoverAsync(string? label, CancellationToken ct);
    Task<TcpOutcome> TryTcpAsync(IPEndPoint endpoint, CancellationToken ct);
}

/// <summary>
/// Works out why a same-network connection fails by checking, in order: a network adapter with an IPv4 address, the same subnet, a reply to discovery (UDP),
/// and the connection port (TCP). Reports the first step that failed in plain English.
/// </summary>
public sealed class LanDiagnosis(IDiagnosisEnvironment environment)
{
    /// <param name="address">What was typed in the address box ("192.168.1.20" or "192.168.1.20:47810"), or null when only a token was used.</param>
    /// <param name="label">The first 4 characters of the token, or null.</param>
    public async Task<DiagnosisReport> RunAsync(string? address, string? label, CancellationToken ct)
    {
        var steps = new List<DiagnosisStepResult>();
        void Add(DiagnosisStep step, StepState state, string message) => steps.Add(new DiagnosisStepResult(step, state, message));
        const string NotChecked = "Not checked, because an earlier step failed.";

        // 1. Adapter
        var adapters = environment.Adapters();
        var usable = adapters.Where(a => a.IPv4 is not null && a.Mask is not null && !IsSelfAssigned(a.IPv4)).ToList();
        if (usable.Count == 0)
        {
            var text = adapters.Count == 0
                ? "This PC is not connected to a network. Connect to Wi-Fi or plug in a network cable, then try again."
                : adapters.Any(a => a.HasIPv6)
                    ? "This PC has only an IPv6 network address. Same-network connections need IPv4, so no other PC can be found. Use a network that gives out IPv4 addresses."
                    : "This PC's network connection has no usable address yet (it has given itself a 169.254 address, which means no router answered). Check the router or the Wi-Fi.";
            Add(DiagnosisStep.Adapter, StepState.Failed, text);
            Add(DiagnosisStep.Subnet, StepState.Skipped, NotChecked);
            Add(DiagnosisStep.DiscoveryReply, StepState.Skipped, NotChecked);
            Add(DiagnosisStep.ConnectionPort, StepState.Skipped, NotChecked);
            return new DiagnosisReport(steps, DiagnosisStep.Adapter, text);
        }
        Add(DiagnosisStep.Adapter, StepState.Passed, $"This PC is on the network as {string.Join(", ", usable.Select(a => $"{a.IPv4} ({a.Name})"))}.");

        // 2. Same subnet
        IPEndPoint? typed = null;
        var subnetFailed = false;
        if (!string.IsNullOrWhiteSpace(address))
        {
            var trimmed = address.Trim();
            if (!TryParseIPv4Endpoint(trimmed, out typed))
            {
                var ipv6 = trimmed.Count(c => c == ':') > 1 || trimmed.StartsWith('[');
                Add(DiagnosisStep.Subnet, StepState.Failed, ipv6
                    ? "That is an IPv6 address. Same-network connections use IPv4 addresses, which look like 192.168.1.20."
                    : "That address is not valid. It looks like 192.168.1.20.");
                subnetFailed = true;
            }
            else
            {
                var same = usable.FirstOrDefault(a => SameSubnet(a.IPv4!, a.Mask!, typed.Address));
                if (same is null)
                {
                    Add(DiagnosisStep.Subnet, StepState.Failed, $"{typed.Address} is not on the same network as this PC ({string.Join(", ", usable.Select(a => a.IPv4))}). Both PCs must be on the same Wi-Fi or router, not a guest network or a different one.");
                    subnetFailed = true;
                }
                else Add(DiagnosisStep.Subnet, StepState.Passed, $"{typed.Address} is on the same network as this PC ({same.Name}).");
            }
        }
        else Add(DiagnosisStep.Subnet, StepState.Skipped, "No address was typed, so there is nothing to compare. The token's label is searched for on every network connection of this PC.");

        // 3. Reply to discovery (UDP)
        IPEndPoint? endpoint = typed;
        var udpPassed = false;
        var udpFailed = false;
        if (subnetFailed) Add(DiagnosisStep.DiscoveryReply, StepState.Skipped, NotChecked);
        else
        {
            var replies = await environment.DiscoverAsync(string.IsNullOrWhiteSpace(label) ? null : label, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var relevant = typed is null ? replies : [.. replies.Where(r => r.Endpoint.Address.Equals(typed.Address))];
            if (relevant.Count > 0)
            {
                udpPassed = true;
                endpoint = relevant[0].Endpoint;
                Add(DiagnosisStep.DiscoveryReply, StepState.Passed, relevant.Count == 1
                    ? $"{Describe(relevant[0])} answered."
                    : $"{relevant.Count} PCs answered: {string.Join(", ", relevant.Select(Describe))}.");
            }
            else
            {
                udpFailed = true;
                var who = typed is not null ? $"The PC at {typed.Address}" : label is not null ? $"No PC showing {label}" : "No PC that is sharing";
                Add(DiagnosisStep.DiscoveryReply, StepState.Failed,
                    $"{who} did not answer the search on this network. Check that the other PC is showing a token, that Windows Firewall there allows Remotely (UDP port {LanNetwork.DiscoveryPort}), " +
                    "and that the Wi-Fi does not isolate devices from each other (common on guest networks and some public Wi-Fi).");
            }
        }

        // 4. Connection port (TCP)
        var tcpPassed = false;
        if (endpoint is null) Add(DiagnosisStep.ConnectionPort, StepState.Skipped, NotChecked);
        else
        {
            var outcome = await environment.TryTcpAsync(endpoint, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            switch (outcome)
            {
                case TcpOutcome.Connected:
                    tcpPassed = true;
                    Add(DiagnosisStep.ConnectionPort, StepState.Passed, $"{endpoint} accepted a connection.");
                    break;
                case TcpOutcome.Refused:
                    Add(DiagnosisStep.ConnectionPort, StepState.Failed, $"The PC at {endpoint.Address} is on the network but nothing is accepting connections on port {endpoint.Port}. Check that Remotely is open there with sharing turned on.");
                    break;
                case TcpOutcome.TimedOut:
                    Add(DiagnosisStep.ConnectionPort, StepState.Failed, udpPassed
                        ? $"The PC at {endpoint.Address} answered the search but not the connection on port {endpoint.Port}. This usually means Windows Firewall on that PC blocks the port. Remotely adds a rule for it when installed; check that the rule \"Remotely same-network (TCP)\" still exists and is enabled."
                        : $"The PC at {endpoint.Address} did not answer on port {endpoint.Port}. It may be switched off, asleep, on another network, or a firewall may be blocking it.");
                    break;
                default:
                    Add(DiagnosisStep.ConnectionPort, StepState.Failed, $"This PC cannot reach {endpoint.Address} at all. Check that both PCs are on the same network.");
                    break;
            }
        }

        var failed = steps.FirstOrDefault(s => s.State == StepState.Failed);
        if (failed is null)
            return new DiagnosisReport(steps, null, "Everything on the network checks out. If connecting still fails, the token may have been used already or expired: use the token the other PC shows now.");
        if (udpFailed && tcpPassed)
            return new DiagnosisReport(steps, DiagnosisStep.DiscoveryReply, "The PC can be reached by its address, but it does not answer the search for tokens (the network may block broadcasts). Enter its address next to the token to connect.");
        return new DiagnosisReport(steps, failed.Step, failed.Message);
    }

    private static string Describe(LanDiscovery.Nearby n) => $"{(string.IsNullOrEmpty(n.HostName) ? n.Endpoint.Address.ToString() : n.HostName)} ({n.Id})";

    private static bool IsSelfAssigned(IPAddress address) => address.GetAddressBytes() is [169, 254, ..];

    private static bool SameSubnet(IPAddress local, IPAddress mask, IPAddress other)
    {
        if (other.AddressFamily != AddressFamily.InterNetwork) return false;
        var a = local.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var b = other.GetAddressBytes();
        for (var i = 0; i < 4; i++) if ((a[i] & m[i]) != (b[i] & m[i])) return false;
        return true;
    }

    private static bool TryParseIPv4Endpoint(string text, out IPEndPoint endpoint)
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

/// <summary>The real machine: network cards, UDP discovery and TCP connect.</summary>
public sealed class SystemDiagnosisEnvironment(int discoveryPort = LanNetwork.DiscoveryPort, IReadOnlyList<IPEndPoint>? extraDiscoveryTargets = null) : IDiagnosisEnvironment
{
    public IReadOnlyList<AdapterInfo> Adapters()
    {
        var result = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var addresses = nic.GetIPProperties().UnicastAddresses;
            var v4 = addresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask is not null);
            var v6 = addresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal);
            result.Add(new AdapterInfo(nic.Name, v4?.Address, v4?.IPv4Mask, v6));
        }
        return result;
    }

    public async Task<IReadOnlyList<LanDiscovery.Nearby>> DiscoverAsync(string? label, CancellationToken ct)
    {
        if (label is { Length: LanToken.IdLength })
        {
            var found = await LanDiscovery.FindAsync(label, TimeSpan.FromSeconds(3), ct, extraDiscoveryTargets, discoveryPort).ConfigureAwait(false);
            return found is null ? [] : [new LanDiscovery.Nearby(label, found.Endpoint, found.HostName)];
        }
        return await LanDiscovery.ListAsync(TimeSpan.FromSeconds(3), ct, extraDiscoveryTargets, discoveryPort).ConfigureAwait(false);
    }

    public async Task<TcpOutcome> TryTcpAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            return TcpOutcome.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return TcpOutcome.TimedOut; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return TcpOutcome.Refused; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { return TcpOutcome.TimedOut; }
        catch (SocketException) { return TcpOutcome.Unreachable; }
    }
}
