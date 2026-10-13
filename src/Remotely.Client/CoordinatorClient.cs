using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Remotely.Core.Crypto;
using Remotely.Core.Net;
using Remotely.Core.Protocol;

namespace Remotely.Client;

public sealed class CoordinatorException(string message) : Exception(message);

/// <summary>Connection from this device to the coordinator: authenticates with the device key, pairs, lists devices, opens relayed sessions.</summary>
public sealed class CoordinatorClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly CoordinatorOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RelaySession> _sessions = new();
    private readonly Channel<RelaySession> _incoming = Channel.CreateUnbounded<RelaySession>();
    private readonly Channel<ControlMessage> _control = Channel.CreateUnbounded<ControlMessage>();
    private Task _pump = Task.CompletedTask;

    private CoordinatorClient(ClientWebSocket socket, CoordinatorOptions options, DeviceIdentity identity)
    {
        _socket = socket;
        _options = options;
        Identity = identity;
    }

    public DeviceIdentity Identity { get; }

    /// <summary>False until <see cref="PairAsync"/> succeeds on a device the server has not seen before.</summary>
    public bool IsPaired { get; private set; }

    /// <summary>Sessions other devices have opened to this device (host side).</summary>
    public ChannelReader<RelaySession> IncomingSessions => _incoming.Reader;

    public static async Task<CoordinatorClient> ConnectAsync(CoordinatorOptions options, DeviceIdentity identity, CancellationToken ct = default)
    {
        var secure = options.ServerUri.Scheme == "wss";
        if (!secure && !(options.AllowInsecureTransport && options.ServerUri.Scheme == "ws"))
            throw new CoordinatorException("Refusing an unencrypted connection to the coordinator.");
        if (secure && string.IsNullOrWhiteSpace(options.PinnedServerKey))
            throw new CoordinatorException("A pinned server key is required for wss.");

        var handler = new SocketsHttpHandler();
        if (secure)
        {
            var pin = options.PinnedServerKey!.Trim().ToLowerInvariant();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                using var x509 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
                var actual = Convert.ToHexStringLower(SHA256.HashData(x509.PublicKey.ExportSubjectPublicKeyInfo()));
                return CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(pin));
            };
        }

        var socket = new ClientWebSocket();
        var client = new CoordinatorClient(socket, options, identity);
        try
        {
            using var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
            await socket.ConnectAsync(options.ServerUri, invoker, ct).ConfigureAwait(false);
            await client.HandshakeAsync(ct).ConfigureAwait(false);
            if (client.IsPaired) client._pump = Task.Run(client.PumpAsync);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        var hello = await ReceiveControlAsync(ct).ConfigureAwait(false) as ServerHello
                    ?? throw new CoordinatorException("Unexpected reply from server.");
        if (hello.ProtocolVersion != ProtocolInfo.Version) throw new CoordinatorException("Server speaks a different protocol version.");
        var signed = new FieldWriter().Field(System.Text.Encoding.ASCII.GetBytes(ProtocolInfo.AuthLabel)).Field(hello.Challenge).ToArray();
        await SendControlAsync(new AuthResponse(Identity.PublicKey, Identity.Sign(signed)), ct).ConfigureAwait(false);
        IsPaired = await ReceiveControlAsync(ct).ConfigureAwait(false) switch
        {
            Authenticated => true,
            NotPaired => false,
            _ => throw new CoordinatorException("Unexpected reply from server."),
        };
    }

    /// <summary>Registers this device using a one-time code from the server's admin API.</summary>
    public async Task PairAsync(string code, string deviceName, bool canHost, CancellationToken ct = default)
    {
        if (IsPaired) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);
        await SendControlAsync(new PairRequest(code, deviceName, canHost), timeout.Token).ConfigureAwait(false);
        var result = await ReceiveControlAsync(timeout.Token).ConfigureAwait(false) as PairResult
                     ?? throw new CoordinatorException("Unexpected reply from server.");
        if (!result.Ok) throw new CoordinatorException(result.Error ?? "Pairing refused.");
        if (await ReceiveControlAsync(timeout.Token).ConfigureAwait(false) is not Authenticated)
            throw new CoordinatorException("Unexpected reply from server.");
        IsPaired = true;
        _pump = Task.Run(PumpAsync);
    }

    public async Task<DeviceInfo[]> ListDevicesAsync(CancellationToken ct = default) =>
        (await RequestAsync(new ListDevices(), m => m is DeviceList, ct).ConfigureAwait(false) as DeviceList)!.Devices;

    /// <summary>Opens a relayed session to a host. Run <see cref="SecureSession.InitiateAsync"/> on it next.</summary>
    public async Task<RelaySession> ConnectToAsync(string hostDeviceId, CancellationToken ct = default)
    {
        var reply = await RequestAsync(new ConnectRequest(hostDeviceId), m => m is SessionOpened { IsInitiator: true } or ConnectFailed, ct).ConfigureAwait(false);
        if (reply is ConnectFailed failed) throw new CoordinatorException(failed.Reason);
        return _sessions[((SessionOpened)reply).SessionId];
    }

    private async Task<ControlMessage> RequestAsync(ControlMessage request, Func<ControlMessage, bool> isReply, CancellationToken ct)
    {
        if (!IsPaired) throw new CoordinatorException("This device is not paired with the server.");
        await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            timeout.CancelAfter(_options.RequestTimeout);
            await SendControlAsync(request, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var m = await _control.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (isReply(m)) return m;
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var m = await WebSocketMessages.ReceiveAsync(_socket, ProtocolInfo.MaxRelayBytes + RelayFrame.HeaderLength, _lifetime.Token).ConfigureAwait(false);
                if (m is null) break;
                if (m.Value.Type == WebSocketMessageType.Binary)
                {
                    if (RelayFrame.TryParse(m.Value.Data, out var id) && _sessions.TryGetValue(id, out var s))
                        await s.DeliverAsync(m.Value.Data[RelayFrame.HeaderLength..], _lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                var control = ControlCodec.Deserialize(m.Value.Data);
                switch (control)
                {
                    case SessionOpened opened:
                        var session = new RelaySession(opened.SessionId, opened.PeerDeviceId, opened.IsInitiator, SendRelayAsync, CloseSessionAsync);
                        _sessions[opened.SessionId] = session; // registered before anything else can arrive on this socket
                        if (!opened.IsInitiator) _incoming.Writer.TryWrite(session);
                        else _control.Writer.TryWrite(control);
                        break;
                    case SessionClosed closed:
                        if (_sessions.TryRemove(closed.SessionId, out var ended)) ended.Complete();
                        break;
                    default:
                        _control.Writer.TryWrite(control);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException or FormatException) { }
        finally
        {
            foreach (var s in _sessions.Values) s.Complete();
            _sessions.Clear();
            _incoming.Writer.TryComplete();
            _control.Writer.TryComplete();
        }
    }

    private Task SendRelayAsync(Guid id, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > ProtocolInfo.MaxRelayBytes) throw new ArgumentException("Payload too large; split it.", nameof(payload));
        return SendAsync(RelayFrame.Build(id, payload.Span), WebSocketMessageType.Binary, ct);
    }

    private async Task CloseSessionAsync(Guid id)
    {
        if (_sessions.TryRemove(id, out var s)) s.Complete();
        try { await SendControlAsync(new CloseSession(id), _lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private Task SendControlAsync(ControlMessage m, CancellationToken ct) =>
        SendAsync(ControlCodec.Serialize(m), WebSocketMessageType.Text, ct);

    private async Task SendAsync(byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try { await _socket.SendAsync(data, type, true, ct).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private async Task<ControlMessage> ReceiveControlAsync(CancellationToken ct)
    {
        var m = await WebSocketMessages.ReceiveAsync(_socket, ProtocolInfo.MaxControlBytes, ct).ConfigureAwait(false);
        if (m is not { Type: WebSocketMessageType.Text }) throw new CoordinatorException("Server closed the connection.");
        return ControlCodec.Deserialize(m.Value.Data);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _pump.ConfigureAwait(false); } catch { /* pump swallows its own errors */ }
        _socket.Dispose();
        _lifetime.Dispose();
    }
}
