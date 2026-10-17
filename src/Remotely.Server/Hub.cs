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
public sealed class Hub(DeviceStore store, PairingCodes codes, RuntimeSettings settings, AuditLog audit, ServerMetrics metrics, RateLimits limits,
    TimeProvider time, ILogger<Hub> log)
{
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KickGrace = TimeSpan.FromSeconds(2);

    private sealed record Session(Guid Id, DeviceConnection Initiator, DeviceConnection Responder, DateTimeOffset Started);

    private readonly ConcurrentDictionary<string, DeviceConnection> _online = new();
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<DeviceConnection, byte> _connections = new();
    private readonly object _pairGate = new();
    private volatile bool _stopping;

    public bool IsOnline(string deviceId) => _online.ContainsKey(deviceId);
    public int OnlineCount => _online.Count;
    public int SessionCount => _sessions.Count;
    public bool IsStopping => _stopping;
    public IReadOnlyList<string> OnlineIds() => [.. _online.Keys];

    public async Task RunAsync(DeviceConnection conn, CancellationToken ct)
    {
        var authenticated = false;
        _connections[conn] = 0;
        try
        {
            if (_stopping || !await AuthenticateAsync(conn, ct).ConfigureAwait(false)) return;
            authenticated = true;
            store.Touch(conn.DeviceId, time.GetUtcNow());
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
            if (authenticated) store.Touch(conn.DeviceId, time.GetUtcNow());
            await DropSessionsAsync(conn, "Peer disconnected").ConfigureAwait(false);
            await CloseQuietly(conn).ConfigureAwait(false);
            _connections.TryRemove(conn, out _);
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
        if (device is { Blocked: false })
        {
            await conn.SendAsync(new Authenticated(device.DeviceId, device.Name), timeout.Token).ConfigureAwait(false);
            return true;
        }

        // A blocked device looks unpaired to itself and is told why if it tries to pair again; a valid code does not get it back in.
        var blocked = device is not null;
        if (blocked)
        {
            audit.Write("auth", conn.DeviceId, null, "refused", "The device is blocked.");
            log.LogInformation("Blocked device {Id} tried to connect from {Remote}", DeviceIds.Short(conn.DeviceId), conn.Remote);
        }

        await conn.SendAsync(new NotPaired(), timeout.Token).ConfigureAwait(false);
        for (var attempts = 0; attempts < 5; attempts++)
        {
            if (await ReadControlAsync(conn, timeout.Token).ConfigureAwait(false) is not PairRequest pair) return false;
            var refusal = blocked ? "This device has been blocked by the server." : TryPair(conn, pair);
            if (refusal is not null)
            {
                await conn.SendAsync(new PairResult(false, refusal), timeout.Token).ConfigureAwait(false);
                continue;
            }
            var name = store.Find(conn.DeviceId)!.Name;
            await conn.SendAsync(new PairResult(true, null), timeout.Token).ConfigureAwait(false);
            await conn.SendAsync(new Authenticated(conn.DeviceId, name), timeout.Token).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    /// <summary>Checks the limits and the code and, if all is well, registers the device. Returns the reason to show the device, or null on success.</summary>
    private string? TryPair(DeviceConnection conn, PairRequest pair)
    {
        string name;
        switch (limits.Pair.Hit(conn.Remote))
        {
            case RateResult.DeniedFirst:
                log.LogWarning("Too many pairing attempts from {Remote}; refusing for a while", conn.Remote);
                audit.Write("pair", conn.DeviceId, null, "rate-limited", $"from {conn.Remote}");
                goto case RateResult.Denied;
            case RateResult.Denied:
                metrics.PairingRateLimited();
                return "Too many attempts. Wait a minute and try again.";
        }

        lock (_pairGate)
        {
            if (codes.IsLockedOut(conn.Remote) || !codes.Exists(pair.Code))
            {
                codes.TryConsume(conn.Remote, pair.Code);   // counts the miss against this address
                metrics.PairingFailed();
                audit.Write("pair", conn.DeviceId, null, "failed", $"Wrong, used or expired code from {conn.Remote}");
                return "Invalid or expired code.";
            }
            if (store.Count >= settings.MaxDevices)
            {
                metrics.PairingFailed();
                audit.Write("pair", conn.DeviceId, null, "refused", $"The server already has {store.Count} devices (limit {settings.MaxDevices})");
                return $"This server has reached its limit of {settings.MaxDevices} devices.";
            }
            if (!codes.TryConsume(conn.Remote, pair.Code))
            {
                metrics.PairingFailed();
                return "Invalid or expired code.";
            }
            name = CleanName(pair.Name);
            store.Add(new DeviceRecord(conn.DeviceId, name, conn.PublicKey, pair.CanHost, time.GetUtcNow()));
        }
        metrics.PairingSucceeded();
        audit.Write("pair", conn.DeviceId, null, "ok", $"'{name}' ({(pair.CanHost ? "can be controlled" : "viewer only")}) from {conn.Remote}");
        log.LogInformation("Paired {Id} as '{Name}'", DeviceIds.Short(conn.DeviceId), name);
        return null;
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
                    await conn.SendAsync(new DeviceList([.. store.All().Where(d => !d.Blocked).Select(d => new DeviceInfo(d.DeviceId, d.Name, d.CanHost, IsOnline(d.DeviceId)))]), ct).ConfigureAwait(false);
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
        DeviceRecord? target = null;
        var rate = limits.Session.Hit(viewer.DeviceId);
        if (rate != RateResult.Allowed)
        {
            failure = "Too many connection attempts. Wait a minute and try again.";
            if (rate == RateResult.DeniedFirst) log.LogWarning("Device {Id} is opening sessions too fast", DeviceIds.Short(viewer.DeviceId));
        }
        else
        {
            target = targetId.Length == DeviceIds.ByteLength * 2 ? store.Find(targetId.ToLowerInvariant()) : null;
            var max = settings.MaxSessionsPerDevice;
            if (target is null) failure = "Unknown device.";
            else if (target.DeviceId == viewer.DeviceId) failure = "A device cannot connect to itself.";
            else if (!target.CanHost) failure = "That device does not accept connections.";
            else if (target.Blocked || !_online.TryGetValue(target.DeviceId, out host)) failure = "That device is offline.";
            else if (_sessions.Values.Count(x => x.Initiator == viewer || x.Responder == viewer) >= max
                     || _sessions.Values.Count(x => x.Initiator == host || x.Responder == host) >= max)
                failure = "Too many sessions.";
        }

        if (failure is not null || host is null)
        {
            metrics.SessionRefused();
            audit.Write("connect", viewer.DeviceId, target?.DeviceId ?? Truncate(targetId), "failed", failure ?? "Unavailable.");
            await viewer.SendAsync(new ConnectFailed(failure ?? "Unavailable."), ct).ConfigureAwait(false);
            return;
        }

        var session = new Session(Guid.NewGuid(), viewer, host, time.GetUtcNow());
        _sessions[session.Id] = session;
        metrics.SessionOpened();
        audit.Write("connect", viewer.DeviceId, host.DeviceId, "ok", $"session {session.Id:N}");
        await host.SendAsync(new SessionOpened(session.Id, viewer.DeviceId, IsInitiator: false), ct).ConfigureAwait(false);
        await viewer.SendAsync(new SessionOpened(session.Id, host.DeviceId, IsInitiator: true), ct).ConfigureAwait(false);
    }

    private static string Truncate(string s) => s.Length > 64 ? s[..64] : s;

    private async Task RelayAsync(DeviceConnection from, byte[] frame, CancellationToken ct)
    {
        if (!RelayFrame.TryParse(frame, out var id) || !_sessions.TryGetValue(id, out var session)) return;
        var to = ReferenceEquals(session.Initiator, from) ? session.Responder
               : ReferenceEquals(session.Responder, from) ? session.Initiator
               : null;
        if (to is null) return;
        metrics.FrameRelayed(frame.Length);
        await to.SendRelayAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task EndSessionAsync(Session session, DeviceConnection endedBy, string reason)
    {
        if (!_sessions.TryRemove(session.Id, out _)) return;
        audit.Write("session.end", session.Initiator.DeviceId, session.Responder.DeviceId, "ok",
            $"{reason}; lasted {(time.GetUtcNow() - session.Started).TotalSeconds:0} s");
        var other = ReferenceEquals(session.Initiator, endedBy) ? session.Responder : session.Initiator;
        try { await other.SendAsync(new SessionClosed(session.Id, reason)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    }

    private async Task DropSessionsAsync(DeviceConnection conn, string reason)
    {
        foreach (var s in _sessions.Values.Where(x => ReferenceEquals(x.Initiator, conn) || ReferenceEquals(x.Responder, conn)).ToList())
            await EndSessionAsync(s, conn, reason).ConfigureAwait(false);
    }

    /// <summary>Disconnects a device now (revoked, blocked, or no longer in a restored device list). Its sessions end; a client that ignores the close is cut off after a short grace period.</summary>
    public async Task KickAsync(string deviceId, string reason)
    {
        if (!_online.TryGetValue(deviceId, out var conn)) return;
        _online.TryRemove(new KeyValuePair<string, DeviceConnection>(deviceId, conn));
        await DropSessionsAsync(conn, reason).ConfigureAwait(false);
        await CloseQuietly(conn, WebSocketCloseStatus.PolicyViolation, reason).ConfigureAwait(false);
        _ = Task.Run(async () =>
        {
            await Task.Delay(KickGrace).ConfigureAwait(false);
            try { conn.Socket.Abort(); } catch (ObjectDisposedException) { }
        });
    }

    /// <summary>Starts the shutdown: refuses new connections, tells every device the server is stopping and, after a short grace period, cuts off those that did not leave.</summary>
    public async Task CloseAllAsync(string reason)
    {
        _stopping = true;
        var all = _connections.Keys.ToList();
        await Task.WhenAll(all.Select(c => CloseQuietly(c, WebSocketCloseStatus.EndpointUnavailable, reason))).ConfigureAwait(false);
        var deadline = time.GetUtcNow() + KickGrace;
        while (!_connections.IsEmpty && time.GetUtcNow() < deadline) await Task.Delay(50).ConfigureAwait(false);
        foreach (var c in _connections.Keys)
        {
            try { c.Socket.Abort(); } catch (ObjectDisposedException) { }
        }
    }

    private static async Task<ControlMessage?> ReadControlAsync(DeviceConnection conn, CancellationToken ct)
    {
        var m = await WebSocketMessages.ReceiveAsync(conn.Socket, ProtocolInfo.MaxControlBytes, ct).ConfigureAwait(false);
        if (m is not { Type: WebSocketMessageType.Text }) return null;
        try { return ControlCodec.Deserialize(m.Value.Data); }
        catch (FormatException) { return null; }
    }

    private static async Task CloseQuietly(DeviceConnection conn, WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string? description = null)
    {
        try
        {
            if (conn.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await conn.Socket.CloseOutputAsync(status, description, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    public static string CleanName(string name)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Unnamed device";
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }
}
