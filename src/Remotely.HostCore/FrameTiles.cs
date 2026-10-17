using System.Buffers.Binary;
using System.Text;

namespace Remotely.HostCore;

/// <summary>One display of the host. X and Y are in virtual-desktop pixels (the primary display is at 0,0; other displays may have negative coordinates).</summary>
public sealed record MonitorInfo(int Index, string Name, int X, int Y, int Width, int Height, bool Primary);

/// <summary>A rectangle of the screen as its own JPEG. Coordinates are pixels of the captured area (0,0 is its top-left corner).</summary>
public sealed record FrameTile(int X, int Y, int Width, int Height, byte[] Jpeg);

/// <summary>The mouse pointer image. <see cref="Bgra"/> is straight (not premultiplied) alpha, Width*Height*4 bytes, top-down rows.</summary>
public sealed record CursorShape(ulong Id, int HotspotX, int HotspotY, int Width, int Height, byte[] Bgra);

/// <summary>Where the pointer is on the host, in virtual-desktop pixels, and which <see cref="CursorShape.Id"/> it currently has.</summary>
public sealed record CursorState(bool Visible, ulong ShapeId, int X, int Y);

/// <summary>Wire ids for the capture engine. Viewer to host 0x20-0x2F, host to viewer 0xA0-0xAF (see docs/protocol.md).</summary>
public static class FrameMessages
{
    // viewer -> host
    /// <summary>[0x20] flags(1: bit0 tiles, bit1 cursor) quality(1: 0 = host default, else JPEG quality 10..95) monitor(i8: -1 = whole virtual desktop).</summary>
    public const byte StreamConfig = 0x20;
    /// <summary>[0x21] The next frame reply is a complete picture.</summary>
    public const byte KeyFrameRequest = 0x21;

    // host -> viewer
    public const byte TileFrame = 0xA0;
    public const byte CursorShapeReply = 0xA1;
    public const byte CursorStateReply = 0xA2;
    public const byte MonitorList = 0xA3;

    public const byte FlagTiles = 1, FlagCursor = 2;
    public const byte WholeDesktop = 0xFF;

    public static bool IsViewerMessage(byte kind) => kind is >= 0x20 and <= 0x2F;
    public static bool IsHostMessage(byte kind) => kind is >= 0xA0 and <= 0xAF;

    public static byte[] EncodeStreamConfig(bool tiles, bool cursor, int quality, int monitor) =>
        [StreamConfig, (byte)((tiles ? FlagTiles : 0) | (cursor ? FlagCursor : 0)), (byte)Math.Clamp(quality, 0, 95), (byte)(sbyte)Math.Clamp(monitor, -1, 126)];
}

/// <summary>Everything one capture call returns. <see cref="Frame"/> is null when the pixels did not change since this viewer's last frame.</summary>
public sealed record TileCapture(IReadOnlyList<MonitorInfo> Monitors, TiledFrame? Frame, CursorState? Cursor, CursorShape? NewShape);

/// <summary>A change set for the picture: either one full-size tile (key frame) or the tiles that changed.</summary>
public sealed record TiledFrame(int Width, int Height, string Desktop, int Monitor, bool IsKey, IReadOnlyList<FrameTile> Tiles, int CaptureMicros, int EncodeMicros);

/// <summary>What the host asks its capture source for, per viewer. <see cref="SessionId"/> keys the per-viewer "what did this viewer last receive" state inside the source.</summary>
public sealed record TileRequest(int SessionId, int Monitor, int Quality, bool ForceKey, bool MonitorsOnly, ulong KnownCursorId, bool WantCursor);

/// <summary>Byte layouts of the capture messages. All integers big-endian.</summary>
public static class FrameWire
{
    public const int MaxTiles = 4096;
    public const int MaxCursorSide = 256;
    private const int MaxDimension = 32768;

    // ---- monitors (hello extension and 0xA3) ----

    public static byte[] EncodeMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        using var ms = new MemoryStream();
        var count = Math.Min(monitors.Count, 32);
        ms.WriteByte((byte)count);
        Span<byte> tmp = stackalloc byte[16];
        for (var i = 0; i < count; i++)
        {
            var m = monitors[i];
            var name = Encoding.UTF8.GetBytes(m.Name);
            if (name.Length > 60) name = name[..60];
            ms.WriteByte((byte)m.Index);
            ms.WriteByte(m.Primary ? (byte)1 : (byte)0);
            BinaryPrimitives.WriteInt32BigEndian(tmp, m.X);
            BinaryPrimitives.WriteInt32BigEndian(tmp[4..], m.Y);
            BinaryPrimitives.WriteInt32BigEndian(tmp[8..], m.Width);
            BinaryPrimitives.WriteInt32BigEndian(tmp[12..], m.Height);
            ms.Write(tmp);
            ms.WriteByte((byte)name.Length);
            ms.Write(name);
        }
        return ms.ToArray();
    }

    /// <exception cref="FormatException">Malformed list.</exception>
    public static MonitorInfo[] DecodeMonitors(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1) throw new FormatException("Truncated monitor list.");
        var count = data[0];
        var list = new MonitorInfo[count];
        var pos = 1;
        for (var i = 0; i < count; i++)
        {
            if (data.Length < pos + 19) throw new FormatException("Truncated monitor list.");
            var index = data[pos];
            var primary = data[pos + 1] == 1;
            var x = BinaryPrimitives.ReadInt32BigEndian(data[(pos + 2)..]);
            var y = BinaryPrimitives.ReadInt32BigEndian(data[(pos + 6)..]);
            var w = BinaryPrimitives.ReadInt32BigEndian(data[(pos + 10)..]);
            var h = BinaryPrimitives.ReadInt32BigEndian(data[(pos + 14)..]);
            var nameLength = data[pos + 18];
            pos += 19;
            if (data.Length < pos + nameLength || w is < 1 or > MaxDimension || h is < 1 or > MaxDimension) throw new FormatException("Bad monitor entry.");
            list[i] = new MonitorInfo(index, Encoding.UTF8.GetString(data.Slice(pos, nameLength)), x, y, w, h, primary);
            pos += nameLength;
        }
        return list;
    }

    // ---- 0xA0 tile frame body (after the kind byte) ----
    // flags(1: bit0 key) monitor(1) width(i32) height(i32) desktopLen(1) desktop captureUs(u32) encodeUs(u32) tileCount(u16) { x y w h (u16 each) len(u32) jpeg }*

    public static byte[] EncodeTileFrame(TiledFrame frame)
    {
        var desktop = Encoding.UTF8.GetBytes(frame.Desktop);
        if (desktop.Length > 64) throw new ArgumentException("Desktop name too long.", nameof(frame));
        if (frame.Tiles.Count is < 1 or > MaxTiles) throw new ArgumentException("A frame needs 1 to " + MaxTiles + " tiles.", nameof(frame));
        var size = 1 + 1 + 4 + 4 + 1 + desktop.Length + 4 + 4 + 2;
        foreach (var t in frame.Tiles) size += 8 + 4 + t.Jpeg.Length;
        var buffer = new byte[size];
        var span = buffer.AsSpan();
        span[0] = frame.IsKey ? (byte)1 : (byte)0;
        span[1] = frame.Monitor < 0 ? FrameMessages.WholeDesktop : (byte)frame.Monitor;
        BinaryPrimitives.WriteInt32BigEndian(span[2..], frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(span[6..], frame.Height);
        span[10] = (byte)desktop.Length;
        desktop.CopyTo(span[11..]);
        var pos = 11 + desktop.Length;
        BinaryPrimitives.WriteUInt32BigEndian(span[pos..], (uint)Math.Max(0, frame.CaptureMicros));
        BinaryPrimitives.WriteUInt32BigEndian(span[(pos + 4)..], (uint)Math.Max(0, frame.EncodeMicros));
        BinaryPrimitives.WriteUInt16BigEndian(span[(pos + 8)..], (ushort)frame.Tiles.Count);
        pos += 10;
        foreach (var t in frame.Tiles)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[pos..], (ushort)t.X);
            BinaryPrimitives.WriteUInt16BigEndian(span[(pos + 2)..], (ushort)t.Y);
            BinaryPrimitives.WriteUInt16BigEndian(span[(pos + 4)..], (ushort)t.Width);
            BinaryPrimitives.WriteUInt16BigEndian(span[(pos + 6)..], (ushort)t.Height);
            BinaryPrimitives.WriteUInt32BigEndian(span[(pos + 8)..], (uint)t.Jpeg.Length);
            t.Jpeg.CopyTo(span[(pos + 12)..]);
            pos += 12 + t.Jpeg.Length;
        }
        return buffer;
    }

    /// <exception cref="FormatException">Malformed frame.</exception>
    public static TiledFrame DecodeTileFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length < 21) throw new FormatException("Truncated tile frame.");
        var isKey = (data[0] & 1) != 0;
        var monitor = data[1] == FrameMessages.WholeDesktop ? -1 : data[1];
        var width = BinaryPrimitives.ReadInt32BigEndian(data[2..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(data[6..]);
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension) throw new FormatException("Bad frame size.");
        var nameLength = data[10];
        var pos = 11 + nameLength;
        if (data.Length < pos + 10) throw new FormatException("Truncated tile frame.");
        var desktop = Encoding.UTF8.GetString(data.Slice(11, nameLength));
        var capture = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data[pos..]));
        var encode = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]));
        var count = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 8)..]);
        pos += 10;
        if (count is < 1 or > MaxTiles) throw new FormatException("Bad tile count.");
        var tiles = new List<FrameTile>(count);
        for (var i = 0; i < count; i++)
        {
            if (data.Length < pos + 12) throw new FormatException("Truncated tile.");
            int x = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]), y = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            int w = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 4)..]), h = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 6)..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 8)..]);
            pos += 12;
            if (w == 0 || h == 0 || x + w > width || y + h > height) throw new FormatException("Tile outside the frame.");
            if (length > (uint)(data.Length - pos)) throw new FormatException("Truncated tile data.");
            tiles.Add(new FrameTile(x, y, w, h, data.Slice(pos, (int)length).ToArray()));
            pos += (int)length;
        }
        if (pos != data.Length) throw new FormatException("Trailing bytes after the tiles.");
        if (isKey && !(tiles.Count == 1 && tiles[0] is { X: 0, Y: 0 } t0 && t0.Width == width && t0.Height == height))
            throw new FormatException("A key frame must be one full-size tile.");
        return new TiledFrame(width, height, desktop, monitor, isKey, tiles, capture, encode);
    }

    // ---- 0xA1 / 0xA2 bodies ----

    public static byte[] EncodeShape(CursorShape shape)
    {
        if (shape.Width is < 1 or > MaxCursorSide || shape.Height is < 1 or > MaxCursorSide || shape.Bgra.Length != shape.Width * shape.Height * 4)
            throw new ArgumentException("Bad cursor image.", nameof(shape));
        var buffer = new byte[8 + 2 + 2 + 2 + 2 + shape.Bgra.Length];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, shape.Id);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(8), (ushort)Math.Clamp(shape.HotspotX, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(10), (ushort)Math.Clamp(shape.HotspotY, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(12), (ushort)shape.Width);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(14), (ushort)shape.Height);
        shape.Bgra.CopyTo(buffer, 16);
        return buffer;
    }

    /// <exception cref="FormatException">Malformed shape.</exception>
    public static CursorShape DecodeShape(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) throw new FormatException("Truncated cursor shape.");
        var id = BinaryPrimitives.ReadUInt64BigEndian(data);
        int hx = BinaryPrimitives.ReadUInt16BigEndian(data[8..]), hy = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        int w = BinaryPrimitives.ReadUInt16BigEndian(data[12..]), h = BinaryPrimitives.ReadUInt16BigEndian(data[14..]);
        if (w is < 1 or > MaxCursorSide || h is < 1 or > MaxCursorSide || data.Length != 16 + w * h * 4) throw new FormatException("Bad cursor image.");
        return new CursorShape(id, hx, hy, w, h, data[16..].ToArray());
    }

    public static byte[] EncodeState(CursorState state)
    {
        var buffer = new byte[17];
        buffer[0] = state.Visible ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(1), state.ShapeId);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(9), state.X);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(13), state.Y);
        return buffer;
    }

    /// <exception cref="FormatException">Malformed state.</exception>
    public static CursorState DecodeState(ReadOnlySpan<byte> data)
    {
        if (data.Length != 17) throw new FormatException("Bad cursor state.");
        return new CursorState((data[0] & 1) != 0, BinaryPrimitives.ReadUInt64BigEndian(data[1..]),
            BinaryPrimitives.ReadInt32BigEndian(data[9..]), BinaryPrimitives.ReadInt32BigEndian(data[13..]));
    }

    // ---- service <-> helper pipe: request and reply (not seen by viewers) ----

    public static byte[] EncodeRequest(TileRequest r)
    {
        var buffer = new byte[4 + 1 + 1 + 1 + 8 + 1];
        BinaryPrimitives.WriteInt32BigEndian(buffer, r.SessionId);
        buffer[4] = (byte)(sbyte)Math.Clamp(r.Monitor, -1, 126);
        buffer[5] = (byte)Math.Clamp(r.Quality, 0, 95);
        buffer[6] = (byte)((r.ForceKey ? 1 : 0) | (r.MonitorsOnly ? 2 : 0) | (r.WantCursor ? 4 : 0));
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(7), r.KnownCursorId);
        buffer[15] = 0x54; // 'T': marks a tile request (an empty payload is the old "capture full frame" call)
        return buffer;
    }

    public static bool TryDecodeRequest(ReadOnlySpan<byte> data, out TileRequest request)
    {
        if (data.Length != 16 || data[15] != 0x54) { request = default!; return false; }
        request = new TileRequest(BinaryPrimitives.ReadInt32BigEndian(data), (sbyte)data[4], data[5], (data[6] & 1) != 0, (data[6] & 2) != 0,
            BinaryPrimitives.ReadUInt64BigEndian(data[7..]), (data[6] & 4) != 0);
        return true;
    }

    /// <summary>monitors | flags(1: bit0 frame, bit1 cursor state, bit2 new shape) | [frame] | [state] | [shape], each part with a u32 length.</summary>
    public static byte[] EncodeCapture(TileCapture c)
    {
        using var ms = new MemoryStream();
        void Part(byte[] bytes)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)bytes.Length);
            ms.Write(len);
            ms.Write(bytes);
        }
        Part(EncodeMonitors(c.Monitors));
        ms.WriteByte((byte)((c.Frame is not null ? 1 : 0) | (c.Cursor is not null ? 2 : 0) | (c.NewShape is not null ? 4 : 0)));
        if (c.Frame is not null) Part(EncodeTileFrame(c.Frame));
        if (c.Cursor is not null) Part(EncodeState(c.Cursor));
        if (c.NewShape is not null) Part(EncodeShape(c.NewShape));
        return ms.ToArray();
    }

    /// <exception cref="FormatException">Malformed reply.</exception>
    public static TileCapture DecodeCapture(byte[] data)
    {
        var pos = 0;
        ReadOnlySpan<byte> Part()
        {
            if (data.Length < pos + 4) throw new FormatException("Truncated capture reply.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            pos += 4;
            if (length > (uint)(data.Length - pos)) throw new FormatException("Truncated capture reply.");
            var part = data.AsSpan(pos, (int)length);
            pos += (int)length;
            return part;
        }
        var monitors = DecodeMonitors(Part());
        if (data.Length <= pos) throw new FormatException("Truncated capture reply.");
        var flags = data[pos++];
        var frame = (flags & 1) != 0 ? DecodeTileFrame(Part()) : null;
        var cursor = (flags & 2) != 0 ? DecodeState(Part()) : null;
        var shape = (flags & 4) != 0 ? DecodeShape(Part()) : null;
        return new TileCapture(monitors, frame, cursor, shape);
    }
}
