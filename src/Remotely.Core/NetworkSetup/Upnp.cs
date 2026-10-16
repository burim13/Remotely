using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Remotely.Core.NetworkSetup;

public interface IIgdLocator
{
    /// <summary>URLs of the device descriptions of Internet Gateway Devices (routers) found on the local network.</summary>
    Task<IReadOnlyList<Uri>> FindAsync(CancellationToken ct = default);
}

/// <summary>Finds routers with SSDP (a multicast M-SEARCH to 239.255.255.250:1900) and reads the LOCATION header of each answer.</summary>
public sealed class SsdpIgdLocator(TimeSpan? listenFor = null) : IIgdLocator
{
    private static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
    ];

    public static string BuildSearch(string target) =>
        "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + target + "\r\n\r\n";

    public static Uri? ParseLocation(string response)
    {
        foreach (var line in response.Split("\r\n"))
        {
            var i = line.IndexOf(':');
            if (i > 0 && line[..i].Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(line[(i + 1)..].Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
                return uri;
        }
        return null;
    }

    public async Task<IReadOnlyList<Uri>> FindAsync(CancellationToken ct = default)
    {
        var found = new List<Uri>();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        try
        {
            foreach (var target in SearchTargets)
            {
                var datagram = Encoding.ASCII.GetBytes(BuildSearch(target));
                await udp.SendAsync(datagram, endpoint, ct).ConfigureAwait(false);
            }
        }
        catch (SocketException) { return found; }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(listenFor ?? TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                var received = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                if (ParseLocation(Encoding.ASCII.GetString(received.Buffer)) is { } uri && !found.Contains(uri)) found.Add(uri);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        return found;
    }
}

/// <summary>What was read from a router's device description: where to send commands and which service version it speaks.</summary>
public sealed record IgdService(Uri ControlUrl, string ServiceType);

public static class IgdDescription
{
    private static readonly string[] WanServices = ["urn:schemas-upnp-org:service:WANIPConnection:", "urn:schemas-upnp-org:service:WANPPPConnection:"];

    public static IgdService? Parse(string xml, Uri descriptionUrl)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }

        var baseUrl = descriptionUrl;
        var urlBase = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value.Trim();
        if (!string.IsNullOrEmpty(urlBase) && Uri.TryCreate(urlBase, UriKind.Absolute, out var b)) baseUrl = b;

        foreach (var service in doc.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            var type = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value.Trim();
            var control = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value.Trim();
            if (type is null || control is null || !WanServices.Any(p => type.StartsWith(p, StringComparison.Ordinal))) continue;
            if (Uri.TryCreate(baseUrl, control, out var controlUrl)) return new IgdService(controlUrl, type);
        }
        return null;
    }
}

/// <summary>UPnP Internet Gateway Device client: AddPortMapping, DeletePortMapping and GetExternalIPAddress over SOAP. Written for this project (no third-party package).</summary>
public sealed class UpnpPortMapper(IIgdLocator locator, HttpClient? http = null) : IPortMapper
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
    private IgdService? _service;

    public string Name => "UPnP";

    private async Task<IgdService?> ResolveAsync(CancellationToken ct)
    {
        if (_service is not null) return _service;
        foreach (var location in await locator.FindAsync(ct).ConfigureAwait(false))
        {
            // The router must be on this network: a description URL that points anywhere else is ignored.
            if (!IPAddress.TryParse(location.Host, out var ip) || AddressClassifier.Classify(ip) is not (AddressKind.Private or AddressKind.Loopback)) continue;
            try
            {
                var xml = await _http.GetStringAsync(location, ct).ConfigureAwait(false);
                var service = IgdDescription.Parse(xml, location);
                if (service is not null && service.ControlUrl.Host == location.Host) return _service = service;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        }
        return null;
    }

    public async Task<PortMappingAttempt> CreateAsync(PortMappingRequest request, CancellationToken ct = default)
    {
        var service = await ResolveAsync(ct).ConfigureAwait(false);
        if (service is null) return PortMappingAttempt.Failure("Setup_Upnp_NoRouter");

        var lease = request.Lease;
        var reply = await SendAsync(service, "AddPortMapping", AddArguments(request, lease), ct).ConfigureAwait(false);
        if (reply.FaultCode == 725)   // "only permanent leases supported"
        {
            lease = TimeSpan.Zero;
            reply = await SendAsync(service, "AddPortMapping", AddArguments(request, lease), ct).ConfigureAwait(false);
        }
        if (!reply.Ok)
            return PortMappingAttempt.Failure(reply.FaultCode switch { 718 => "Setup_Upnp_Conflict", null => "Setup_Upnp_NoAnswer", _ => "Setup_Upnp_Refused" }, reply.FaultCode?.ToString(CultureInfo.InvariantCulture));

        IPAddress? external = null;
        var ext = await SendAsync(service, "GetExternalIPAddress", [], ct).ConfigureAwait(false);
        if (ext.Ok && ext.Values.TryGetValue("NewExternalIPAddress", out var text) && IPAddress.TryParse(text, out var parsed)) external = parsed;

        return PortMappingAttempt.Success(new PortMapping(Name, "TCP", request.ExternalPort, request.InternalPort, request.InternalAddress, external, lease, DateTimeOffset.UtcNow, request.Description));
    }

    public async Task<bool> RemoveAsync(PortMapping mapping, CancellationToken ct = default)
    {
        var service = await ResolveAsync(ct).ConfigureAwait(false);
        if (service is null) return false;
        var reply = await SendAsync(service, "DeletePortMapping",
            [("NewRemoteHost", ""), ("NewExternalPort", mapping.ExternalPort.ToString(CultureInfo.InvariantCulture)), ("NewProtocol", mapping.Protocol)], ct).ConfigureAwait(false);
        return reply.Ok;
    }

    private static (string, string)[] AddArguments(PortMappingRequest r, TimeSpan lease) =>
    [
        ("NewRemoteHost", ""),
        ("NewExternalPort", r.ExternalPort.ToString(CultureInfo.InvariantCulture)),
        ("NewProtocol", "TCP"),
        ("NewInternalPort", r.InternalPort.ToString(CultureInfo.InvariantCulture)),
        ("NewInternalClient", r.InternalAddress.ToString()),
        ("NewEnabled", "1"),
        ("NewPortMappingDescription", r.Description),
        ("NewLeaseDuration", ((int)lease.TotalSeconds).ToString(CultureInfo.InvariantCulture)),
    ];

    internal sealed record SoapReply(bool Ok, int? FaultCode, IReadOnlyDictionary<string, string> Values);

    internal static string BuildEnvelope(string serviceType, string action, IEnumerable<(string Name, string Value)> arguments)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(System.Security.SecurityElement.Escape(serviceType)).Append("\">");
        foreach (var (name, value) in arguments)
            sb.Append('<').Append(name).Append('>').Append(System.Security.SecurityElement.Escape(value)).Append("</").Append(name).Append('>');
        sb.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
        return sb.ToString();
    }

    internal static SoapReply ParseReply(bool httpOk, string body)
    {
        var values = new Dictionary<string, string>();
        int? fault = null;
        try
        {
            var doc = XDocument.Parse(body);
            foreach (var e in doc.Descendants().Where(e => !e.HasElements)) values[e.Name.LocalName] = e.Value;
            if (values.TryGetValue("errorCode", out var code) && int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) fault = n;
        }
        catch (System.Xml.XmlException) { }
        return new SoapReply(httpOk, fault, values);
    }

    private async Task<SoapReply> SendAsync(IgdService service, string action, IEnumerable<(string, string)> arguments, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, service.ControlUrl)
        {
            Content = new StringContent(BuildEnvelope(service.ServiceType, action, arguments), Encoding.UTF8, "text/xml"),
        };
        message.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service.ServiceType}#{action}\"");
        try
        {
            using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseReply(response.IsSuccessStatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new SoapReply(false, null, new Dictionary<string, string>()); }
    }
}
