using System.Buffers.Binary;

namespace Remotely.Host.Helper;

/// <summary>
/// Framing for the service/helper pipes. Request: kind(1) length(u32 BE) payload. Reply: status(1: 0 ok, 1 error text) length(u32 BE) payload.
/// Two pipes are used so input is never queued behind a screen capture: "main" (capture, clipboard) and "input".
/// </summary>
internal static class HelperRpc
{
    public const byte Capture = 1, Input = 2, ClipboardRead = 3, ClipboardWrite = 4;
    public const int MaxPayload = 4 * 1024 * 1024;

    public static byte[] Frame(byte first, ReadOnlySpan<byte> payload)
    {
        var message = new byte[5 + payload.Length];
        message[0] = first;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(message.AsSpan(5));
        return message;
    }

    public static (byte First, byte[] Payload) ReadBlocking(Stream stream)
    {
        var header = new byte[5];
        if (!ReadFully(stream, header)) throw new EndOfStreamException();
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        if (length > MaxPayload) throw new InvalidDataException("Message too large.");
        var payload = new byte[length];
        if (length > 0 && !ReadFully(stream, payload)) throw new EndOfStreamException();
        return (header[0], payload);
    }

    private static bool ReadFully(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
