namespace Remotely.Core.Lan.Consent;

public enum ShareState { Off, Ready, Connecting, AwaitingConsent, InSession, Error }

/// <summary>What the Share screen, the banner and the toasts show. <see cref="ResourceKey"/> names the plain-language sentence in the app's string table.</summary>
public sealed record ShareSnapshot(ShareState State, string? Controller, string? Address, DateTimeOffset? SinceUtc, string? ErrorText, bool ControlPaused)
{
    public static ShareSnapshot Off { get; } = new(ShareState.Off, null, null, null, null, false);

    public string ResourceKey => State switch
    {
        ShareState.Off => "Share_State_Off",
        ShareState.Ready => "Share_State_Ready",
        ShareState.Connecting => "Share_State_Connecting",
        ShareState.AwaitingConsent => "Share_State_AwaitingConsent",
        ShareState.InSession => ControlPaused ? "Share_State_InSessionPaused" : "Share_State_InSession",
        _ => "Share_State_Error",
    };
}

/// <summary>
/// The sharing state, as a plain class so every rule can be tested without a screen. Each method returns false (and changes nothing) when the move is not allowed
/// from the current state, so a late event (for example "connection failed" after the person pressed Stop) cannot put the screen in a wrong state.
/// Off -> Ready (Start) -> Connecting (a PC is dialling in) -> AwaitingConsent (token was right, the person must answer) -> InSession -> Ready.
/// </summary>
public sealed class ShareStateMachine(Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private ShareSnapshot _current = ShareSnapshot.Off;

    public ShareSnapshot Current { get { lock (_gate) return _current; } }
    public event Action<ShareSnapshot>? Changed;

    private bool Move(Func<ShareSnapshot, ShareSnapshot?> next)
    {
        ShareSnapshot updated;
        lock (_gate)
        {
            var n = next(_current);
            if (n is null) return false;
            _current = updated = n;
        }
        Changed?.Invoke(updated);
        return true;
    }

    private ShareSnapshot Ready() => new(ShareState.Ready, null, null, _clock(), null, false);

    /// <summary>Sharing turned on (also the way out of Error).</summary>
    public bool Start() => Move(s => s.State is ShareState.Off or ShareState.Error ? Ready() : null);

    /// <summary>Sharing turned off. Allowed from every state.</summary>
    public bool Stop() => Move(s => s.State == ShareState.Off ? null : ShareSnapshot.Off);

    /// <summary>Another PC opened a connection and is proving it knows the token (or its remembered key).</summary>
    public bool BeginHandshake(string address) => Move(s => s.State == ShareState.Ready ? new(ShareState.Connecting, null, address, _clock(), null, false) : null);

    /// <summary>The handshake failed (wrong token, timeout, not remembered). Back to Ready.</summary>
    public bool HandshakeFailed() => Move(s => s.State == ShareState.Connecting ? Ready() : null);

    /// <summary>The token was right and the person at this PC must say yes or no.</summary>
    public bool RequestConsent(string controller, string address) =>
        Move(s => s.State is ShareState.Connecting or ShareState.Ready ? new(ShareState.AwaitingConsent, controller, address, _clock(), null, false) : null);

    public bool ConsentDenied() => Move(s => s.State == ShareState.AwaitingConsent ? Ready() : null);

    /// <summary>The PC is let in (after consent, automatically for a remembered PC, or when asking is switched off).</summary>
    public bool BeginSession(string controller, string address) =>
        Move(s => s.State is ShareState.Connecting or ShareState.AwaitingConsent or ShareState.Ready ? new(ShareState.InSession, controller, address, _clock(), null, false) : null);

    public bool EndSession() => Move(s => s.State == ShareState.InSession ? Ready() : null);

    /// <summary>"Pause control": the other PC keeps watching but its mouse, keyboard, clipboard and files are ignored.</summary>
    public bool SetControlPaused(bool paused) => Move(s => s.State == ShareState.InSession && s.ControlPaused != paused ? s with { ControlPaused = paused } : null);

    /// <summary>Something stopped sharing from working (for example the network port could not be opened). Allowed from every state.</summary>
    public bool Fail(string errorText) => Move(_ => new(ShareState.Error, null, null, _clock(), errorText, false));
}
