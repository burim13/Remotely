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
}

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

    public static InputEvent Move(ushort x, ushort y) => new(InputKind.MouseMove, x, y);
    public static InputEvent Down(MouseButton b, ushort x, ushort y) => new(InputKind.MouseDown, x, y, (ushort)b);
    public static InputEvent Up(MouseButton b, ushort x, ushort y) => new(InputKind.MouseUp, x, y, (ushort)b);
    public static InputEvent Scroll(short delta, ushort x, ushort y, bool horizontal = false) =>
        new(InputKind.Wheel, x, y, unchecked((ushort)delta), 0, horizontal ? FlagHorizontal : (byte)0);
    public static InputEvent Key(bool down, ushort virtualKey, ushort scanCode, bool extended) =>
        new(down ? InputKind.KeyDown : InputKind.KeyUp, 0, 0, virtualKey, scanCode, extended ? FlagExtended : (byte)0);

    public short WheelDelta => unchecked((short)Arg1);
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
            if (kind is < InputKind.MouseMove or > InputKind.ReleaseAll) return false;
            var e = new InputEvent(kind, BinaryPrimitives.ReadUInt16BigEndian(message[(o + 2)..]), BinaryPrimitives.ReadUInt16BigEndian(message[(o + 4)..]),
                BinaryPrimitives.ReadUInt16BigEndian(message[(o + 6)..]), BinaryPrimitives.ReadUInt16BigEndian(message[(o + 8)..]), message[o + 1]);
            if (kind is InputKind.MouseDown or InputKind.MouseUp && e.Arg1 > (ushort)MouseButton.X2) return false;
            events.Add(e);
        }
        return true;
    }
}
