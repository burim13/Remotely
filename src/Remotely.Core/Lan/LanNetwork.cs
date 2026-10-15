using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Remotely.Core.Lan;

/// <summary>What "the same network" means: an address that is on the same subnet as one of this PC's own network connections.</summary>
public static class LanNetwork
{
    public const int TcpPort = 47810;
    public const int DiscoveryPort = 47811;

    private static IEnumerable<(IPAddress Address, IPAddress Mask)> Interfaces()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask is { } mask && !u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    yield return (u.Address, mask);
        }
    }

    public static IReadOnlyList<IPAddress> LocalAddresses() => [.. Interfaces().Select(i => i.Address)];

    public static IReadOnlyList<IPAddress> BroadcastAddresses() => [.. Interfaces().Select(i => Combine(i.Address, i.Mask, broadcast: true)).Distinct()];

    /// <summary>True when <paramref name="remote"/> is on the same subnet as this PC. Loopback counts only when <paramref name="allowLoopback"/> is set (tests).</summary>
    public static bool IsOnLink(IPAddress remote, bool allowLoopback)
    {
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return allowLoopback;
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;
        foreach (var (address, mask) in Interfaces())
            if (Combine(address, mask, broadcast: false).Equals(Combine(remote, mask, broadcast: false))) return true;
        return false;
    }

    private static IPAddress Combine(IPAddress address, IPAddress mask, bool broadcast)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var result = new byte[4];
        for (var i = 0; i < 4; i++) result[i] = broadcast ? (byte)(a[i] | ~m[i]) : (byte)(a[i] & m[i]);
        return new IPAddress(result);
    }
}
