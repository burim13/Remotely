using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Remotely.Core.Lan;

namespace Remotely.Core.Diagnostics;

public enum CheckStatus { Passed, Failed, Note, Skipped }

/// <summary>One line of the Network test. <see cref="Id"/> is adapter, subnet, discovery, port or server.</summary>
public sealed record NetworkCheck(string Id, CheckStatus Status, string Message, ErrorInfo? Error = null, double? LatencyMs = null);

public sealed record NetworkTestReport(IReadOnlyList<NetworkCheck> Checks)
{
    public bool Passed => Checks.All(c => c.Status != CheckStatus.Failed);
    public NetworkCheck? FirstFailure => Checks.FirstOrDefault(c => c.Status == CheckStatus.Failed);
}

public sealed record ServerProbeResult(string Host, int Port, bool Reachable, IReadOnlyList<double> LatenciesMs, string Message, ErrorInfo? Error);

/// <summary>Measures whether the server's address can be reached and how long a connection takes. Replaceable, so the test can run without a network.</summary>
public interface IServerProbe
{
    Task<ServerProbeResult> ProbeAsync(string serverAddress, int attempts, CancellationToken ct);
}

/// <summary>
/// The in-app Network test. The same-network half is <see cref="LanDiagnosis"/> (adapter, same subnet, UDP discovery reply, TCP port), unchanged; the server half opens a TCP
/// connection to the server's address a few times and reports the time it took. It sends nothing but the TCP handshake: no data, no login, no identity. This is the only
/// feature of the diagnostics that touches the network, and it runs only when a person starts it.
/// </summary>
public sealed class NetworkTest(IDiagnosisEnvironment environment, IServerProbe? serverProbe = null)
{
    /// <param name="lanAddress">An address typed for a same-network connection, or null.</param>
    /// <param name="label">The first 4 characters of a token, or null. With neither, no particular PC is expected and a silent network is a note, not a failure.</param>
    /// <param name="serverAddress">The configured server ("wss://host:port" or "host:port"), or null when no server is set up.</param>
    public async Task<NetworkTestReport> RunAsync(string? lanAddress, string? label, string? serverAddress, CancellationToken ct)
    {
        var checks = new List<NetworkCheck>();
        var peerExpected = !string.IsNullOrWhiteSpace(lanAddress) || !string.IsNullOrWhiteSpace(label);
        var lan = await new LanDiagnosis(environment).RunAsync(lanAddress, label, ct).ConfigureAwait(false);
        foreach (var step in lan.Steps)
        {
            var (id, fallback) = step.Step switch
            {
                DiagnosisStep.Adapter => ("adapter", ErrorCatalogue.NoNetwork),
                DiagnosisStep.Subnet => ("subnet", ErrorCatalogue.PcUnreachable),
                DiagnosisStep.DiscoveryReply => ("discovery", ErrorCatalogue.PcNotFound),
                _ => ("port", ErrorCatalogue.PcUnreachable),
            };
            if (step.State == StepState.Passed) checks.Add(new NetworkCheck(id, CheckStatus.Passed, step.Message));
            else if (step.State == StepState.Skipped) checks.Add(new NetworkCheck(id, CheckStatus.Skipped, step.Message));
            else if (!peerExpected && step.Step == DiagnosisStep.DiscoveryReply)
                checks.Add(new NetworkCheck(id, CheckStatus.Note, "No PC on this network answered the search. That is normal when nobody is sharing right now."));
            else checks.Add(new NetworkCheck(id, CheckStatus.Failed, step.Message, ErrorCatalogue.Classify(step.Message) ?? fallback));
        }

        if (string.IsNullOrWhiteSpace(serverAddress))
        {
            checks.Add(new NetworkCheck("server", CheckStatus.Skipped, "No server is set up on this PC, so there is nothing to reach."));
        }
        else
        {
            var probe = await (serverProbe ?? new TcpServerProbe()).ProbeAsync(serverAddress, 3, ct).ConfigureAwait(false);
            checks.Add(new NetworkCheck("server", probe.Reachable ? CheckStatus.Passed : CheckStatus.Failed, probe.Message, probe.Error,
                probe.LatenciesMs.Count > 0 ? probe.LatenciesMs.Average() : null));
        }
        return new NetworkTestReport(checks);
    }
}

/// <summary>TCP connect time to the server's host and port.</summary>
public sealed class TcpServerProbe : IServerProbe
{
    public static bool TryParseAddress(string text, out string host, out int port)
    {
        host = "";
        port = 0;
        var s = text.Trim();
        if (s.Length == 0) return false;
        var defaultPort = 443;
        if (s.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss" or "http" or "https") || string.IsNullOrEmpty(uri.Host)) return false;
            host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
            port = uri.Port;   // the framework knows the default port of ws, wss, http and https
            return port is >= 1 and <= 65535;
        }
        if (s.Any(char.IsWhiteSpace) || s.Contains('/')) return false;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0) return false;
            host = s[1..close];
            var rest = s[(close + 1)..];
            port = defaultPort;
            if (rest.Length > 0 && (!rest.StartsWith(':') || !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port))) return false;
            return port is >= 1 and <= 65535 && IPAddress.TryParse(host, out _);
        }
        var parts = s.Split(':');
        if (parts.Length > 2) return false;
        host = parts[0];
        port = defaultPort;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
        return host.Length > 0 && port is >= 1 and <= 65535;
    }

    public async Task<ServerProbeResult> ProbeAsync(string serverAddress, int attempts, CancellationToken ct)
    {
        if (!TryParseAddress(serverAddress, out var host, out var port))
            return new ServerProbeResult("", 0, false, [], "The server address is not valid. It looks like server.example.com:443 or wss://server.example.com.", ErrorCatalogue.ServerAddressNotValid);

        var times = new List<double>();
        string? failure = null;
        for (var i = 0; i < Math.Max(1, attempts); i++)
        {
            ct.ThrowIfCancellationRequested();
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var clock = Stopwatch.StartNew();
            try
            {
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                times.Add(clock.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = "no answer within 4 seconds"; }
            catch (SocketException ex) { failure = ex.SocketErrorCode == SocketError.ConnectionRefused ? "the connection was refused" : ex.SocketErrorCode == SocketError.HostNotFound ? "the name could not be found" : "the connection failed (" + ex.SocketErrorCode + ")"; }
        }
        if (times.Count == 0)
            return new ServerProbeResult(host, port, false, [], $"Could not reach {host}:{port}: {failure}. Check the address, that the server is running, and that its port is open in the firewall and the router.", ErrorCatalogue.ServerUnreachable);
        var text = string.Create(CultureInfo.InvariantCulture, $"Reached {host}:{port}. Connection time: best {times.Min():0} ms, average {times.Average():0} ms, worst {times.Max():0} ms ({times.Count} of {Math.Max(1, attempts)} attempts answered).");
        return new ServerProbeResult(host, port, true, times, text, null);
    }
}
