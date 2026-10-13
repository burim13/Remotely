using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Remotely.Core.Crypto;
using Remotely.Core.Net;
using Remotely.Core.Protocol;

namespace Remotely.Server;

/// <summary>
/// Device registry, pairing, signalling and blind relay. The relay forwards opaque bytes between the two ends of a session and
/// never sees session keys: peers authenticate and encrypt end to end (see Remotely.Core.Crypto.Handshake).
/// </summary>
public sealed class Hub(DeviceStore store, PairingCodes codes, TimeProvider time, ILogger<Hub> log)
{
    private const int MaxSessionsPerDevice = 8;
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(15);

    private sealed record Session(Guid Id, DeviceConnection Initiator, DeviceConnection Responder);

    private readonly ConcurrentDictionary<string, DeviceConnection> _online = new();
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public bool IsOnline(string deviceId) => _online.ContainsKey(deviceId);

    public async Task RunAsync(DeviceConnection conn, CancellationToken ct)
    {
        try
        {
            if (!await AuthenticateAsync(conn, ct).ConfigureAwait(false)) return;
            _online.AddOrUpdate(conn.DeviceId, conn, (key, old) => { _ = CloseQuietly(old); return conn; });
            log.LogInformation("Device {Id} online", DeviceIds.Short(conn.DeviceId));
            await LoopAsync(conn, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException or IOException)
        {
            log.LogDebug("Connection {Id} ended: {Reason}", DeviceIds.Short(conn.DeviceId), ex.Message);
        }
        finally
        {
            if (conn.DeviceId.Length > 0 && _online.TryGetValue(conn.DeviceId, out var current) && ReferenceEquals(current, conn))
                _online.TryRemove(conn.DeviceId, out _);
            await DropSessionsAsync(conn, "Peer disconnected").ConfigureAwait(false);
            await CloseQuietly(conn).ConfigureAwait(false);
        }
    }

    private async Task<bool> AuthenticateAsync(DeviceConnection conn, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AuthTimeout);

        var challenge = RandomNumberGenerator.GetBytes(32);
        await conn.SendAsync(new ServerHello(challenge, ProtocolInfo.Version), timeout.Token).ConfigureAwait(false);

        var first = await ReadControlAsync(conn, timeout.Token).ConfigureAwait(false);
        if (first is not AuthResponse auth) return false;
        var signed = new Core.Protocol.FieldWriter().Field(System.Text.Encoding.ASCII.GetBytes(ProtocolInfo.AuthLabel)).Field(challenge).ToArray();
        if (!DeviceIdentity.Verify(auth.PublicKey, signed, auth.Signature)) return false;

        conn.PublicKey = auth.PublicKey;
        conn.DeviceId = DeviceIds.FromPublicKey(auth.PublicKey);

        var device = store.Find(conn.DeviceId);
        if (device is not null)
        {
            await conn.SendAsync(new Authenticated(device.DeviceId, device.Name), timeout.Token).ConfigureAwait(false);
            return true;
        }

        await conn.SendAsync(new NotPaired(), timeout.Token).ConfigureAwait(false);
        for (var attempts = 0; attempts < 5; attempts++)
        {
            if (await ReadControlAsync(conn, timeout.Token).ConfigureAwait(false) is not PairRequest pair) return false;
            if (codes.IsLockedOut(conn.Remote) || !codes.TryConsume(conn.Remote, pair.Code))
            {
                await conn.SendAsync(new PairResult(false, "Invalid or expired code."), timeout.Token).ConfigureAwait(false);
                continue;
            }
            var name = CleanName(pair.Name);
            store.Add(new DeviceRecord(conn.DeviceId, name, conn.PublicKey, pair.CanHost, time.GetUtcNow()));
            log.LogInformation("Paired {Id} as '{Name}'", DeviceIds.Short(conn.DeviceId), name);
            await conn.SendAsync(new PairResult(true, null), timeout.Token).ConfigureAwait(false);
            await conn.SendAsync(new Authenticated(conn.DeviceId, name), timeout.Token).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private async Task LoopAsync(DeviceConnection conn, CancellationToken ct)
    {
        while (conn.Socket.State == WebSocketState.Open)
        {
            var message = await WebSocketMessages.ReceiveAsync(conn.Socket, ProtocolInfo.MaxRelayBytes + RelayFrame.HeaderLength, ct).ConfigureAwait(false);
            if (message is null) return;

            if (message.Value.Type == WebSocketMessageType.Binary)
            {
                await RelayAsync(conn, message.Value.Data, ct).ConfigureAwait(false);
                continue;
            }
            if (message.Value.Data.Length > ProtocolInfo.MaxControlBytes) return;

            ControlMessage control;
            try { control = ControlCodec.Deserialize(message.Value.Data); }
            catch (FormatException) { return; }

            switch (control)
            {
                case ListDevices:
                    await conn.SendAsync(new DeviceList([.. store.All().Select(d => new DeviceInfo(d.DeviceId, d.Name, d.CanHost, IsOnline(d.DeviceId)))]), ct).ConfigureAwait(false);
                    break;
                case ConnectRequest connect:
                    await OpenSessionAsync(conn, connect.TargetDeviceId, ct).ConfigureAwait(false);
                    break;
                case CloseSession close:
                    if (_sessions.TryGetValue(close.SessionId, out var s) && (s.Initiator == conn || s.Responder == conn))
                        await EndSessionAsync(s, conn, "Closed by peer").ConfigureAwait(false);
                    break;
                default:
                    return; // unexpected message in this state: drop the connection
            }
        }
    }

    private async Task OpenSessionAsync(DeviceConnection viewer, string targetId, CancellationToken ct)
    {
        string? failure = null;
        DeviceConnection? host = null;
        var target = targetId.Length == DeviceIds.ByteLength * 2 ? store.Find(targetId.ToLowerInvariant()) : null;
        if (target is null) failure = "Unknown device.";
        else if (target.DeviceId == viewer.DeviceId) failure = "A device cannot connect to itself.";
        else if (!target.CanHost) failure = "That device does not accept connections.";
        else if (!_online.TryGetValue(target.DeviceId, out host)) failure = "That device is offline.";
        else if (_sessions.Values.Count(x => x.Initiator == viewer || x.Responder == viewer) >= MaxSessionsPerDevice
                 || _sessions.Values.Count(x => x.Initiator == host || x.Responder == host) >= MaxSessionsPerDevice)
            failure = "Too many sessions.";

        if (failure is not null || host is null)
        {
            await viewer.SendAsync(new ConnectFailed(failure ?? "Unavailable."), ct).ConfigureAwait(false);
            return;
        }

        var session = new Session(Guid.NewGuid(), viewer, host);
        _sessions[session.Id] = session;
        await host.SendAsync(new SessionOpened(session.Id, viewer.DeviceId, IsInitiator: false), ct).ConfigureAwait(false);
        await viewer.SendAsync(new SessionOpened(session.Id, host.DeviceId, IsInitiator: true), ct).ConfigureAwait(false);
    }

    private async Task RelayAsync(DeviceConnection from, byte[] frame, CancellationToken ct)
    {
        if (!RelayFrame.TryParse(frame, out var id) || !_sessions.TryGetValue(id, out var session)) return;
        var to = ReferenceEquals(session.Initiator, from) ? session.Responder
               : ReferenceEquals(session.Responder, from) ? session.Initiator
               : null;
        if (to is not null) await to.SendRelayAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task EndSessionAsync(Session session, DeviceConnection endedBy, string reason)
    {
        if (!_sessions.TryRemove(session.Id, out _)) return;
        var other = ReferenceEquals(session.Initiator, endedBy) ? session.Responder : session.Initiator;
        try { await other.SendAsync(new SessionClosed(session.Id, reason)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    }

    private async Task DropSessionsAsync(DeviceConnection conn, string reason)
    {
        foreach (var s in _sessions.Values.Where(x => ReferenceEquals(x.Initiator, conn) || ReferenceEquals(x.Responder, conn)).ToList())
            await EndSessionAsync(s, conn, reason).ConfigureAwait(false);
    }

    private static async Task<ControlMessage?> ReadControlAsync(DeviceConnection conn, CancellationToken ct)
    {
        var m = await WebSocketMessages.ReceiveAsync(conn.Socket, ProtocolInfo.MaxControlBytes, ct).ConfigureAwait(false);
        if (m is not { Type: WebSocketMessageType.Text }) return null;
        try { return ControlCodec.Deserialize(m.Value.Data); }
        catch (FormatException) { return null; }
    }

    private static async Task CloseQuietly(DeviceConnection conn)
    {
        try
        {
            if (conn.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await conn.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static string CleanName(string name)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Unnamed device";
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }
}
