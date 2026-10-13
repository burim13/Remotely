using System.Runtime.InteropServices;
using Remotely.Host.Interop;
using Remotely.HostCore;

namespace Remotely.Host.Helper;

/// <summary>Turns viewer input into SendInput calls on the desktop that currently has input. Remembers what is held down so ReleaseAll can let go.</summary>
internal sealed class InputInjector(DesktopThread desktop) : IInputSink
{
    private readonly HashSet<(ushort Vk, ushort Scan, bool Extended)> _keysDown = [];
    private readonly HashSet<MouseButton> _buttonsDown = [];

    public Task InjectAsync(IReadOnlyList<InputEvent> events, CancellationToken ct) =>
        desktop.RunAsync(_ =>
        {
            var inputs = new List<INPUT>(events.Count + 4);
            foreach (var e in events) Translate(e, inputs);
            if (inputs.Count == 0) return true;
            var sent = Native.SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<INPUT>());
            if (sent != inputs.Count)
                throw new InvalidOperationException($"SendInput injected {sent} of {inputs.Count} events (error {Marshal.GetLastWin32Error()}).");
            return true;
        });

    private void Translate(InputEvent e, List<INPUT> output)
    {
        switch (e.Kind)
        {
            case InputKind.MouseMove:
                output.Add(Mouse(Native.MOUSEEVENTF_MOVE, e.X, e.Y, 0));
                break;
            case InputKind.MouseDown:
            {
                var button = (MouseButton)e.Arg1;
                output.Add(Mouse(Native.MOUSEEVENTF_MOVE, e.X, e.Y, 0));
                var (flag, data) = ButtonFlags(button, down: true);
                output.Add(Mouse(flag, 0, 0, data, positional: false));
                _buttonsDown.Add(button);
                break;
            }
            case InputKind.MouseUp:
            {
                var button = (MouseButton)e.Arg1;
                output.Add(Mouse(Native.MOUSEEVENTF_MOVE, e.X, e.Y, 0));
                var (flag, data) = ButtonFlags(button, down: false);
                output.Add(Mouse(flag, 0, 0, data, positional: false));
                _buttonsDown.Remove(button);
                break;
            }
            case InputKind.Wheel:
                output.Add(Mouse(Native.MOUSEEVENTF_MOVE, e.X, e.Y, 0));
                output.Add(Mouse((e.Flags & InputEvent.FlagHorizontal) != 0 ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(int)e.WheelDelta), positional: false));
                break;
            case InputKind.KeyDown or InputKind.KeyUp:
            {
                var extended = (e.Flags & InputEvent.FlagExtended) != 0;
                var down = e.Kind == InputKind.KeyDown;
                output.Add(Key(e.Arg1, e.Arg2, extended, up: !down));
                if (down) _keysDown.Add((e.Arg1, e.Arg2, extended)); else _keysDown.Remove((e.Arg1, e.Arg2, extended));
                break;
            }
            case InputKind.ReleaseAll:
                foreach (var (vk, scan, ext) in _keysDown) output.Add(Key(vk, scan, ext, up: true));
                foreach (var b in _buttonsDown) { var (flag, data) = ButtonFlags(b, down: false); output.Add(Mouse(flag, 0, 0, data, positional: false)); }
                _keysDown.Clear();
                _buttonsDown.Clear();
                break;
        }
    }

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

    private static INPUT Key(ushort vk, ushort scan, bool extended, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0) | (up ? Native.KEYEVENTF_KEYUP : 0) },
        },
    };
}
