namespace Remotely.HostCore;

/// <summary>
/// Knows which keys, text code units, mouse buttons, touch contacts and pen contact are currently held down by remote input, so everything can be
/// released when the viewer loses focus, leaves, or vanishes. Not thread-safe: the injector uses it only on its desktop thread.
/// </summary>
public sealed class HeldInputTracker
{
    private readonly HashSet<(ushort Vk, ushort Scan, bool Extended)> _keys = [];
    private readonly HashSet<ushort> _units = [];
    private readonly HashSet<MouseButton> _buttons = [];
    private readonly Dictionary<ushort, (ushort X, ushort Y)> _touches = [];
    private bool _penDown;
    private (ushort X, ushort Y) _penAt;
    private byte _penFlags;
    private ushort _lastX, _lastY;

    public bool AnyHeld => _keys.Count + _units.Count + _buttons.Count + _touches.Count > 0 || _penDown;

    public int HeldKeys => _keys.Count;
    public int HeldButtons => _buttons.Count;
    public int HeldTouches => _touches.Count;
    public bool PenDown => _penDown;

    public void Apply(in InputEvent e)
    {
        switch (e.Kind)
        {
            case InputKind.MouseMove or InputKind.Wheel:
                (_lastX, _lastY) = (e.X, e.Y);
                break;
            case InputKind.MouseDown:
                (_lastX, _lastY) = (e.X, e.Y);
                _buttons.Add((MouseButton)e.Arg1);
                break;
            case InputKind.MouseUp:
                (_lastX, _lastY) = (e.X, e.Y);
                _buttons.Remove((MouseButton)e.Arg1);
                break;
            case InputKind.KeyDown:
                _keys.Add((e.Arg1, e.Arg2, (e.Flags & InputEvent.FlagExtended) != 0));
                break;
            case InputKind.KeyUp:
                _keys.Remove((e.Arg1, e.Arg2, (e.Flags & InputEvent.FlagExtended) != 0));
                break;
            case InputKind.Text:
                if (e.IsKeyUp) _units.Remove(e.Arg1); else _units.Add(e.Arg1);
                break;
            case InputKind.Touch:
                if (e.Phase is (byte)TouchPhase.Up or (byte)TouchPhase.Cancel) _touches.Remove(e.Arg1);
                else _touches[e.Arg1] = (e.X, e.Y);
                break;
            case InputKind.Pen:
                _penFlags = (byte)(e.Flags & ~InputEvent.PhaseMask);
                _penAt = (e.X, e.Y);
                _penDown = e.Phase is (byte)PenPhase.Down or (byte)PenPhase.Move;
                break;
            case InputKind.ReleaseAll:
                Clear();
                break;
        }
    }

    /// <summary>Events that undo everything still held, in a safe order (contacts, buttons, keys). Forgets it all.</summary>
    public List<InputEvent> TakeReleaseEvents()
    {
        var events = new List<InputEvent>();
        foreach (var (id, at) in _touches) events.Add(InputEvent.Touch(TouchPhase.Cancel, id, at.X, at.Y, 0));
        if (_penDown) events.Add(new InputEvent(InputKind.Pen, _penAt.X, _penAt.Y, 0, 0, (byte)((byte)PenPhase.Cancel | _penFlags)));
        foreach (var b in _buttons) events.Add(InputEvent.Up(b, _lastX, _lastY));
        foreach (var (vk, scan, ext) in _keys) events.Add(InputEvent.Key(false, vk, scan, ext));
        foreach (var unit in _units) events.Add(InputEvent.Text(unit, false));
        Clear();
        return events;
    }

    private void Clear()
    {
        _keys.Clear();
        _units.Clear();
        _buttons.Clear();
        _touches.Clear();
        _penDown = false;
    }
}

/// <summary>
/// Releases everything a viewer holds if the viewer stops talking. It only acts for a viewer that has shown it sends <see cref="InputKind.Heartbeat"/>
/// (an older viewer may legitimately hold a button for a long time without sending anything).
/// </summary>
public sealed class InputWatchdog(Func<long> nowMilliseconds, long timeoutMilliseconds = InputWatchdog.DefaultTimeoutMs)
{
    public const long DefaultTimeoutMs = 5000;

    private long _lastActivity = nowMilliseconds();
    private bool _armed;

    /// <summary>True once a heartbeat has been seen.</summary>
    public bool Armed => Volatile.Read(ref _armed);

    public void Activity(InputKind kind)
    {
        Volatile.Write(ref _lastActivity, nowMilliseconds());
        if (kind == InputKind.Heartbeat) Volatile.Write(ref _armed, true);
    }

    public bool IsStale => Armed && nowMilliseconds() - Volatile.Read(ref _lastActivity) > timeoutMilliseconds;
}
