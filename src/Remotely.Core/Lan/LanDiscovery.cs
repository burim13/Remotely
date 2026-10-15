using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Remotely.Core.Lan;

/// <summary>
/// Finding a PC on the network from its token's 4-character label. The controller broadcasts "who has label K7P4?"; the PC that shows that label answers with
/// its TCP port and name. The label is public by design; the secret part of the token never goes on the network.
/// </summary>
public static class LanDiscovery
{
    private static readonly byte[] Magic = "RMTL"u8.ToArray();
    private const byte TypeProbe = 0x10;
    private const byte TypeReply = 0x11;

    public sealed record Found(IPEndPoint Endpoint, string HostName);

    /// <summary>Broadcasts a probe and returns the first matching PC, or null after <paramref name="timeout"/>. <paramref name="extraTargets"/> adds unicast targets (used by tests).</summary>
    public static async Task<Found?> FindAsync(string id, TimeSpan timeout, CancellationToken ct, IReadOnlyList<IPEndPoint>? extraTargets = null, int discoveryPort = LanNetwork.DiscoveryPort)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var probe = new byte[Magic.Length + 1 + id.Length];
        Magic.CopyTo(probe, 0);
        probe[Magic.Length] = TypeProbe;
        Encoding.ASCII.GetBytes(id).CopyTo(probe, Magic.Length + 1);

        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, discoveryPort) };
        targets.AddRange(LanNetwork.BroadcastAddresses().Select(b => new IPEndPoint(b, discoveryPort)));
        if (extraTargets is not null) targets.AddRange(extraTargets);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var nextSend = DateTime.MinValue;
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= nextSend)
                {
                    foreach (var t in targets)
                        try { await udp.SendAsync(probe, t, deadline.Token).ConfigureAwait(false); }
                        catch (SocketException) { /* that network card cannot broadcast; the others may */ }
                    nextSend = DateTime.UtcNow.AddMilliseconds(700);
                }
                using var slice = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                slice.CancelAfter(300);
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(slice.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { continue; }   // just the 300 ms slice: look at the clock, maybe re-send
                if (TryParseReply(result.Buffer, id, out var port, out var name))
                    return new Found(new IPEndPoint(result.RemoteEndPoint.Address, port), name);
            }
        }
        catch (OperationCanceledException) { /* timeout or cancelled */ }
        return null;
    }

    private static bool TryParseReply(byte[] data, string id, out int port, out string name)
    {
        port = 0; name = "";
        if (data.Length < Magic.Length + 1 + id.Length + 2 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic) || data[Magic.Length] != TypeReply) return false;
        if (Encoding.ASCII.GetString(data, Magic.Length + 1, id.Length) != id) return false;
        port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(Magic.Length + 1 + id.Length));
        name = Encoding.UTF8.GetString(data, Magic.Length + 1 + id.Length + 2, data.Length - (Magic.Length + 1 + id.Length + 2));
        return port != 0;
    }

    /// <summary>Answers probes for the current token's label. Run it for as long as the PC is showing a token.</summary>
    public static async Task ServeAsync(UdpClient udp, Func<string?> currentId, Func<int> tcpPort, string hostName, bool allowLoopback, CancellationToken ct)
    {
        var name = Encoding.UTF8.GetBytes(hostName.Length > 40 ? hostName[..40] : hostName);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var received = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var data = received.Buffer;
                var id = currentId();
                if (id is null || data.Length != Magic.Length + 1 + id.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic) || data[Magic.Length] != TypeProbe) continue;
                if (Encoding.ASCII.GetString(data, Magic.Length + 1, id.Length) != id) continue;
                if (!LanNetwork.IsOnLink(received.RemoteEndPoint.Address, allowLoopback)) continue;
                var reply = new byte[Magic.Length + 1 + id.Length + 2 + name.Length];
                Magic.CopyTo(reply, 0);
                reply[Magic.Length] = TypeReply;
                Encoding.ASCII.GetBytes(id).CopyTo(reply, Magic.Length + 1);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(Magic.Length + 1 + id.Length), (ushort)tcpPort());
                name.CopyTo(reply, Magic.Length + 1 + id.Length + 2);
                try { await udp.SendAsync(reply, received.RemoteEndPoint, ct).ConfigureAwait(false); }
                catch (SocketException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public static UdpClient OpenResponder(int discoveryPort = LanNetwork.DiscoveryPort)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
        return udp;
    }
}
