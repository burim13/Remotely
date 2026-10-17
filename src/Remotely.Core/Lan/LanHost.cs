using System.Net;
using System.Net.Sockets;
using Remotely.Core.Crypto;
using Remotely.Core.Lan.Consent;

namespace Remotely.Core.Lan;

public sealed class LanHostOptions
{
    /// <summary>0 picks any free port.</summary>
    public int TcpPort { get; init; } = LanNetwork.TcpPort;
    public int DiscoveryPort { get; init; } = LanNetwork.DiscoveryPort;
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxFailures { get; init; } = 3;
    public string HostName { get; init; } = Environment.MachineName;
    /// <summary>Accept connections from this PC itself (tests only). Normal use accepts only addresses on the same subnet.</summary>
    public bool AllowLoopback { get; init; }
    /// <summary>
    /// The consent rules. Null means every PC that knows the token is let in (used by tests and by headless self-tests); the app always supplies one.
    /// </summary>
    public ConsentGate? Consent { get; init; }
    /// <summary>This PC's long-term key. Needed to prove the host to a controller (so it can be remembered) and to accept remembered PCs without a token.</summary>
    public DeviceIdentity? Identity { get; init; }
    /// <summary>Accept PCs from the remembered list without a token. Needs <see cref="Identity"/> and <see cref="Consent"/>.</summary>
    public bool AllowResume { get; init; } = true;
}

/// <summary>How a PC was let in and when. <see cref="Via"/> is "token" or "remembered".</summary>
public sealed record LanSessionInfo(string ControllerName, string Address, string? DeviceId, string Via, ConsentOutcome Outcome, DateTimeOffset StartedUtc);

/// <summary>
/// The PC that is being controlled, on the local network: shows a token, answers "who has this label?" on the network, and lets in the one PC that
/// proves it knows the token. A token works once, expires, and is replaced after 3 wrong tries. Only PCs on the same subnet are listened to.
/// </summary>
public sealed class LanHost : IAsyncDisposable
{
    private const int MaxHandshakesAtOnce = 8;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(12);

    private readonly LanHostOptions _options;
    private readonly Func<IMessageChannel, string, CancellationToken, Task> _serve;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private TcpListener? _listener;
    private UdpClient? _udp;
    private LanTokenState _state;
    private int _handshakes;
    private int _active;
    private Task _acceptLoop = Task.CompletedTask;
    private Task _discoveryLoop = Task.CompletedTask;
    private Task _expiryLoop = Task.CompletedTask;
    private HostSessionChannel? _channel;
    private CancellationTokenSource? _sessionCts;
    private bool _endedByHost;

    public LanHost(LanHostOptions options, Func<IMessageChannel, string, CancellationToken, Task> serve, Action<string> log)
    {
        _options = options;
        _serve = serve;
        _log = log;
        _state = NewState();
    }

    public int TcpPort { get; private set; }
    public LanToken CurrentToken { get { lock (_gate) return _state.Token; } }
    public DateTimeOffset TokenExpiresUtc { get { lock (_gate) return _state.ExpiresUtc; } }
    public bool IsConnected => Volatile.Read(ref _active) != 0;
    public bool ControlPaused => _channel?.Paused ?? false;

    /// <summary>Ends the current session (the other PC is disconnected); sharing continues with a fresh token.</summary>
    public void EndSession()
    {
        CancellationTokenSource? cts;
        lock (_gate) { cts = _sessionCts; _endedByHost = true; }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>"Pause control": the connected PC keeps watching but cannot act on this PC. No effect when nobody is in a session.</summary>
    public async Task SetControlPausedAsync(bool paused, string noticeForViewer)
    {
        HostSessionChannel? channel;
        lock (_gate) channel = _channel;
        if (channel is not null) await channel.SetPausedAsync(paused, noticeForViewer).ConfigureAwait(false);
    }

    /// <summary>A new token is being shown (the old one was used, expired, or had too many wrong tries).</summary>
    public event Action? TokenChanged;
    /// <summary>Arguments: the other PC's name and its address.</summary>
    public event Action<string, string>? ControllerConnected;
    public event Action? ControllerLeft;
    /// <summary>A PC opened a connection that looks like a Remotely handshake (argument: its address).</summary>
    public event Action<string>? HandshakeStarted;
    /// <summary>That handshake ended without a session (wrong token, not remembered, timeout).</summary>
    public event Action? HandshakeFailed;
    /// <summary>The token was right and the person is being asked.</summary>
    public event Action<ConsentRequest>? ConsentRequested;
    /// <summary>The answer (including automatic ones and refusals). Raised before <see cref="SessionStarted"/>.</summary>
    public event Action<ConsentRequest, ConsentResult>? ConsentResolved;
    public event Action<LanSessionInfo>? SessionStarted;
    /// <summary>Second argument: why it ended (for example "stopped by you" or "the other PC left").</summary>
    public event Action<LanSessionInfo, string>? SessionEnded;
    /// <summary>A sentence for the person at this PC, for example that the token was replaced after wrong tries.</summary>
    public event Action<string>? Notice;

    private LanTokenState NewState() => new(LanToken.Create(), DateTimeOffset.UtcNow + _options.TokenLifetime);

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _options.TcpPort);
        try { _listener.Start(16); }
        catch (SocketException) when (_options.TcpPort != 0)
        {
            _listener = new TcpListener(IPAddress.Any, 0);   // another Remotely already uses the usual port: take a free one (discovery tells the other PC which)
            _listener.Start(16);
        }
        TcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        try
        {
            _udp = LanDiscovery.OpenResponder(_options.DiscoveryPort);
            _discoveryLoop = Task.Run(() => LanDiscovery.ServeAsync(_udp, () => CurrentToken.Id, () => TcpPort, _options.HostName, _options.AllowLoopback, _cts.Token));
        }
        catch (SocketException ex) { _log("Discovery is unavailable (" + ex.Message + "). The other PC can still connect by this PC's address."); }
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _expiryLoop = Task.Run(ExpiryLoopAsync);
        _log($"Sharing on port {TcpPort}. Token {CurrentToken.Display}.");
    }

    /// <summary>Shows a fresh token now (the person asked for one).</summary>
    public void NewToken() => Rotate(null);

    private void Rotate(string? notice)
    {
        var next = NewState();   // stretches the secret: do it outside the lock
        lock (_gate) _state = next;
        if (notice is not null) Notice?.Invoke(notice);
        TokenChanged?.Invoke();
    }

    private async Task ExpiryLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                if (DateTimeOffset.UtcNow >= TokenExpiresUtc && !IsConnected) Rotate(null);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task<byte[]> ReadFirstFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is < 5 or > 4096) throw new InvalidDataException("Not a handshake.");   // hello messages are small: never buffer more than this for a stranger
        var frame = new byte[4 + (int)length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(4), ct).ConfigureAwait(false);
        return frame;
    }

    private async Task HandleAsync(TcpClient client)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        var counted = 0;
        var announced = false;
        var started = false;
        void Release() { if (Interlocked.Exchange(ref counted, 0) == 1) Interlocked.Decrement(ref _handshakes); }
        try
        {
            if (!LanNetwork.IsOnLink(remote, _options.AllowLoopback)) { client.Dispose(); return; }   // not on this network: never even answered
            if (IsConnected) { client.Dispose(); return; }
            Interlocked.Increment(ref _handshakes);
            counted = 1;
            if (Volatile.Read(ref _handshakes) > MaxHandshakesAtOnce) { Release(); client.Dispose(); return; }
            client.NoDelay = true;
            var stream = client.GetStream();

            byte[] first;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(HandshakeTimeout);
                first = await ReadFirstFrameAsync(stream, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or SocketException)
            {
                client.Dispose();
                return;
            }
            var resume = LanResume.IsResumeHello(first.AsSpan(4));
            if (!resume && !first.AsSpan(4, 4).SequenceEqual("RMTL"u8)) { client.Dispose(); return; }

            announced = true;
            HandshakeStarted?.Invoke(remote.ToString());
            started = resume
                ? await ResumeFlowAsync(new FramedStream(stream), first[4..], remote, Release).ConfigureAwait(false)
                : await TokenFlowAsync(new FramedStream(new ReplayStream(first, stream)), remote, Release).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException)
        {
            client.Dispose();
        }
        finally
        {
            Release();
            if (announced && !started) HandshakeFailed?.Invoke();
        }
    }

    /// <summary>The usual way in: the other PC knows the token. Returns true when a session ran.</summary>
    private async Task<bool> TokenFlowAsync(FramedStream wire, IPAddress remote, Action release)
    {
        LanTokenState state;
        lock (_gate) state = _state;

        LanSecureSession session;
        string rawName;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(HandshakeTimeout);
            (session, rawName) = await LanSecureSession.AcceptAsync(wire, state, timeout.Token).ConfigureAwait(false);
        }
        catch (LanAuthException)
        {
            _log($"A wrong token from {remote}.");
            var failures = state.AddFailure();
            bool current; lock (_gate) current = ReferenceEquals(_state, state);
            if (current && failures >= _options.MaxFailures) Rotate("The token was replaced after too many wrong tries.");
            wire.Dispose();
            return false;
        }
        catch (Exception ex) when (ex is Core.Crypto.HandshakeException or IOException or OperationCanceledException or InvalidDataException or SocketException)
        {
            wire.Dispose();
            return false;
        }
        finally { release(); }

        bool stillValid; lock (_gate) stillValid = ReferenceEquals(_state, state) && DateTimeOffset.UtcNow < state.ExpiresUtc;
        if (!stillValid || Interlocked.CompareExchange(ref _active, 1, 0) != 0) { session.Dispose(); return false; }
        Rotate(null);   // single use: the next connection needs the next token

        var address = remote.ToString();
        var hello = ControllerHello.Parse(rawName);
        ProvenDevice? device = null;
        ConsentResult result;
        try
        {
            if (hello.SupportsDeviceProof && _options.Identity is { } identity)
            {
                device = await DeviceProof.ChallengeAsync(session, identity, _options.HostName, hello.Name, TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
                if (device is null) { _log($"{hello.Name} ({remote}) did not prove its device key."); session.Dispose(); Volatile.Write(ref _active, 0); return false; }
            }

            var request = new ConsentRequest(hello.Name, address, device?.DeviceId, device is not null, DateTimeOffset.UtcNow);
            if (_options.Consent is { } gate)
            {
                void Relay(ConsentRequest r) => ConsentRequested?.Invoke(r);
                gate.Prompting += Relay;
                try { result = await gate.DecideAsync(hello.Name, address, device?.PublicKey, device?.DeviceId, _cts.Token).ConfigureAwait(false); }
                finally { gate.Prompting -= Relay; }
            }
            else result = new(ConsentOutcome.AllowedWithoutAsking);
            ConsentResolved?.Invoke(request, result);

            if (device is not null)
                await DeviceProof.SendDecisionAsync(session, result.Allowed, result.Outcome == ConsentOutcome.AllowedAlways, result.Allowed ? null : "The person at the other PC did not allow the connection.", _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Core.Crypto.HandshakeException or IOException or OperationCanceledException or InvalidDataException or SocketException or System.Security.Cryptography.CryptographicException)
        {
            session.Dispose();
            Volatile.Write(ref _active, 0);
            return false;
        }

        if (!result.Allowed)
        {
            _log($"{hello.Name} ({remote}) was not let in ({result.Outcome}).");
            session.Dispose();
            Volatile.Write(ref _active, 0);
            return false;
        }
        await RunSessionAsync(session, new LanSessionInfo(hello.Name, address, device?.DeviceId, "token", result.Outcome, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        return true;
    }

    /// <summary>A remembered PC comes back without a token. Returns true when a session ran.</summary>
    private async Task<bool> ResumeFlowAsync(FramedStream wire, byte[] hello1, IPAddress remote, Action release)
    {
        var gate = _options.Consent;
        if (!_options.AllowResume || _options.Identity is not { } identity || gate is null || !gate.Options.AllowRemembered)
        {
            release();
            wire.Dispose();
            return false;
        }
        IMessageChannel channel;
        RememberedDevice device;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(HandshakeTimeout);
            (channel, device) = await LanResume.AcceptAsync(wire, hello1, identity, gate.Remembered, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Core.Crypto.HandshakeException or IOException or OperationCanceledException or InvalidDataException or SocketException)
        {
            _log($"A PC that is not on the remembered list tried to reconnect from {remote}.");
            wire.Dispose();
            return false;
        }
        finally { release(); }

        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) { channel.Dispose(); return false; }
        var address = remote.ToString();
        var now = DateTimeOffset.UtcNow;
        gate.Remembered.Touch(device.DeviceId, address, now);
        ConsentResolved?.Invoke(new ConsentRequest(device.Name, address, device.DeviceId, true, now), new ConsentResult(ConsentOutcome.AllowedRemembered));
        await RunSessionAsync(channel, new LanSessionInfo(device.Name, address, device.DeviceId, "remembered", ConsentOutcome.AllowedRemembered, now)).ConfigureAwait(false);
        return true;
    }

    private async Task RunSessionAsync(IMessageChannel session, LanSessionInfo info)
    {
        var wrapped = new HostSessionChannel(session);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        lock (_gate) { _channel = wrapped; _sessionCts = sessionCts; _endedByHost = false; }
        _log($"{info.ControllerName} ({info.Address}) connected.");
        ControllerConnected?.Invoke(info.ControllerName, info.Address);
        SessionStarted?.Invoke(info);
        try { await _serve(wrapped, info.ControllerName, sessionCts.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Security.Cryptography.CryptographicException or InvalidDataException or SocketException) { }
        finally
        {
            bool byHost;
            lock (_gate) { byHost = _endedByHost; _channel = null; _sessionCts = null; }
            wrapped.Dispose();
            Volatile.Write(ref _active, 0);
            _log($"{info.ControllerName} left.");
            ControllerLeft?.Invoke();
            SessionEnded?.Invoke(info, byHost ? SessionEndReasons.StoppedByHost : _cts.IsCancellationRequested ? SessionEndReasons.SharingStopped : SessionEndReasons.ControllerLeft);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _udp?.Dispose();
        try { await Task.WhenAll(_acceptLoop, _discoveryLoop, _expiryLoop).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        _cts.Dispose();
    }
}
