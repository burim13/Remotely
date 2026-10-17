using System.Buffers.Binary;

namespace Remotely.HostCore;

public enum InputKind : byte
{
    MouseMove = 1,
    MouseDown = 2,
    MouseUp = 3,
    Wheel = 4,
    KeyDown = 5,
    KeyUp = 6,
    /// <summary>Release every key and button the viewer has pressed (sent when focus is lost so nothing sticks).</summary>
    ReleaseAll = 7,
    // Kinds 8..11 are an extension (see FidelityMessages). A host that does not advertise HostCapabilities.RichClipboard rejects a whole message that contains them.
    /// <summary>One UTF-16 code unit typed as text (SendInput with KEYEVENTF_UNICODE), independent of the host's keyboard layout. Arg1 = code unit; <see cref="InputEvent.FlagKeyUp"/> in Flags marks the release.</summary>
    Text = 8,
    /// <summary>A touch contact. Flags low 3 bits = <see cref="TouchPhase"/>, X/Y = position, Arg1 = contact id, Arg2 = pressure 0..1024.</summary>
    Touch = 9,
    /// <summary>A pen. Flags low 3 bits = <see cref="PenPhase"/> plus the pen flag bits, X/Y = position, Arg1 = pressure 0..1024, Arg2 = (tiltX + 90) * 256 + (tiltY + 90).</summary>
    Pen = 10,
    /// <summary>Sent about once a second while the viewer holds a key or button. The first one tells the host the viewer sends them; from then on the host releases everything held if they stop (the viewer vanished).</summary>
    Heartbeat = 11,
}

public enum TouchPhase : byte { Down = 0, Move = 1, Up = 2, Cancel = 3 }

/// <summary>Pen phases. Hover = in range but not touching; Leave = left the digitiser's range.</summary>
public enum PenPhase : byte { Down = 0, Move = 1, Up = 2, Cancel = 3, Hover = 4, Leave = 5 }

public enum MouseButton : byte { Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4 }

/// <summary>
/// One input event. Mouse positions are normalised to 0..65535 across the whole captured (virtual) desktop, so the viewer needs no
/// knowledge of the host's resolution. Keys carry both the Windows virtual-key code and the scan code.
/// </summary>
public readonly record struct InputEvent(InputKind Kind, ushort X = 0, ushort Y = 0, ushort Arg1 = 0, ushort Arg2 = 0, byte Flags = 0)
{
    public const int WireSize = 10;
    public const byte FlagExtended = 1;
    public const byte FlagHorizontal = 1;
    /// <summary>Text events: this is the release of the code unit.</summary>
    public const byte FlagKeyUp = 2;
    public const byte PhaseMask = 7;
    public const byte PenBarrel = 8, PenEraser = 16, PenInverted = 32;
    public const int MaxPressure = 1024;
    public const int MaxTilt = 90;

    public static InputEvent Move(ushort x, ushort y) => new(InputKind.MouseMove, x, y);
    public static InputEvent Down(MouseButton b, ushort x, ushort y) => new(InputKind.MouseDown, x, y, (ushort)b);
    public static InputEvent Up(MouseButton b, ushort x, ushort y) => new(InputKind.MouseUp, x, y, (ushort)b);
    public static InputEvent Scroll(short delta, ushort x, ushort y, bool horizontal = false) =>
        new(InputKind.Wheel, x, y, unchecked((ushort)delta), 0, horizontal ? FlagHorizontal : (byte)0);
    public static InputEvent Key(bool down, ushort virtualKey, ushort scanCode, bool extended) =>
        new(down ? InputKind.KeyDown : InputKind.KeyUp, 0, 0, virtualKey, scanCode, extended ? FlagExtended : (byte)0);

    public static InputEvent Text(ushort codeUnit, bool down) => new(InputKind.Text, 0, 0, codeUnit, 0, down ? (byte)0 : FlagKeyUp);
    public static InputEvent Touch(TouchPhase phase, ushort contactId, ushort x, ushort y, int pressure = MaxPressure / 2) =>
        new(InputKind.Touch, x, y, contactId, (ushort)Math.Clamp(pressure, 0, MaxPressure), (byte)phase);
    public static InputEvent Pen(PenPhase phase, ushort x, ushort y, int pressure, int tiltX, int tiltY, bool barrel = false, bool eraser = false, bool inverted = false) =>
        new(InputKind.Pen, x, y, (ushort)Math.Clamp(pressure, 0, MaxPressure),
            (ushort)((Math.Clamp(tiltX, -MaxTilt, MaxTilt) + MaxTilt) * 256 + Math.Clamp(tiltY, -MaxTilt, MaxTilt) + MaxTilt),
            (byte)((byte)phase | (barrel ? PenBarrel : 0) | (eraser ? PenEraser : 0) | (inverted ? PenInverted : 0)));
    public static InputEvent Heartbeat() => new(InputKind.Heartbeat);

    /// <summary>A wheel movement of any size as events that each fit the wire: high-resolution and fast wheels report more than 32767 units at once.</summary>
    public static IEnumerable<InputEvent> ScrollMany(int delta, ushort x, ushort y, bool horizontal = false)
    {
        while (delta != 0)
        {
            var part = Math.Clamp(delta, short.MinValue, short.MaxValue);
            yield return Scroll((short)part, x, y, horizontal);
            delta -= part;
        }
    }

    public short WheelDelta => unchecked((short)Arg1);
    public bool IsKeyUp => (Flags & FlagKeyUp) != 0;
    public byte Phase => (byte)(Flags & PhaseMask);
    public int Pressure => Kind == InputKind.Pen ? Arg1 : Arg2;
    public int TiltX => (Arg2 >> 8) - MaxTilt;
    public int TiltY => (Arg2 & 0xFF) - MaxTilt;
    /// <summary>True for the kinds that old hosts do not understand.</summary>
    public bool IsExtension => Kind >= InputKind.Text;
}

public static class InputCodec
{
    public const int MaxEventsPerMessage = 256;

    public static byte[] Encode(IReadOnlyList<InputEvent> events)
    {
        if (events.Count > MaxEventsPerMessage) throw new ArgumentException("Too many events in one message.", nameof(events));
        var message = new byte[3 + events.Count * InputEvent.WireSize];
        message[0] = HostMessages.Input;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(1), (ushort)events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var o = 3 + i * InputEvent.WireSize;
            message[o] = (byte)e.Kind;
            message[o + 1] = e.Flags;
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(o + 2), e.X);
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(o + 4), e.Y);
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(o + 6), e.Arg1);
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(o + 8), e.Arg2);
        }
        return message;
    }

    /// <summary>Strict decode: unknown kinds, bad buttons or a wrong length reject the whole message.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> message, out List<InputEvent> events)
    {
        events = [];
        if (message.Length < 3 || message[0] != HostMessages.Input) return false;
        var count = BinaryPrimitives.ReadUInt16BigEndian(message[1..]);
        if (count > MaxEventsPerMessage || message.Length != 3 + count * InputEvent.WireSize) return false;
        for (var i = 0; i < count; i++)
        {
            var o = 3 + i * InputEvent.WireSize;
            var kind = (InputKind)message[o];
            if (kind is < InputKind.MouseMove or > InputKind.Heartbeat) return false;
            var e = new InputEvent(kind, BinaryPrimitives.ReadUInt16BigEndian(message[(o + 2)..]), BinaryPrimitives.ReadUInt16BigEndian(message[(o + 4)..]),
                BinaryPrimitives.ReadUInt16BigEndian(message[(o + 6)..]), BinaryPrimitives.ReadUInt16BigEndian(message[(o + 8)..]), message[o + 1]);
            if (kind is InputKind.MouseDown or InputKind.MouseUp && e.Arg1 > (ushort)MouseButton.X2) return false;
            if (kind == InputKind.Touch && (e.Phase > (byte)TouchPhase.Cancel || e.Arg2 > InputEvent.MaxPressure)) return false;
            if (kind == InputKind.Pen && (e.Phase > (byte)PenPhase.Leave || e.Arg1 > InputEvent.MaxPressure || (e.Arg2 >> 8) > 2 * InputEvent.MaxTilt || (e.Arg2 & 0xFF) > 2 * InputEvent.MaxTilt)) return false;
            events.Add(e);
        }
        return true;
    }

    /// <summary>True if any event needs a host that advertises <see cref="HostCapabilities.RichClipboard"/>.</summary>
    public static bool NeedsExtension(IEnumerable<InputEvent> events) => events.Any(e => e.IsExtension);
}
