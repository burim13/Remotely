using System.Buffers.Binary;
using System.Text;

namespace Remotely.HostCore;

/// <summary>
/// One captured screen image. <see cref="Desktop"/> is the Windows desktop it came from ("Default" normally, "Winlogon" for the lock screen and UAC).
/// The first four members are the version 1 shape and keep their meaning. When <see cref="Tiles"/> is null, <see cref="Jpeg"/> is the complete picture.
/// A viewer that asked for tiled frames (<c>ViewerSession.EnableTiledFramesAsync</c>) can also receive frames with <see cref="Tiles"/> set: then
/// <see cref="Jpeg"/> is empty and each tile is a JPEG of one rectangle to paint over the previous picture (<see cref="Width"/> and <see cref="Height"/> still give the full size).
/// </summary>
public sealed record FrameInfo(int Width, int Height, string Desktop, byte[] Jpeg)
{
    /// <summary>True when the host sent this through the version 2 tile stream (as opposed to a version 1 full-frame reply).</summary>
    public bool FromTileStream { get; init; }
    /// <summary>The changed rectangles of a partial update; null for a complete picture.</summary>
    public IReadOnlyList<FrameTile>? Tiles { get; init; }
    /// <summary>Which display this picture shows (<see cref="MonitorInfo.Index"/>), -1 for the whole virtual desktop or when the host does not say.</summary>
    public int Monitor { get; init; } = -1;
    /// <summary>How long the host spent capturing and compressing this update, in microseconds (0 when the host does not say).</summary>
    public int CaptureMicros { get; init; }
    public int EncodeMicros { get; init; }
}

/// <summary>Frame wire format (used on the helper pipe and inside the encrypted session): ver(1) w(i32 BE) h(i32 BE) desktopLen(1) desktop jpegLen(u32 BE) jpeg.</summary>
public static class FrameCodec
{
    private const byte Version = 1;
    private const int MaxDimension = 32768;

    public static byte[] Encode(FrameInfo frame)
    {
        var desktop = Encoding.UTF8.GetBytes(frame.Desktop);
        if (desktop.Length > 64) throw new ArgumentException("Desktop name too long.", nameof(frame));
        var buffer = new byte[1 + 4 + 4 + 1 + desktop.Length + 4 + frame.Jpeg.Length];
        var span = buffer.AsSpan();
        span[0] = Version;
        BinaryPrimitives.WriteInt32BigEndian(span[1..], frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(span[5..], frame.Height);
        span[9] = (byte)desktop.Length;
        desktop.CopyTo(span[10..]);
        BinaryPrimitives.WriteUInt32BigEndian(span[(10 + desktop.Length)..], (uint)frame.Jpeg.Length);
        frame.Jpeg.CopyTo(span[(14 + desktop.Length)..]);
        return buffer;
    }

    /// <exception cref="FormatException">Malformed frame.</exception>
    public static FrameInfo Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14 || data[0] != Version) throw new FormatException("Not a frame.");
        var width = BinaryPrimitives.ReadInt32BigEndian(data[1..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(data[5..]);
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension) throw new FormatException("Bad frame size.");
        var nameLength = data[9];
        if (data.Length < 14 + nameLength) throw new FormatException("Truncated frame.");
        var desktop = Encoding.UTF8.GetString(data.Slice(10, nameLength));
        var jpegLength = BinaryPrimitives.ReadUInt32BigEndian(data[(10 + nameLength)..]);
        if (jpegLength != (uint)(data.Length - 14 - nameLength)) throw new FormatException("Frame length mismatch.");
        return new FrameInfo(width, height, desktop, data[(14 + nameLength)..].ToArray());
    }
}
