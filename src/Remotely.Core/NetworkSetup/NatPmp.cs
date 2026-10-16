using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Remotely.Core.NetworkSetup;

/// <summary>Packet layouts of NAT-PMP (RFC 6886). Pure functions, tested without a router.</summary>
public static class NatPmpPacket
{
    public const int Port = 5351;
    public const byte OpExternalAddress = 0;
    public const byte OpMapTcp = 2;

    public static byte[] ExternalAddressRequest() => [0, OpExternalAddress];

    /// <summary>Asks for a TCP mapping. A lifetime of zero with an external port of zero removes the mapping for that internal port.</summary>
    public static byte[] MapTcpRequest(int internalPort, int suggestedExternalPort, uint lifetimeSeconds)
    {
        var p = new byte[12];
        p[1] = OpMapTcp;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), (ushort)suggestedExternalPort);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), lifetimeSeconds);
        return p;
    }

    public static bool TryParseExternalAddress(byte[] reply, out ushort resultCode, out IPAddress? address)
    {
        resultCode = 0;
        address = null;
        if (reply.Length < 12 || reply[0] != 0 || reply[1] != 128 + OpExternalAddress) return false;
        resultCode = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2));
        address = new IPAddress(reply.AsSpan(8, 4));
        return true;
    }

    public static bool TryParseMapTcp(byte[] reply, out ushort resultCode, out int internalPort, out int mappedExternalPort, out uint lifetimeSeconds)
    {
        resultCode = 0; internalPort = 0; mappedExternalPort = 0; lifetimeSeconds = 0;
        if (reply.Length < 16 || reply[0] != 0 || reply[1] != 128 + OpMapTcp) return false;
        resultCode = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2));
        internalPort = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8));
        mappedExternalPort = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(10));
        lifetimeSeconds = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(12));
        return true;
    }
}

public interface INatPmpTransport
{
    /// <summary>Sends one request to the gateway's NAT-PMP port and returns the reply, or null if none came.</summary>
    Task<byte[]?> ExchangeAsync(IPAddress gateway, byte[] request, CancellationToken ct = default);
}

public sealed class UdpNatPmpTransport : INatPmpTransport
{
    public async Task<byte[]?> ExchangeAsync(IPAddress gateway, byte[] request, CancellationToken ct = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var delay = TimeSpan.FromMilliseconds(250);
        for (var attempt = 0; attempt < 3; attempt++, delay *= 2)
        {
            try
            {
                await udp.SendAsync(request, new IPEndPoint(gateway, NatPmpPacket.Port), ct).ConfigureAwait(false);
                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(delay);
                var received = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                if (received.RemoteEndPoint.Address.Equals(gateway)) return received.Buffer;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
        }
        return null;
    }
}

public sealed class NatPmpPortMapper(Func<IReadOnlyList<IPAddress>> gateways, INatPmpTransport transport) : IPortMapper
{
    public string Name => "NAT-PMP";

    public async Task<PortMappingAttempt> CreateAsync(PortMappingRequest request, CancellationToken ct = default)
    {
        var list = gateways();
        if (list.Count == 0) return PortMappingAttempt.Failure("Setup_Upnp_NoRouter");
        foreach (var gateway in list)
        {
            var addressReply = await transport.ExchangeAsync(gateway, NatPmpPacket.ExternalAddressRequest(), ct).ConfigureAwait(false);
            if (addressReply is null || !NatPmpPacket.TryParseExternalAddress(addressReply, out var addressCode, out var external)) continue;
            if (addressCode != 0) return PortMappingAttempt.Failure(Describe(addressCode), addressCode.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var lifetime = (uint)Math.Max(request.Lease.TotalSeconds, 60);
            var mapReply = await transport.ExchangeAsync(gateway, NatPmpPacket.MapTcpRequest(request.InternalPort, request.ExternalPort, lifetime), ct).ConfigureAwait(false);
            if (mapReply is null || !NatPmpPacket.TryParseMapTcp(mapReply, out var code, out _, out var mapped, out var granted)) continue;
            if (code != 0) return PortMappingAttempt.Failure(Describe(code), code.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return PortMappingAttempt.Success(new PortMapping(Name, "TCP", mapped, request.InternalPort, request.InternalAddress, external, TimeSpan.FromSeconds(granted), DateTimeOffset.UtcNow, request.Description));
        }
        return PortMappingAttempt.Failure("Setup_Upnp_NoRouter");
    }

    public async Task<bool> RemoveAsync(PortMapping mapping, CancellationToken ct = default)
    {
        foreach (var gateway in gateways())
        {
            var reply = await transport.ExchangeAsync(gateway, NatPmpPacket.MapTcpRequest(mapping.InternalPort, 0, 0), ct).ConfigureAwait(false);
            if (reply is not null && NatPmpPacket.TryParseMapTcp(reply, out var code, out _, out _, out _) && code == 0) return true;
        }
        return false;
    }

    private static string Describe(ushort code) => code switch { 2 => "Setup_Upnp_Refused", 4 => "Setup_Upnp_Refused", _ => "Setup_Upnp_NoAnswer" };
}
