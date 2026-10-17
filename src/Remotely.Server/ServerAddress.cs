using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Remotely.Server;

/// <summary>The name or address devices use to reach this server: saved in address.txt next to the data, turned into the wss:// address that goes into an invite.</summary>
public static partial class ServerAddress
{
    public static string FilePath(string dataDir) => Path.Combine(dataDir, "address.txt");

    /// <summary>The saved address, or empty when none was saved.</summary>
    public static string Load(string dataDir)
    {
        try
        {
            return File.Exists(FilePath(dataDir)) ? File.ReadAllText(FilePath(dataDir)).Trim() : "";
        }
        catch (IOException) { return ""; }
    }

    public static void Save(string dataDir, string address)
    {
        Directory.CreateDirectory(dataDir);
        DataFiles.WriteAtomic(FilePath(dataDir), System.Text.Encoding.UTF8.GetBytes(address.Trim()));
    }

    /// <summary>"myhome.example.com", "myhome.example.com:9000", "192.168.1.5", "2001:db8::1", "[2001:db8::1]:9000" or a full wss:// address become the address devices connect to.</summary>
    public static string BuildUri(bool tls, int publicPort, string address)
    {
        address = address.Trim();
        if (address.Contains("://", StringComparison.Ordinal)) return address;
        var scheme = tls ? "wss" : "ws";
        string hostPort;
        if (address.StartsWith('['))
            hostPort = address.Contains("]:", StringComparison.Ordinal) ? address : $"{address}:{publicPort}";
        else if (IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            hostPort = $"[{address}]:{publicPort}";
        else
            hostPort = address.Count(c => c == ':') == 1 ? address : $"{address}:{publicPort}";
        return $"{scheme}://{hostPort}/ws";
    }

    /// <summary>Null when the address is usable, otherwise a plain sentence saying what is wrong. Empty is allowed (it means "work it out").</summary>
    public static string? Validate(string address, bool tls, int publicPort)
    {
        address = address.Trim();
        if (address.Length == 0) return null;
        if (address.Length > 255) return "The address is too long.";
        if (address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return "The address cannot contain spaces or control characters.";
        if (address.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(address, UriKind.Absolute, out var full) && full.Scheme is "ws" or "wss" && full.Host.Length > 0 && full.PathAndQuery.StartsWith("/ws", StringComparison.Ordinal)
                ? null
                : "A full address must look like wss://name:8443/ws.";
        }
        if (!Uri.TryCreate(BuildUri(tls, publicPort, address), UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return "That is not a valid name or address.";
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
        if (uri.HostNameType == UriHostNameType.IPv6) return IPAddress.TryParse(host, out _) ? null : "That is not a valid IPv6 address.";
        return HostName().IsMatch(host) ? null : "A name may only contain letters, digits, dots and hyphens.";
    }

    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?$")]
    private static partial Regex HostName();

    /// <summary>The address to put in an invite: the one given, else the one saved, else this PC's home-network address.</summary>
    public static (string? Uri, string Source) Resolve(string dataDir, bool tls, int publicPort, string? given = null)
    {
        if (!string.IsNullOrWhiteSpace(given)) return (BuildUri(tls, publicPort, given), "given");
        if (Load(dataDir) is { Length: > 0 } saved) return (BuildUri(tls, publicPort, saved), "saved");
        var lan = LocalNetworkAddress();
        return lan is null ? (null, "none") : (BuildUri(tls, publicPort, lan), "automatic");
    }

    /// <summary>The first usable IPv4 address of an up network adapter (preferring one with a gateway); an IPv6 address that is not link-local only when there is no IPv4.</summary>
    public static string? LocalNetworkAddress()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
                .ToList();
            foreach (var nic in adapters)
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address)
                        && !u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        return u.Address.ToString();
            foreach (var nic in adapters)
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal && !IPAddress.IsLoopback(u.Address) && !u.Address.IsIPv6Multicast)
                        return u.Address.ToString();
        }
        catch (NetworkInformationException) { }
        return null;
    }
}
