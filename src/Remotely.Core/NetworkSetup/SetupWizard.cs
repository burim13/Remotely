using System.Net;
using Remotely.Core.Admin;

namespace Remotely.Core.NetworkSetup;

public sealed record SetupWizardServices(
    ILocalAddressSource Addresses,
    IPortProbe Ports,
    IReachabilityProbe Reachability,
    IElevatedActions Elevated,
    IOutsideCheck Outside,
    PortMappingSession Mapping,
    IServerAdmin? Admin,
    Func<IReadOnlyList<IPAddress>> Gateways);

public enum PortStepVerdict
{
    Free,
    /// <summary>The port is taken, and what answers there is this Remotely server (presents the expected key).</summary>
    UsedByThisServer,
    /// <summary>The port is taken by something else (or by a server whose key could not be checked).</summary>
    UsedBySomethingElse,
    NotAllowed,
    Invalid,
    Unknown,
}

public sealed record PortStep(PortStepVerdict Verdict, PortStatus Raw);

public enum InsideVerdict
{
    /// <summary>This PC can open the server on its own network address: server running, port open to the network.</summary>
    Works,
    /// <summary>It answers on this PC (loopback) but not on the network address: Windows Firewall or another firewall is the likely cause.</summary>
    BlockedOnNetworkAddress,
    /// <summary>Not even this PC can reach it: the server is not running or listens on another port.</summary>
    ServerNotAnswering,
    WrongServer,
}

public sealed record InsideReport(InsideVerdict Verdict, ReachabilityResult Loopback, ReachabilityResult? NetworkAddress, ReachabilityResult? PublicName);

public sealed record ServerSnapshot(int Port, string Address, bool TlsEnabled, string? PinHex, DateTimeOffset? CertificateExpiresUtc, string? Version);

public sealed record CertificateReport(CertificateSummary? Presented, ExpiryState? Expiry, string? PinHex);

/// <summary>
/// The first-run "make my server reachable" flow as plain methods, so each step can be tested with fake network probes. The window (SetupWizardPage) only shows
/// what these return. The wizard never changes anything on the router unless <see cref="PortMappingSession.AutomaticMappingEnabled"/> was switched on by the user.
/// </summary>
public sealed class SetupWizard(SetupWizardServices services)
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    public int Port { get; private set; } = 8443;
    public bool TlsEnabled { get; private set; } = true;
    public string? ServerPin { get; private set; }
    public string PublicName { get; private set; } = "";
    public IPAddress? ChosenLocalAddress { get; private set; }
    public ServerSnapshot? Snapshot { get; private set; }

    public async Task<ServerSnapshot?> LoadServerAsync(CancellationToken ct = default)
    {
        if (services.Admin is null) return null;
        try
        {
            var s = await services.Admin.GetStatusAsync(ct).ConfigureAwait(false);
            Port = s.Port;
            TlsEnabled = s.TlsEnabled;
            ServerPin = s.TlsEnabled ? s.CertificatePin : null;
            if (!string.IsNullOrWhiteSpace(s.Address) && PublicName.Length == 0) { var v = PublicNameValidator.Validate(HostOf(s.Address)); if (v.IsValid && !IPAddressIsLocal(v.Host)) PublicName = v.Host; }
            return Snapshot = new ServerSnapshot(s.Port, s.Address, s.TlsEnabled, ServerPin, s.CertificateExpiresUtc, s.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or NotSupportedException) { return null; }
    }

    private static string HostOf(string address)
    {
        var a = address.Trim();
        if (Uri.TryCreate(a, UriKind.Absolute, out var uri) && uri.Scheme is "ws" or "wss" or "http" or "https") return uri.Host;
        return a;
    }

    private static bool IPAddressIsLocal(string host) => IPAddress.TryParse(host, out var ip) && AddressClassifier.Classify(ip) is AddressKind.Private or AddressKind.Loopback or AddressKind.LinkLocal;

    public DetectedAddresses DetectAddresses()
    {
        var detected = AddressDetector.Detect(services.Addresses);
        ChosenLocalAddress ??= detected.Recommended?.Address;
        return detected;
    }

    public void ChooseLocalAddress(IPAddress address) => ChosenLocalAddress = address;

    public PublicNameValidator.Result SetPublicName(string? text)
    {
        var result = PublicNameValidator.Validate(text);
        if (result.IsValid) { PublicName = result.Host; if (result.Port is { } p) Port = p; }
        return result;
    }

    public bool TrySetPort(int port)
    {
        if (port is < 1 or > 65535) return false;
        Port = port;
        return true;
    }

    public async Task<PortStep> CheckPortAsync(CancellationToken ct = default)
    {
        var raw = await services.Ports.CheckAsync(Port, ct).ConfigureAwait(false);
        switch (raw)
        {
            case PortStatus.Free: return new(PortStepVerdict.Free, raw);
            case PortStatus.NotAllowed: return new(PortStepVerdict.NotAllowed, raw);
            case PortStatus.Invalid: return new(PortStepVerdict.Invalid, raw);
            case PortStatus.InUse:
                var probe = await services.Reachability.ProbeAsync("127.0.0.1", Port, TlsEnabled, ServerPin, ProbeTimeout, ct).ConfigureAwait(false);
                // Only a server that presents the expected key counts as "ours"; without a known pin a listener cannot be told apart from another program.
                var ours = probe.Outcome == ReachabilityOutcome.Reachable && (!TlsEnabled || ServerPin is not null);
                return new(ours ? PortStepVerdict.UsedByThisServer : PortStepVerdict.UsedBySomethingElse, raw);
            default: return new(PortStepVerdict.Unknown, raw);
        }
    }

    public Task<FirewallRuleState> GetFirewallAsync(CancellationToken ct = default) => services.Elevated.GetFirewallRuleAsync(Port, ct);

    public Task<ActionResult> AddFirewallRuleAsync(CancellationToken ct = default) => services.Elevated.AddFirewallRuleAsync(Port, ct);

    /// <summary>
    /// Tries the server on this PC (loopback), on its network address, and, if a public name was given, on that name. A failure for the public name from the inside
    /// does not prove the outside is broken: some routers do not send a PC's own traffic back in. It is shown as information only.
    /// </summary>
    public async Task<InsideReport> TestInsideAsync(CancellationToken ct = default)
    {
        var loop = await services.Reachability.ProbeAsync("127.0.0.1", Port, TlsEnabled, ServerPin, ProbeTimeout, ct).ConfigureAwait(false);
        ReachabilityResult? net = null, name = null;
        if (ChosenLocalAddress is not null)
            net = await services.Reachability.ProbeAsync(ChosenLocalAddress.ToString(), Port, TlsEnabled, ServerPin, ProbeTimeout, ct).ConfigureAwait(false);
        if (PublicName.Length > 0)
            name = await services.Reachability.ProbeAsync(PublicName, Port, TlsEnabled, ServerPin, ProbeTimeout, ct).ConfigureAwait(false);

        var verdict =
            loop.Outcome == ReachabilityOutcome.WrongServer || net?.Outcome == ReachabilityOutcome.WrongServer ? InsideVerdict.WrongServer
            : loop.Outcome != ReachabilityOutcome.Reachable ? InsideVerdict.ServerNotAnswering
            : net is null || net.Outcome == ReachabilityOutcome.Reachable ? InsideVerdict.Works
            : InsideVerdict.BlockedOnNetworkAddress;
        return new InsideReport(verdict, loop, net, name);
    }

    public RouterInstructions? GetRouterInstructions()
    {
        if (ChosenLocalAddress is null) return null;
        var gateway = services.Gateways().FirstOrDefault();
        return RouterGuide.Build(ChosenLocalAddress, Port, PublicName, gateway);
    }

    public async Task<CertificateReport> GetCertificateAsync(CancellationToken ct = default)
    {
        if (!TlsEnabled) return new(null, null, null);
        var probe = await services.Reachability.ProbeAsync("127.0.0.1", Port, tls: true, expectedPinHex: null, ProbeTimeout, ct).ConfigureAwait(false);
        if (probe.Certificate is null) return new(null, null, ServerPin);
        return new(probe.Certificate, CertificateInspector.Expiry(probe.Certificate.NotBefore, probe.Certificate.NotAfter, DateTimeOffset.UtcNow), probe.Certificate.PinHex);
    }

    /// <summary>The address to give the phone: the public name if one was entered, else the router's outside address supplied by the caller.</summary>
    public async Task<(OutsideCheckTicket Ticket, string Url)?> StartOutsideTestAsync(string? wanAddress, CancellationToken ct = default)
    {
        var host = PublicName.Length > 0 ? PublicName : wanAddress?.Trim();
        if (string.IsNullOrEmpty(host)) return null;
        var ticket = await services.Outside.StartAsync(ct).ConfigureAwait(false);
        return (ticket, OutsideCheckEvaluator.BuildUrl(host, Port, ticket));
    }

    public Task<(OutsideVerdict Verdict, OutsideCheckHit? Hit)> WaitForOutsideAsync(OutsideCheckTicket ticket, TimeSpan timeout, TimeSpan? pollInterval = null, CancellationToken ct = default) =>
        OutsideCheckEvaluator.WaitAsync(services.Outside, ticket, timeout, pollInterval ?? TimeSpan.FromSeconds(2), ct);

    public async Task<PortMappingAttempt> CreateAutomaticMappingAsync(CancellationToken ct = default)
    {
        if (ChosenLocalAddress is null) return PortMappingAttempt.Failure("Setup_Upnp_NoAddress");
        return await services.Mapping.CreateAsync(new PortMappingRequest(ChosenLocalAddress, Port, Port, "Remotely Server", TimeSpan.FromHours(1)), ct).ConfigureAwait(false);
    }

    /// <summary>Writes the chosen address and port into the server's settings through the admin API. Returns false when no admin connection exists.</summary>
    public async Task<bool> ApplyToServerAsync(CancellationToken ct = default)
    {
        if (services.Admin is null) return false;
        try
        {
            var current = await services.Admin.GetSettingsAsync(ct).ConfigureAwait(false);
            await services.Admin.SetSettingsAsync(current with { Port = Port, Address = PublicName.Length > 0 ? PublicName : current.Address }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or NotSupportedException) { return false; }
    }
}
