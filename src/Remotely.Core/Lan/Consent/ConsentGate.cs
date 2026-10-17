namespace Remotely.Core.Lan.Consent;

public enum ConsentDecision { Deny, AllowOnce, AlwaysAllow }

public enum ConsentOutcome
{
    /// <summary>The PC is on the remembered list and proved its key: no question asked.</summary>
    AllowedRemembered,
    /// <summary>"Ask me before someone connects" is switched off.</summary>
    AllowedWithoutAsking,
    AllowedOnce,
    AllowedAlways,
    Denied,
    /// <summary>Nobody answered in time. Counts as a refusal.</summary>
    TimedOut,
}

/// <param name="DeviceId">Proven device id of the other PC, or null when it did not prove one (an older version of the app).</param>
/// <param name="CanRemember">True only when a device key was proven, so "Always allow" can be offered.</param>
public sealed record ConsentRequest(string ControllerName, string Address, string? DeviceId, bool CanRemember, DateTimeOffset RequestedUtc);

public sealed record ConsentResult(ConsentOutcome Outcome)
{
    public bool Allowed => Outcome is ConsentOutcome.AllowedRemembered or ConsentOutcome.AllowedWithoutAsking or ConsentOutcome.AllowedOnce or ConsentOutcome.AllowedAlways;
}

/// <summary>Settings the person can change while sharing is on.</summary>
public sealed class ConsentOptions
{
    private volatile bool _ask = true;
    private volatile bool _allowRemembered = true;

    /// <summary>"Ask me before someone connects". On by default; a PC that is not remembered is then never let in without a click.</summary>
    public bool AskBeforeConnect { get => _ask; set => _ask = value; }
    /// <summary>Whether PCs on the remembered list may reconnect without a token or a question.</summary>
    public bool AllowRemembered { get => _allowRemembered; set => _allowRemembered = value; }
    public TimeSpan PromptTimeout { get; set; } = TimeSpan.FromSeconds(25);
}

/// <summary>
/// The consent rules, in one place. 1) A remembered PC that proved its key is let in (if remembered PCs are allowed). 2) With asking switched off, anyone who knew the token is
/// let in. 3) Otherwise the person is asked; no answer in time, an error, or no way to ask all mean "no". "Always allow" is honoured only when a device key was proven,
/// otherwise it counts as "Allow once" (there is nothing safe to remember a PC by).
/// </summary>
public sealed class ConsentGate(ConsentOptions options, RememberedDevices remembered, Func<ConsentRequest, CancellationToken, Task<ConsentDecision>>? prompt, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public ConsentOptions Options => options;
    public RememberedDevices Remembered => remembered;

    /// <summary>The person is being asked now.</summary>
    public event Action<ConsentRequest>? Prompting;

    /// <param name="devicePublicKey">The proven public key (null when none was proven); needed to remember the PC.</param>
    public async Task<ConsentResult> DecideAsync(string controllerName, string address, byte[]? devicePublicKey, string? deviceId, CancellationToken ct)
    {
        var request = new ConsentRequest(controllerName, address, deviceId, devicePublicKey is not null && deviceId is not null, _clock());
        if (deviceId is not null && options.AllowRemembered && remembered.Contains(deviceId))
        {
            remembered.Touch(deviceId, address, _clock());
            return new(ConsentOutcome.AllowedRemembered);
        }
        if (!options.AskBeforeConnect) return new(ConsentOutcome.AllowedWithoutAsking);
        if (prompt is null) return new(ConsentOutcome.Denied);

        ConsentDecision decision;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.PromptTimeout);
        try
        {
            Prompting?.Invoke(request);
            decision = await prompt(request, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(ConsentOutcome.TimedOut);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(ConsentOutcome.Denied);
        }

        switch (decision)
        {
            case ConsentDecision.AlwaysAllow when request.CanRemember && remembered.Add(devicePublicKey!, controllerName, _clock()):
                return new(ConsentOutcome.AllowedAlways);
            case ConsentDecision.AlwaysAllow or ConsentDecision.AllowOnce:
                return new(ConsentOutcome.AllowedOnce);
            default:
                return new(ConsentOutcome.Denied);
        }
    }
}
