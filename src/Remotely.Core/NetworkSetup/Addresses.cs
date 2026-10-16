using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Remotely.Core.NetworkSetup;

public enum AddressKind
{
    Loopback,
    LinkLocal,
    /// <summary>10/8, 172.16/12, 192.168/16 (RFC 1918) or IPv6 unique-local fc00::/7: reachable only inside a network.</summary>
    Private,
    /// <summary>100.64.0.0/10 (RFC 6598): the range internet providers use when many customers share one public address.</summary>
    CarrierGradeNat,
    Public,
}

public static class AddressClassifier
{
    public static AddressKind Classify(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return AddressKind.Loopback;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal) return AddressKind.LinkLocal;
            var first = address.GetAddressBytes()[0];
            if ((first & 0xFE) == 0xFC) return AddressKind.Private;
            return address.IsIPv6Multicast || address.Equals(IPAddress.IPv6Any) ? AddressKind.LinkLocal : AddressKind.Public;
        }
        var b = address.GetAddressBytes();
        if (b[0] == 10) return AddressKind.Private;
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return AddressKind.Private;
        if (b[0] == 192 && b[1] == 168) return AddressKind.Private;
        if (b[0] == 169 && b[1] == 254) return AddressKind.LinkLocal;
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return AddressKind.CarrierGradeNat;
        if (b[0] == 0 || b[0] >= 224) return AddressKind.LinkLocal;
        return AddressKind.Public;
    }
}

/// <summary>One address of this PC. <see cref="HasGateway"/> is true when the network adapter it belongs to has a default gateway (a router), which is the adapter the internet goes through.</summary>
public sealed record LocalAddress(IPAddress Address, string AdapterName, AddressKind Kind, bool HasGateway);

public interface ILocalAddressSource
{
    IReadOnlyList<LocalAddress> Read();
}

public sealed class SystemLocalAddressSource : ILocalAddressSource
{
    public IReadOnlyList<LocalAddress> Read()
    {
        var result = new List<LocalAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                var gateway = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
                foreach (var u in props.UnicastAddresses)
                    result.Add(new LocalAddress(u.Address, nic.Name, AddressClassifier.Classify(u.Address), gateway));
            }
        }
        catch (NetworkInformationException) { }
        return result;
    }

    /// <summary>The default gateway (router) addresses of the adapters that are up. NAT-PMP is spoken to these.</summary>
    public static IReadOnlyList<IPAddress> Gateways()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var g in nic.GetIPProperties().GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) result.Add(g.Address);
            }
        }
        catch (NetworkInformationException) { }
        return result;
    }
}

public sealed record DetectedAddresses(IReadOnlyList<LocalAddress> Candidates, LocalAddress? Recommended);

public static class AddressDetector
{
    /// <summary>
    /// IPv4 addresses that a device on the same network could use, best first: addresses on the adapter with a default gateway before others,
    /// ordinary private addresses before the rest. Loopback and link-local (169.254.x.x) addresses are never offered.
    /// </summary>
    public static DetectedAddresses Detect(ILocalAddressSource source)
    {
        var list = source.Read()
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.Kind is not (AddressKind.Loopback or AddressKind.LinkLocal))
            .OrderByDescending(a => a.HasGateway)
            .ThenByDescending(a => a.Kind == AddressKind.Private)
            .ThenBy(a => a.Address.ToString(), StringComparer.Ordinal)
            .ToList();
        return new DetectedAddresses(list, list.FirstOrDefault());
    }
}

/// <summary>Checks the optional public name or address the user types. Accepts a host name, an IPv4 or IPv6 address, with an optional :port. Refuses anything else with a resource key naming the reason.</summary>
public static class PublicNameValidator
{
    public readonly record struct Result(bool IsValid, string? ErrorKey, string Host, int? Port);

    public static Result Validate(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return new(true, null, "", null);
        if (s.Contains("://", StringComparison.Ordinal)) return new(false, "Setup_Err_NameHasScheme", s, null);
        if (s.Any(char.IsWhiteSpace) || s.Contains('/') || s.Contains('@')) return new(false, "Setup_Err_NameInvalid", s, null);

        string host = s;
        int? port = null;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0) return new(false, "Setup_Err_NameInvalid", s, null);
            host = s[1..close];
            var rest = s[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':') || !TryPort(rest[1..], out var p)) return new(false, "Setup_Err_NamePort", s, null);
                port = p;
            }
            return IPAddress.TryParse(host, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6 ? new(true, null, host, port) : new(false, "Setup_Err_NameInvalid", s, null);
        }
        var colons = s.Count(c => c == ':');
        if (colons == 1)
        {
            var i = s.IndexOf(':');
            host = s[..i];
            if (!TryPort(s[(i + 1)..], out var p)) return new(false, "Setup_Err_NamePort", s, null);
            port = p;
        }
        else if (colons > 1)
            return IPAddress.TryParse(s, out _) ? new(false, "Setup_Err_NameIpv6Brackets", s, null) : new(false, "Setup_Err_NameInvalid", s, null);

        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && host.Count(c => c == '.') == 3) return new(true, null, host, port);
        var trimmed = host.TrimEnd('.');
        if (trimmed.Length is 0 or > 253) return new(false, "Setup_Err_NameInvalid", s, null);
        foreach (var label in trimmed.Split('.'))
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')) return new(false, "Setup_Err_NameInvalid", s, null);
            foreach (var c in label)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')) return new(false, "Setup_Err_NameInvalid", s, null);
        }
        return new(true, null, host, port);
    }

    private static bool TryPort(string text, out int port) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}
