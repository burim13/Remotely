using Remotely.HostCore;

namespace Remotely.Host.Unattended;

/// <summary>Asks Windows not to sleep. The Windows implementation is <c>PowerRequest</c>; tests use a fake.</summary>
public interface IPowerRequest
{
    /// <summary>Returns true when the request is now in force.</summary>
    bool Acquire();
    void Release();
}

/// <summary>
/// Knows whether somebody is looking at this PC: every frame the service captures for a viewer (a token session or a session through the server) counts. While somebody is,
/// and the switch is on, a "do not sleep" request is held; it is let go <see cref="Grace"/> after the last frame, or when the switch is turned off.
/// It also remembers which Windows desktop the newest frame came from, which is what the app shows as "looking at the lock screen".
/// </summary>
public sealed class SessionActivity(IPowerRequest power, Func<bool> keepAwake, Func<DateTimeOffset>? clock = null, TimeSpan? grace = null)
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(30);
    /// <summary>A frame older than this no longer counts as "being looked at right now".</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromSeconds(15);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private DateTimeOffset _lastTouch = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDesktopAt = DateTimeOffset.MinValue;
    private string? _lastDesktop;
    private bool _held;

    public TimeSpan Grace { get; } = grace ?? DefaultGrace;

    public bool AwakeHeld
    {
        get { lock (_gate) return _held; }
    }

    /// <summary>Desktop of the newest frame if it is recent, else null.</summary>
    public string? CaptureDesktop
    {
        get
        {
            lock (_gate) return _clock() - _lastDesktopAt <= Recent ? _lastDesktop : null;
        }
    }

    /// <summary>A viewer's frame was captured (<paramref name="desktop"/> is its desktop name, null when the request carried no picture).</summary>
    public void Touch(string? desktop)
    {
        lock (_gate)
        {
            var now = _clock();
            _lastTouch = now;
            if (desktop is not null) { _lastDesktop = desktop; _lastDesktopAt = now; }
            if (!_held && keepAwake()) _held = power.Acquire();
        }
    }

    /// <summary>Call every few seconds: lets go of the request when nobody has looked for <see cref="Grace"/>, or when the switch was turned off.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (!_held) return;
            if (_clock() - _lastTouch > Grace || !keepAwake())
            {
                power.Release();
                _held = false;
            }
        }
    }

    /// <summary>Lets go at once (the service is stopping).</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_held) power.Release();
            _held = false;
        }
    }
}

/// <summary>Passes every capture through to the real source and tells <see cref="SessionActivity"/> that someone is looking.</summary>
public sealed class ActivityFrameSource(ITiledFrameSource inner, SessionActivity activity) : ITiledFrameSource
{
    public async Task<FrameInfo> CaptureAsync(CancellationToken ct)
    {
        var frame = await inner.CaptureAsync(ct).ConfigureAwait(false);
        activity.Touch(frame.Desktop);
        return frame;
    }

    public async Task<TileCapture> CaptureTilesAsync(TileRequest request, CancellationToken ct)
    {
        var capture = await inner.CaptureTilesAsync(request, ct).ConfigureAwait(false);
        activity.Touch(capture.Frame?.Desktop);
        return capture;
    }
}
