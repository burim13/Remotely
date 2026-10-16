using System.Buffers.Binary;

namespace Remotely.HostCore;

/// <summary>Message kinds inside the encrypted viewer/host session. First byte of every message.</summary>
public static class HostMessages
{
    // viewer -> host
    public const byte FrameRequest = 0x01;
    public const byte Input = 0x02;
    public const byte Clipboard = 0x03;
    public const byte SecureAttention = 0x04;
    public const byte FileStart = 0x10;
    public const byte FileChunk = 0x11;
    public const byte FileEnd = 0x12;
    public const byte ShareList = 0x13;
    public const byte ShareGet = 0x14;

    // host -> viewer
    public const byte Hello = 0x80;
    public const byte FrameReply = 0x81;
    public const byte Unchanged = 0x82;
    public const byte ClipboardReply = 0x83;
    public const byte FileAck = 0x90;
    public const byte ShareListReply = 0x91;
    public const byte ShareFileStart = 0x92;
    public const byte ShareFileChunk = 0x93;
    public const byte ShareFileEnd = 0x94;
    public const byte Error = 0xFF;

    public const int ProtocolVersion = 1;
    public const int MaxClipboardChars = 256 * 1024;
    public const int FileChunkBytes = 512 * 1024;
    public const long MaxFileBytes = 512L * 1024 * 1024;

    public static byte[] Prefixed(byte kind, ReadOnlySpan<byte> body)
    {
        var message = new byte[1 + body.Length];
        message[0] = kind;
        body.CopyTo(message.AsSpan(1));
        return message;
    }

    /// <summary>kind + u32 id + payload.</summary>
    public static byte[] WithId(byte kind, uint id, ReadOnlySpan<byte> payload)
    {
        var message = new byte[5 + payload.Length];
        message[0] = kind;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(1), id);
        payload.CopyTo(message.AsSpan(5));
        return message;
    }

    public static bool TryReadId(ReadOnlySpan<byte> message, out uint id, out ReadOnlySpan<byte> rest)
    {
        if (message.Length < 5) { id = 0; rest = default; return false; }
        id = BinaryPrimitives.ReadUInt32BigEndian(message[1..]);
        rest = message[5..];
        return true;
    }
}

[Flags]
public enum HostCapabilities : byte
{
    None = 0,
    Control = 1,          // this viewer may send input
    SecureAttention = 2,  // Ctrl+Alt+Del can be sent (service mode only)
    Clipboard = 4,
    Files = 8,
    // Reserved for Remotely 1.0 work (one owner each, see docs/agent-specs.md). Not offered by any host yet.
    Monitors = 16,        // A6: monitor list in the hello extension, per-monitor capture, tiled frames
    CursorShape = 32,     // A6: cursor image sent separately from pixels
    RichClipboard = 64,   // A7: HTML, images and file lists on the clipboard
    ResumableFiles = 128, // A8: transfers with offsets (resume)
}

public sealed record HelloInfo(int Version, HostCapabilities Capabilities)
{
    public byte[] Encode() => [HostMessages.Hello, (byte)Version, (byte)Capabilities];

    public static HelloInfo? Decode(ReadOnlySpan<byte> message) =>
        message.Length >= 3 && message[0] == HostMessages.Hello ? new HelloInfo(message[1], (HostCapabilities)message[2]) : null;
}
