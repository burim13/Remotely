using System.Net;
using System.Net.Sockets;
using Remotely.Core.Crypto;

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
}

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

    /// <summary>A new token is being shown (the old one was used, expired, or had too many wrong tries).</summary>
    public event Action? TokenChanged;
    /// <summary>Arguments: the other PC's name and its address.</summary>
    public event Action<string, string>? ControllerConnected;
    public event Action? ControllerLeft;
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

    private async Task HandleAsync(TcpClient client)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        FramedStream? wire = null;
        var counted = 0;
        void Release() { if (Interlocked.Exchange(ref counted, 0) == 1) Interlocked.Decrement(ref _handshakes); }
        try
        {
            if (!LanNetwork.IsOnLink(remote, _options.AllowLoopback)) { client.Dispose(); return; }   // not on this network: never even answered
            if (IsConnected) { client.Dispose(); return; }
            Interlocked.Increment(ref _handshakes);
            counted = 1;
            if (Volatile.Read(ref _handshakes) > MaxHandshakesAtOnce) { Release(); client.Dispose(); return; }
            client.NoDelay = true;
            wire = new FramedStream(client.GetStream());
            LanTokenState state;
            lock (_gate) state = _state;

            LanSecureSession session;
            string controller;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(HandshakeTimeout);
                (session, controller) = await LanSecureSession.AcceptAsync(wire, state, timeout.Token).ConfigureAwait(false);
            }
            catch (LanAuthException)
            {
                _log($"A wrong token from {remote}.");
                var failures = state.AddFailure();
                bool current; lock (_gate) current = ReferenceEquals(_state, state);
                if (current && failures >= _options.MaxFailures) Rotate("The token was replaced after too many wrong tries.");
                wire.Dispose();
                return;
            }
            catch (Exception ex) when (ex is Core.Crypto.HandshakeException or IOException or OperationCanceledException or InvalidDataException or SocketException)
            {
                wire.Dispose();
                return;
            }
            finally { Release(); }

            bool stillValid; lock (_gate) stillValid = ReferenceEquals(_state, state) && DateTimeOffset.UtcNow < state.ExpiresUtc;
            if (!stillValid || Interlocked.CompareExchange(ref _active, 1, 0) != 0) { session.Dispose(); return; }
            Rotate(null);   // single use: the next connection needs the next token
            _log($"{controller} ({remote}) connected.");
            ControllerConnected?.Invoke(controller, remote.ToString());
            try { await _serve(session, controller, _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Security.Cryptography.CryptographicException or InvalidDataException or SocketException) { }
            finally
            {
                session.Dispose();
                Volatile.Write(ref _active, 0);
                _log($"{controller} left.");
                ControllerLeft?.Invoke();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException)
        {
            wire?.Dispose();
        }
        finally { Release(); }
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
