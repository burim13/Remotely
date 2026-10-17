namespace Remotely.HostCore;

/// <summary>
/// The host's side of the input-fidelity extension for one viewer: clipboard transfers in both directions, keyboard layout answers, and
/// releasing exactly what this viewer still holds when its session ends or, for a viewer that sends heartbeats, when it goes silent.
/// </summary>
public sealed class FidelityHostSession : IDisposable
{
    public static readonly TimeSpan ClipboardPollInterval = TimeSpan.FromMilliseconds(700);

    private readonly HostServices _services;
    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly Action<string> _log;
    private readonly CancellationToken _sessionEnded;
    private readonly CancellationTokenRegistration _onEnd;
    private readonly IRichClipboardSync? _rich;
    private readonly object _gate = new();
    private readonly HeldInputTracker _held = new();
    private readonly InputWatchdog _watchdog;
    private readonly TimeSpan _watchdogInterval;
    private CancellationTokenSource? _watch;
    private long _appliedSequence = -1;
    private int _watchdogStarted;

    public FidelityHostSession(HostServices services, Func<byte[], CancellationToken, Task> send, CancellationToken sessionEnded, Action<string> log, Func<long>? nowMilliseconds = null, TimeSpan? watchdogInterval = null)
    {
        _watchdog = new InputWatchdog(nowMilliseconds ?? (() => Environment.TickCount64));
        _watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(1);
        _services = services;
        _send = send;
        _log = log;
        _sessionEnded = sessionEnded;
        _rich = services.Clipboard as IRichClipboardSync;
        Clip = new ClipPeer(ClipWire.HostToViewer, ClipWire.ViewerToHost, send, ApplyAsync)
        {
            AcceptIncoming = _rich is not null,
            UnavailableReason = "This PC cannot take rich clipboard content.",
        };
        Clip.Problem += message => log("Clipboard: " + message);
        _onEnd = sessionEnded.Register(() => _ = Task.Run(EndAsync));
    }

    public ClipPeer Clip { get; }

    /// <summary>The capability to advertise: set whenever this host can inject input (the extended input kinds ride on it) or has a rich clipboard.</summary>
    public static HostCapabilities Advertise(HostServices services) =>
        services.Input is not null || services.Clipboard is IRichClipboardSync ? HostCapabilities.RichClipboard : HostCapabilities.None;

    /// <summary>
    /// Looks at the input a viewer sent before it is injected: remembers what is held, notes the viewer is alive, and returns the events to inject
    /// (heartbeats are the host's business and never reach the input sink).
    /// </summary>
    public IReadOnlyList<InputEvent> Observe(IReadOnlyList<InputEvent> events)
    {
        var pass = new List<InputEvent>(events.Count);
        lock (_gate)
        {
            foreach (var e in events)
            {
                _watchdog.Activity(e.Kind);
                _held.Apply(e);
                if (e.Kind != InputKind.Heartbeat) pass.Add(e);
            }
        }
        if (_watchdog.Armed && Interlocked.Exchange(ref _watchdogStarted, 1) == 0 && !_sessionEnded.IsCancellationRequested)
            _ = Task.Run(WatchdogAsync);
        return pass;
    }

    private async Task WatchdogAsync()
    {
        try
        {
            while (!_sessionEnded.IsCancellationRequested)
            {
                await Task.Delay(_watchdogInterval, _sessionEnded).ConfigureAwait(false);
                bool silent;
                lock (_gate) silent = _watchdog.IsStale && _held.AnyHeld;
                if (silent)
                {
                    _log("Viewer went silent while holding keys or buttons; releasing them.");
                    await ReleaseInputAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public bool Handles(byte kind) => FidelityMessages.IsToHost(kind);

    public async Task HandleAsync(byte[] message, CancellationToken ct)
    {
        if (message.Length == 0) return;
        switch (message[0])
        {
            case FidelityMessages.ToHost.LayoutQuery:
                var layout = _services.Input is IHostKeyboardInfo info ? await info.GetLayoutAsync(ct).ConfigureAwait(false) : 0u;
                await _send(FidelityMessages.LayoutReply(layout), ct).ConfigureAwait(false);
                break;
            case FidelityMessages.ToHost.ClipSubscribe:
                SetWatching(message.Length >= 2 && message[1] == 1);
                break;
            default:
                if (Clip.Handles(message[0])) await Clip.HandleAsync(message, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task ApplyAsync(ClipBundle bundle, CancellationToken ct)
    {
        if (_rich is null) throw new InvalidOperationException("This PC cannot take rich clipboard content.");
        var sequence = await _rich.WriteBundleAsync(bundle, ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _appliedSequence, sequence);
    }

    private void SetWatching(bool on)
    {
        lock (_gate)
        {
            if (!on || _rich is null || _sessionEnded.IsCancellationRequested)
            {
                _watch?.Cancel();
                _watch = null;
                return;
            }
            if (_watch is not null) return;
            _watch = CancellationTokenSource.CreateLinkedTokenSource(_sessionEnded);
            var token = _watch.Token;
            _ = Task.Run(() => WatchAsync(_rich, token));
        }
    }

    /// <summary>Polls the clipboard's sequence number and offers every new rich copy to the viewer. Plain text keeps using the original text path.</summary>
    private async Task WatchAsync(IRichClipboardSync rich, CancellationToken ct)
    {
        var known = -1L;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var read = await rich.ReadBundleAsync(known, Clip.Limits, ct).ConfigureAwait(false);
                    if (read.Sequence != known)
                    {
                        known = read.Sequence;
                        if (read.Note is not null) _log("Clipboard not shared: " + read.Note);
                        if (read.Bundle is { HasRich: true } bundle && read.Sequence != Interlocked.Read(ref _appliedSequence))
                        {
                            var outcome = await Clip.SendAsync(bundle, ct).ConfigureAwait(false);
                            if (!outcome.Ok && outcome.Status != ClipSendStatus.SkippedEcho) _log($"Clipboard not delivered ({outcome.Status}): {outcome.Message}");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    _log("Clipboard read failed: " + ex.Message);
                }
                await Task.Delay(ClipboardPollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Lets go of every key, button and contact this viewer still holds, and nothing else. Safe to call at any time.</summary>
    public async Task ReleaseInputAsync()
    {
        List<InputEvent> release;
        lock (_gate) release = _held.TakeReleaseEvents();
        if (release.Count == 0 || _services.Input is not { } input) return;
        try { await input.InjectAsync(release, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log("Could not release held input: " + ex.Message); }
    }

    private async Task EndAsync()
    {
        lock (_gate) { _watch?.Cancel(); _watch = null; }
        await ReleaseInputAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _onEnd.Dispose();
        lock (_gate) { _watch?.Cancel(); _watch?.Dispose(); _watch = null; }
    }
}
