namespace Remotely.Core.Lan.Consent;

/// <summary>Settings of the Share screen that survive a restart.</summary>
public sealed class ShareSettings
{
    public bool AskBeforeConnect { get; set; } = true;
    public bool AllowRemembered { get; set; } = true;

    public static ShareSettings Load(string? path) => JsonFile.TryRead<ShareSettings>(path) ?? new();
    public void Save(string? path) => JsonFile.TryWrite(path, this);
}

/// <summary>
/// Joins the pieces of "Share this PC" that have no screen: the state machine, the consent rules, the remembered PCs and the history. A <see cref="LanHost"/> is attached
/// while sharing is on; its events drive the state machine and write the history, so the screens only have to show <see cref="State"/>.
/// </summary>
public sealed class ShareCoordinator
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private LanHost? _host;
    private Guid? _openRecord;
    private LanSessionInfo? _openInfo;

    /// <param name="dataDirectory">Where remembered PCs, history and settings are kept; null keeps everything in memory (tests).</param>
    /// <param name="prompt">Shows the question to the person ("Allow once / Always allow / Deny"). Null means nobody can be asked, so an unknown PC is refused.</param>
    public ShareCoordinator(string? dataDirectory, Func<ConsentRequest, CancellationToken, Task<ConsentDecision>>? prompt, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        SettingsPath = dataDirectory is null ? null : Path.Combine(dataDirectory, "share-settings.json");
        Settings = ShareSettings.Load(SettingsPath);
        Options = new ConsentOptions { AskBeforeConnect = Settings.AskBeforeConnect, AllowRemembered = Settings.AllowRemembered };
        Remembered = new RememberedDevices(dataDirectory is null ? null : Path.Combine(dataDirectory, "remembered-pcs.json"));
        History = new SessionHistory(dataDirectory is null ? null : Path.Combine(dataDirectory, "share-history.json"));
        State = new ShareStateMachine(clock);
        Gate = new ConsentGate(Options, Remembered, prompt, clock);
    }

    public ShareStateMachine State { get; }
    public ConsentOptions Options { get; }
    public ConsentGate Gate { get; }
    public RememberedDevices Remembered { get; }
    public SessionHistory History { get; }
    public ShareSettings Settings { get; }
    private string? SettingsPath { get; }

    public event Action<LanSessionInfo>? SessionStarted;
    public event Action<LanSessionInfo, string>? SessionEnded;
    /// <summary>A PC was turned away (denied, or nobody answered).</summary>
    public event Action<ConsentRequest, ConsentResult>? Refused;
    /// <summary>A sentence for the person (for example: the token was replaced after wrong tries).</summary>
    public event Action<string>? Notice;

    public void SetAskBeforeConnect(bool ask) { Options.AskBeforeConnect = ask; Settings.AskBeforeConnect = ask; Settings.Save(SettingsPath); }
    public void SetAllowRemembered(bool allow) { Options.AllowRemembered = allow; Settings.AllowRemembered = allow; Settings.Save(SettingsPath); }

    /// <summary>Call after <see cref="LanHost.Start"/> succeeded.</summary>
    public void Attach(LanHost host)
    {
        lock (_gate) _host = host;
        host.HandshakeStarted += OnHandshakeStarted;
        host.HandshakeFailed += OnHandshakeFailed;
        host.ConsentRequested += OnConsentRequested;
        host.ConsentResolved += OnConsentResolved;
        host.SessionStarted += OnSessionStarted;
        host.SessionEnded += OnSessionEnded;
        host.Notice += OnNotice;
        State.Start();
    }

    /// <summary>Call when sharing is turned off (before or after the host is disposed).</summary>
    public void Detach()
    {
        LanHost? host;
        Guid? open;
        LanSessionInfo? openInfo;
        lock (_gate) { host = _host; _host = null; open = _openRecord; openInfo = _openInfo; _openRecord = null; _openInfo = null; }
        if (open is { } g) History.End(g, SessionEndReasons.SharingStopped, _clock());
        if (host is not null)
        {
            host.HandshakeStarted -= OnHandshakeStarted;
            host.HandshakeFailed -= OnHandshakeFailed;
            host.ConsentRequested -= OnConsentRequested;
            host.ConsentResolved -= OnConsentResolved;
            host.SessionStarted -= OnSessionStarted;
            host.SessionEnded -= OnSessionEnded;
            host.Notice -= OnNotice;
        }
        State.Stop();
        if (openInfo is not null) SessionEnded?.Invoke(openInfo, SessionEndReasons.SharingStopped);
    }

    /// <summary>"Stop sharing" on the banner: ends the session; the caller then turns sharing off.</summary>
    public void EndSession() { LanHost? h; lock (_gate) h = _host; h?.EndSession(); }

    /// <summary>"Pause control": view only until resumed.</summary>
    public async Task SetControlPausedAsync(bool paused, string noticeForViewer)
    {
        LanHost? h; lock (_gate) h = _host;
        if (h is null || State.Current.State != ShareState.InSession) return;
        await h.SetControlPausedAsync(paused, noticeForViewer).ConfigureAwait(false);
        State.SetControlPaused(paused);
    }

    private void OnHandshakeStarted(string address) => State.BeginHandshake(address);
    private void OnHandshakeFailed() => State.HandshakeFailed();
    private void OnConsentRequested(ConsentRequest r) => State.RequestConsent(r.ControllerName, r.Address);
    private void OnNotice(string text) => Notice?.Invoke(text);

    private void OnConsentResolved(ConsentRequest request, ConsentResult result)
    {
        if (result.Allowed) return;
        History.AddRefused(request.ControllerName, request.DeviceId, request.Address, "token", result.Outcome.ToString(), _clock());
        State.ConsentDenied();
        State.HandshakeFailed();
        Refused?.Invoke(request, result);
    }

    private void OnSessionStarted(LanSessionInfo info)
    {
        lock (_gate) { _openInfo = info; _openRecord = History.Begin(info.ControllerName, info.DeviceId, info.Address, info.Via, info.Outcome.ToString(), info.StartedUtc); }
        State.BeginSession(info.ControllerName, info.Address);
        SessionStarted?.Invoke(info);
    }

    private void OnSessionEnded(LanSessionInfo info, string reason)
    {
        Guid? id;
        lock (_gate) { id = _openRecord; _openRecord = null; _openInfo = null; }
        if (id is { } g) History.End(g, reason, _clock());
        State.EndSession();
        SessionEnded?.Invoke(info, reason);
    }
}
