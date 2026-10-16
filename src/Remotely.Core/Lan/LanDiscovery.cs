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
    // Version 2 (nearby list): "who is sharing here?". Probe = Magic, 0x12, version. Reply = Magic, 0x13, version, 4-character label, port, name.
    // PCs that only know version 1 ignore these packets (wrong length and type), and version-2 PCs still answer version-1 probes unchanged.
    private const byte TypeListProbe = 0x12;
    private const byte TypeListReply = 0x13;
    private const byte ListVersion = 1;

    public sealed record Found(IPEndPoint Endpoint, string HostName);

    /// <summary>A PC that is sharing right now: its public 4-character label and name, and where to connect. Nothing secret is ever part of this.</summary>
    public sealed record Nearby(string Id, IPEndPoint Endpoint, string HostName);

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

    /// <summary>
    /// Asks "who is sharing here?" and collects every PC that answers within <paramref name="timeout"/> (labels and names only). Returns early only when
    /// cancelled; a cancel returns what was found so far. <paramref name="onFound"/> is called once per new PC as it answers.
    /// </summary>
    public static async Task<IReadOnlyList<Nearby>> ListAsync(TimeSpan timeout, CancellationToken ct, IReadOnlyList<IPEndPoint>? extraTargets = null,
        int discoveryPort = LanNetwork.DiscoveryPort, Action<Nearby>? onFound = null)
    {
        var found = new List<Nearby>();
        UdpClient udp;
        try { udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true }; }
        catch (SocketException) { return found; }   // no usable IPv4 network
        using var _ = udp;
        var probe = new byte[Magic.Length + 2];
        Magic.CopyTo(probe, 0);
        probe[Magic.Length] = TypeListProbe;
        probe[Magic.Length + 1] = ListVersion;

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
                        catch (SocketException) { }
                    nextSend = DateTime.UtcNow.AddMilliseconds(700);
                }
                using var slice = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                slice.CancelAfter(300);
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(slice.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { continue; }
                catch (SocketException) { continue; }   // e.g. an ICMP "port unreachable" from a target that is not listening
                if (!TryParseListReply(result.Buffer, out var id, out var port, out var name)) continue;
                var endpoint = new IPEndPoint(result.RemoteEndPoint.Address, port);
                if (found.Any(f => f.Endpoint.Equals(endpoint))) continue;
                var entry = new Nearby(id, endpoint, name);
                found.Add(entry);
                onFound?.Invoke(entry);
            }
        }
        catch (OperationCanceledException) { }
        return found;
    }

    private static bool TryParseListReply(byte[] data, out string id, out int port, out string name)
    {
        id = ""; port = 0; name = "";
        var header = Magic.Length + 2;
        if (data.Length < header + LanToken.IdLength + 2 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic) || data[Magic.Length] != TypeListReply) return false;
        if (data[Magic.Length + 1] < ListVersion) return false;
        id = Encoding.ASCII.GetString(data, header, LanToken.IdLength);
        if (id.Any(c => !LanToken.Alphabet.Contains(c))) return false;
        port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(header + LanToken.IdLength));
        name = Encoding.UTF8.GetString(data, header + LanToken.IdLength + 2, data.Length - (header + LanToken.IdLength + 2));
        return port != 0;
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
                if (id is not null && data.Length == Magic.Length + 2 && data.AsSpan(0, Magic.Length).SequenceEqual(Magic) && data[Magic.Length] == TypeListProbe)
                {
                    // A newer probe version than we know still gets a version-1 reply, which every version-2 reader accepts.
                    if (!LanNetwork.IsOnLink(received.RemoteEndPoint.Address, allowLoopback)) continue;
                    var listReply = new byte[Magic.Length + 2 + id.Length + 2 + name.Length];
                    Magic.CopyTo(listReply, 0);
                    listReply[Magic.Length] = TypeListReply;
                    listReply[Magic.Length + 1] = ListVersion;
                    Encoding.ASCII.GetBytes(id).CopyTo(listReply, Magic.Length + 2);
                    BinaryPrimitives.WriteUInt16BigEndian(listReply.AsSpan(Magic.Length + 2 + id.Length), (ushort)tcpPort());
                    name.CopyTo(listReply, Magic.Length + 2 + id.Length + 2);
                    try { await udp.SendAsync(listReply, received.RemoteEndPoint, ct).ConfigureAwait(false); }
                    catch (SocketException) { }
                    continue;
                }
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
