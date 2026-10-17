using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.Core.Lan.Consent;
using Remotely.HostCore;
using Remotely.Unattended;

namespace Remotely.Host.Unattended;

/// <summary>What the pipe server asks of the service. Everything is already known to come from the signed-in user.</summary>
public interface IUnattendedBackend
{
    Task<PipeReply> HandleAsync(PipeRequest request, CancellationToken ct);
}

public sealed class ServiceLanOptions
{
    /// <summary>Makes what a session may do (screen through the helper, input, clipboard, files). Called each time listening starts.</summary>
    public required Func<HostServices> Services { get; init; }
    public required DeviceIdentity Identity { get; init; }
    public required CachedSettings Settings { get; init; }
    public required ConsentRelay Relay { get; init; }
    public required SessionActivity Activity { get; init; }
    public required Func<bool> HelperAttached { get; init; }
    public required Action<string> Log { get; init; }
    public int TcpPort { get; init; } = LanNetwork.TcpPort;
    public int DiscoveryPort { get; init; } = LanNetwork.DiscoveryPort;
    /// <summary>Tests only: accept connections from the same PC.</summary>
    public bool AllowLoopback { get; init; }
}

/// <summary>
/// Same-network token sharing run by the service, so a token session reaches the lock screen and UAC like a session through the server does. It is the same
/// <see cref="LanHost"/> the app uses (same token, handshake and consent code); what differs is who controls it: the signed-in user's app, over the pipe, starts and stops it and
/// reads the token, and it only starts when an administrator has allowed it. Remembered PCs are not used here: a PC can come in once with a token, never without one.
/// </summary>
public sealed class ServiceLan(ServiceLanOptions options) : IUnattendedBackend, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _change = new(1, 1);
    private LanHost? _host;
    private LanSessionInfo? _session;

    public bool IsListening
    {
        get { lock (_gate) return _host is not null; }
    }

    public async Task<PipeReply> HandleAsync(PipeRequest request, CancellationToken ct)
    {
        options.Relay.NotePoll();
        try
        {
            switch (request.Cmd)
            {
                case PipeCommands.Status:
                    await EnforceSettingsAsync().ConfigureAwait(false);
                    return Reply(true, null);
                case PipeCommands.Start:
                    {
                        var error = await StartAsync().ConfigureAwait(false);
                        return Reply(error is null, error);
                    }
                case PipeCommands.Stop:
                    await StopAsync().ConfigureAwait(false);
                    return Reply(true, null);
                case PipeCommands.NewToken:
                    {
                        LanHost? host; lock (_gate) host = _host;
                        if (host is null) return Reply(false, PipeErrors.NotListening);
                        host.NewToken();
                        return Reply(true, null);
                    }
                case PipeCommands.EndSession:
                    {
                        LanHost? host; lock (_gate) host = _host;
                        host?.EndSession();
                        return Reply(true, null);
                    }
                case PipeCommands.Answer:
                    return options.Relay.Answer(request.QuestionId, request.Decision) ? Reply(true, null) : Reply(false, PipeErrors.NoSuchQuestion);
                default:
                    return Reply(false, PipeErrors.BadRequest);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            options.Log("Unattended pipe command failed: " + ex.Message);
            return Reply(false, PipeErrors.Internal);
        }
    }

    /// <summary>Applies what an administrator changed in the settings file: stops listening when sharing through the service is no longer allowed.</summary>
    public async Task EnforceSettingsAsync()
    {
        LanHost? host; lock (_gate) host = _host;
        if (host is null || options.Settings.Current.LanAllowed) return;
        options.Log("Sharing through the service was switched off; stopping.");
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Starts listening. Returns null when it is listening, otherwise one of <see cref="PipeErrors"/>.</summary>
    public async Task<string?> StartAsync()
    {
        await _change.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate) if (_host is not null) return null;
            var settings = options.Settings.Current;
            if (!settings.LanAllowed) return PipeErrors.NotAllowed;

            var consent = new ConsentOptions { AskBeforeConnect = settings.AskBeforeConnect, AllowRemembered = false };
            var gate = new ConsentGate(consent, new RememberedDevices(null), options.Relay.AskAsync);
            var agent = new HostAgent(options.Identity, options.Services(), options.Log);
            var host = new LanHost(new LanHostOptions
            {
                TcpPort = options.TcpPort,
                DiscoveryPort = options.DiscoveryPort,
                AllowLoopback = options.AllowLoopback,
                Identity = options.Identity,
                Consent = gate,
                AllowResume = false,
            }, (channel, name, ct) => agent.ServeChannelAsync(channel, name, control: true, ct), options.Log);
            host.SessionStarted += OnSessionStarted;
            host.SessionEnded += OnSessionEnded;
            try { host.Start(); }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
            {
                options.Log("Could not listen for tokens: " + ex.Message);
                host.SessionStarted -= OnSessionStarted;
                host.SessionEnded -= OnSessionEnded;
                await host.DisposeAsync().ConfigureAwait(false);
                return PipeErrors.ListenFailed;
            }
            lock (_gate) { _host = host; _consent = consent; }
            options.Log($"Listening for tokens on port {host.TcpPort}.");
            return null;
        }
        finally { _change.Release(); }
    }

    private ConsentOptions? _consent;

    public async Task StopAsync()
    {
        await _change.WaitAsync().ConfigureAwait(false);
        try
        {
            LanHost? host;
            lock (_gate) { host = _host; _host = null; _consent = null; _session = null; }
            if (host is null) return;
            host.SessionStarted -= OnSessionStarted;
            host.SessionEnded -= OnSessionEnded;
            await host.DisposeAsync().ConfigureAwait(false);
            options.Log("Stopped listening for tokens.");
        }
        finally { _change.Release(); }
    }

    private void OnSessionStarted(LanSessionInfo info)
    {
        lock (_gate) _session = info;
    }

    private void OnSessionEnded(LanSessionInfo info, string reason)
    {
        lock (_gate) _session = null;
    }

    public UnattendedStatus Snapshot()
    {
        var settings = options.Settings.Current;
        LanHost? host; LanSessionInfo? session; ConsentOptions? consent;
        lock (_gate) { host = _host; session = _session; consent = _consent; }
        if (consent is not null) consent.AskBeforeConnect = settings.AskBeforeConnect;   // a change by the administrator applies to the next PC that connects
        return new UnattendedStatus
        {
            LanAllowed = settings.LanAllowed,
            AskBeforeConnect = settings.AskBeforeConnect,
            KeepAwake = settings.KeepAwake,
            Listening = host is not null,
            Token = host?.CurrentToken.Display,
            TokenExpiresUtc = host?.TokenExpiresUtc,
            Port = host?.TcpPort ?? 0,
            HelperAttached = options.HelperAttached(),
            CaptureDesktop = options.Activity.CaptureDesktop,
            Controller = session?.ControllerName,
            ControllerAddress = session?.Address,
            SessionSince = session?.StartedUtc,
            Question = options.Relay.Current,
            AwakeHeld = options.Activity.AwakeHeld,
        };
    }

    private PipeReply Reply(bool ok, string? error) => new() { Ok = ok, Error = error, Status = Snapshot() };

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _change.Dispose();
    }
}
