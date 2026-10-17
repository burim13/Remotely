using System.Runtime.InteropServices;
using Remotely.Host.Interop;
using Remotely.HostCore;

namespace Remotely.Host.Helper;

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect { public int Left, Top, Right, Bottom; }

/// <summary>POINTER_INFO from winuser.h (64-bit layout: 96 bytes). The host ships for x64 and ARM64 only.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_INFO
{
    public uint pointerType, pointerId, frameId, pointerFlags;
    public IntPtr sourceDevice, hwndTarget;
    public NativePoint ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
    public uint dwTime, historyCount;
    public int inputData;
    public uint dwKeyStates;
    public ulong performanceCount;
    public uint buttonChangeType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_TOUCH_INFO
{
    public POINTER_INFO pointerInfo;
    public uint touchFlags, touchMask;
    public NativeRect rcContact, rcContactRaw;
    public uint orientation, pressure;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_PEN_INFO
{
    public POINTER_INFO pointerInfo;
    public uint penFlags, penMask, pressure, rotation;
    public int tiltX, tiltY;
}

[StructLayout(LayoutKind.Explicit)]
internal struct POINTER_TYPE_UNION
{
    [FieldOffset(0)] public POINTER_TOUCH_INFO touchInfo;
    [FieldOffset(0)] public POINTER_PEN_INFO penInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_TYPE_INFO
{
    public uint type;
    public POINTER_TYPE_UNION u;
}

internal static class InputNative
{
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint PT_TOUCH = 2, PT_PEN = 3;
    public const uint FEEDBACK_DEFAULT = 1;
    public const uint FLAG_NEW = 0x1, FLAG_INRANGE = 0x2, FLAG_INCONTACT = 0x4, FLAG_CANCELED = 0x8000, FLAG_DOWN = 0x10000, FLAG_UPDATE = 0x20000, FLAG_UP = 0x40000;
    public const uint TOUCH_MASK_CONTACTAREA = 1, TOUCH_MASK_ORIENTATION = 2, TOUCH_MASK_PRESSURE = 4;
    public const uint PEN_FLAG_BARREL = 1, PEN_FLAG_INVERTED = 2, PEN_FLAG_ERASER = 4;
    public const uint PEN_MASK_PRESSURE = 1, PEN_MASK_TILT_X = 4, PEN_MASK_TILT_Y = 8;

    [DllImport("user32.dll", SetLastError = true)] public static extern bool InitializeTouchInjection(uint maxCount, uint mode);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] info, uint count);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint threadId);
}

/// <summary>
/// Turns viewer input into SendInput calls on the desktop that currently has input. Remembers what is held down so a ReleaseAll can let go
/// (the host agent releases a vanished viewer's input with ordinary key-up events). Also injects typed text (Unicode), touch and pen.
/// </summary>
internal sealed class InputInjector : IInputSink, IHostKeyboardInfo
{
    private const int MaxTouchContacts = 10;

    private readonly DesktopThread _desktop;
    private readonly HeldInputTracker _held = new();

    // Touch and pen state, used only on the desktop thread.
    private bool _touchReady, _touchBroken;
    private readonly Dictionary<ushort, uint> _slots = [];
    private ushort? _fallbackContact;
    private IntPtr _penDevice;
    private bool _penBroken, _penInRange;

    public InputInjector(DesktopThread desktop) => _desktop = desktop;

    public Task InjectAsync(IReadOnlyList<InputEvent> events, CancellationToken ct) =>
        _desktop.RunAsync(name =>
        {
            var inputs = new List<INPUT>(events.Count + 4);
            foreach (var e in events) Process(e, inputs);
            Flush(inputs);
            return true;
        });

    public Task<uint> GetLayoutAsync(CancellationToken ct) =>
        _desktop.RunAsync(name =>
        {
            var window = InputNative.GetForegroundWindow();
            var thread = window == IntPtr.Zero ? 0 : InputNative.GetWindowThreadProcessId(window, out _);
            return unchecked((uint)(long)InputNative.GetKeyboardLayout(thread));
        });

    private static void Flush(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var sent = Native.SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<INPUT>());
        var wanted = inputs.Count;
        inputs.Clear();
        if (sent != wanted)
            throw new InvalidOperationException($"SendInput injected {sent} of {wanted} events (error {Marshal.GetLastWin32Error()}).");
    }

    private void Process(InputEvent e, List<INPUT> output)
    {
        if (e.Kind == InputKind.ReleaseAll)
        {
            foreach (var release in _held.TakeReleaseEvents()) Translate(release, output);
            return;
        }
        _held.Apply(e);
        Translate(e, output);
    }

    private void Translate(InputEvent e, List<INPUT> output)
    {
        switch (e.Kind)
        {
            case InputKind.MouseMove:
                output.Add(Mouse(Native.MOUSEEVENTF_MOVE, e.X, e.Y, 0));
                break;
            case InputKind.MouseDown:
            {
                output.Add(Mouse(Native.MOUSEEVENTF_MOVE, e.X, e.Y, 0));
                var (flag, data) = ButtonFlags((MouseButton)e.Arg1, down: true);
                output.Add(Mouse(flag, 0, 0, data, positional: false));
                break;
            }
            case InputKind.MouseUp:
            {
                output.Add(Mouse(Native.MOUSEEVENTF_MOVE, e.X, e.Y, 0));
                var (flag, data) = ButtonFlags((MouseButton)e.Arg1, down: false);
                output.Add(Mouse(flag, 0, 0, data, positional: false));
                break;
            }
            case InputKind.Wheel:
                output.Add(Mouse(Native.MOUSEEVENTF_MOVE, e.X, e.Y, 0));
                output.Add(Mouse((e.Flags & InputEvent.FlagHorizontal) != 0 ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(int)e.WheelDelta), positional: false));
                break;
            case InputKind.KeyDown or InputKind.KeyUp:
                output.Add(Key(e.Arg1, e.Arg2, (e.Flags & InputEvent.FlagExtended) != 0, up: e.Kind == InputKind.KeyUp));
                break;
            case InputKind.Text:
                output.Add(Unicode(e.Arg1, up: e.IsKeyUp));
                break;
            case InputKind.Touch:
                Flush(output);
                InjectTouch(e, output);
                break;
            case InputKind.Pen:
                Flush(output);
                InjectPen(e, output);
                break;
            // Heartbeats are consumed by the host agent and never arrive here; ReleaseAll was expanded before this point.
        }
    }

    // ---- touch and pen ----

    private static (int X, int Y) ToPixels(ushort x, ushort y)
    {
        var left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        return (left + (int)Math.Round(x * (double)Math.Max(width - 1, 0) / 65535), top + (int)Math.Round(y * (double)Math.Max(height - 1, 0) / 65535));
    }

    private void InjectTouch(InputEvent e, List<INPUT> output)
    {
        var phase = (TouchPhase)e.Phase;
        if (!_touchBroken && !_touchReady)
        {
            _touchReady = InputNative.InitializeTouchInjection(MaxTouchContacts, InputNative.FEEDBACK_DEFAULT);
            _touchBroken = !_touchReady;
        }
        if (_touchBroken) { PointerAsMouse(e.Arg1, phase is TouchPhase.Down, phase is TouchPhase.Up or TouchPhase.Cancel, e.X, e.Y, output); return; }

        uint slot;
        if (phase == TouchPhase.Down)
        {
            slot = Enumerable.Range(0, MaxTouchContacts).Select(i => (uint)i).FirstOrDefault(i => !_slots.ContainsValue(i), uint.MaxValue);
            if (slot == uint.MaxValue) return; // more simultaneous fingers than Windows takes
            _slots[e.Arg1] = slot;
        }
        else if (!_slots.TryGetValue(e.Arg1, out slot)) return;

        var (px, py) = ToPixels(e.X, e.Y);
        var flags = phase switch
        {
            TouchPhase.Down => InputNative.FLAG_DOWN | InputNative.FLAG_INRANGE | InputNative.FLAG_INCONTACT,
            TouchPhase.Move => InputNative.FLAG_UPDATE | InputNative.FLAG_INRANGE | InputNative.FLAG_INCONTACT,
            TouchPhase.Up => InputNative.FLAG_UP,
            _ => InputNative.FLAG_UP | InputNative.FLAG_CANCELED,
        };
        var contact = new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO { pointerType = InputNative.PT_TOUCH, pointerId = slot, pointerFlags = flags, ptPixelLocation = new NativePoint { X = px, Y = py } },
            touchMask = InputNative.TOUCH_MASK_CONTACTAREA | InputNative.TOUCH_MASK_ORIENTATION | InputNative.TOUCH_MASK_PRESSURE,
            rcContact = new NativeRect { Left = px - 2, Top = py - 2, Right = px + 2, Bottom = py + 2 },
            orientation = 90,
            pressure = (uint)Math.Max(e.Pressure, 1),
        };
        if (phase is TouchPhase.Up or TouchPhase.Cancel) _slots.Remove(e.Arg1);
        if (!InputNative.InjectTouchInput(1, [contact]))
        {
            _touchBroken = true; // from now on touch is delivered as a left-button mouse
            _slots.Clear();
            PointerAsMouse(e.Arg1, phase is TouchPhase.Down, phase is TouchPhase.Up or TouchPhase.Cancel, e.X, e.Y, output);
        }
    }

    private void InjectPen(InputEvent e, List<INPUT> output)
    {
        var phase = (PenPhase)e.Phase;
        if (!_penBroken && _penDevice == IntPtr.Zero)
        {
            _penDevice = InputNative.CreateSyntheticPointerDevice(InputNative.PT_PEN, 1, InputNative.FEEDBACK_DEFAULT);
            _penBroken = _penDevice == IntPtr.Zero;
        }
        if (_penBroken)
        {
            PointerAsMouse(0, phase is PenPhase.Down, phase is PenPhase.Up or PenPhase.Cancel, e.X, e.Y, output);
            return;
        }
        var (px, py) = ToPixels(e.X, e.Y);
        var inRange = phase is not (PenPhase.Up or PenPhase.Cancel or PenPhase.Leave);
        var flags = phase switch
        {
            PenPhase.Down => InputNative.FLAG_DOWN | InputNative.FLAG_INRANGE | InputNative.FLAG_INCONTACT,
            PenPhase.Move => InputNative.FLAG_UPDATE | InputNative.FLAG_INRANGE | InputNative.FLAG_INCONTACT,
            PenPhase.Hover => InputNative.FLAG_UPDATE | InputNative.FLAG_INRANGE,
            PenPhase.Leave => InputNative.FLAG_UPDATE,
            PenPhase.Up => InputNative.FLAG_UP | InputNative.FLAG_INRANGE,
            _ => InputNative.FLAG_UP | InputNative.FLAG_CANCELED,
        };
        if (inRange && !_penInRange) flags |= InputNative.FLAG_NEW;
        _penInRange = inRange;
        var penFlags = ((e.Flags & InputEvent.PenBarrel) != 0 ? InputNative.PEN_FLAG_BARREL : 0)
                       | ((e.Flags & InputEvent.PenEraser) != 0 ? InputNative.PEN_FLAG_ERASER : 0)
                       | ((e.Flags & InputEvent.PenInverted) != 0 ? InputNative.PEN_FLAG_INVERTED : 0);
        var info = new POINTER_TYPE_INFO { type = InputNative.PT_PEN };
        info.u.penInfo = new POINTER_PEN_INFO
        {
            pointerInfo = new POINTER_INFO { pointerType = InputNative.PT_PEN, pointerId = 0, pointerFlags = flags, ptPixelLocation = new NativePoint { X = px, Y = py } },
            penFlags = penFlags,
            penMask = InputNative.PEN_MASK_PRESSURE | InputNative.PEN_MASK_TILT_X | InputNative.PEN_MASK_TILT_Y,
            pressure = (uint)e.Pressure,
            tiltX = e.TiltX,
            tiltY = e.TiltY,
        };
        if (!InputNative.InjectSyntheticPointerInput(_penDevice, [info], 1))
        {
            _penBroken = true;
            PointerAsMouse(0, phase is PenPhase.Down, phase is PenPhase.Up or PenPhase.Cancel, e.X, e.Y, output);
        }
    }

    /// <summary>Fallback when Windows refuses touch or pen injection: the first contact acts as the left mouse button, others are ignored.</summary>
    private void PointerAsMouse(ushort contact, bool down, bool up, ushort x, ushort y, List<INPUT> output)
    {
        if (down && _fallbackContact is null) _fallbackContact = contact;
        if (_fallbackContact != contact) return;
        output.Add(Mouse(Native.MOUSEEVENTF_MOVE, x, y, 0));
        if (down) output.Add(Mouse(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, positional: false));
        if (up)
        {
            output.Add(Mouse(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, positional: false));
            _fallbackContact = null;
        }
    }

    // ---- SendInput building blocks ----

    private static (uint Flag, uint Data) ButtonFlags(MouseButton b, bool down) => b switch
    {
        MouseButton.Left => (down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP, 0u),
        MouseButton.Right => (down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP, 0u),
        MouseButton.Middle => (down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP, 0u),
        MouseButton.X1 => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, 1u),
        _ => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, 2u),
    };

    private static INPUT Mouse(uint flags, int x, int y, uint data, bool positional = true) => new()
    {
        type = Native.INPUT_MOUSE,
        u = new INPUTUNION
        {
            mi = new MOUSEINPUT
            {
                dx = x, dy = y, mouseData = data,
                // Positions are normalised 0..65535 over the whole virtual desktop.
                dwFlags = flags | (positional ? Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK : 0),
            },
        },
    };

    private static INPUT Key(ushort vk, ushort scan, bool extended, bool up)
    {
        // Viewers on other platforms (macOS) send no scan code; derive it from the virtual key so apps that read scan codes still work.
        if (scan == 0) scan = (ushort)Native.MapVirtualKeyW(vk, 0);
        return new INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0) | (up ? Native.KEYEVENTF_KEYUP : 0) },
            },
        };
    }

    /// <summary>One UTF-16 code unit typed as text: wVk must be 0 and the unit goes in wScan. Surrogate pairs are sent as two such events.</summary>
    private static INPUT Unicode(ushort unit, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = unit, dwFlags = InputNative.KEYEVENTF_UNICODE | (up ? Native.KEYEVENTF_KEYUP : 0) } },
    };
}
