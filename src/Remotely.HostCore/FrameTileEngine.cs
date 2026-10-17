using System.Diagnostics;

namespace Remotely.HostCore;

/// <summary>Compresses a rectangle of 32-bit BGRA pixels (alpha ignored) to JPEG. Implementations must be safe to call from several threads at once.</summary>
public interface ITileEncoder
{
    /// <param name="bgra">First pixel of the rectangle; rows are <paramref name="stride"/> bytes apart and the span reaches the last row's last pixel.</param>
    byte[] Encode(ReadOnlySpan<byte> bgra, int stride, int width, int height, int quality);
}

/// <summary>A full screen image in BGRA with rows of Width*4 bytes. <see cref="Version"/> changes whenever the pixels might have; equal versions guarantee equal pixels.</summary>
public sealed record RawImage(int Width, int Height, string Desktop, int Monitor, byte[] Bgra, long Version, int CaptureMicros);

/// <summary>What one viewer has last been sent, so the next frame carries only what changed for that viewer.</summary>
public sealed class TileSessionState
{
    internal byte[]? Baseline;
    internal int Width, Height, Monitor = int.MinValue;
    internal string? Desktop;
    internal long Version = -1;
    public DateTime LastUsedUtc { get; internal set; } = DateTime.UtcNow;
    public ulong SentCursorId { get; set; }

    /// <summary>The next frame will be a complete picture. Call this when a frame built from this state could not be delivered.</summary>
    public void Invalidate() { Baseline = null; Version = -1; }
}

/// <summary>Finds which parts of the screen changed since a viewer's last frame and compresses only those.</summary>
public static class TileEngine
{
    public const int TileSize = 64;
    public const int DefaultQuality = 65;
    public const int MaxMessageBytes = 900_000;
    private const int MaxRects = 48;

    private readonly record struct Rect(int X, int Y, int W, int H);

    /// <summary>Returns the frame for this viewer, or null when nothing changed for it.</summary>
    /// <exception cref="InvalidOperationException">The picture cannot be compressed under <see cref="MaxMessageBytes"/>.</exception>
    public static TiledFrame? Build(RawImage image, TileSessionState state, ITileEncoder encoder, int quality, bool forceKey)
    {
        quality = quality <= 0 ? DefaultQuality : Math.Clamp(quality, 10, 95);
        state.LastUsedUtc = DateTime.UtcNow;
        var w = image.Width;
        var h = image.Height;
        var needKey = forceKey || state.Baseline is null || state.Width != w || state.Height != h || state.Monitor != image.Monitor
                      || !string.Equals(state.Desktop, image.Desktop, StringComparison.Ordinal);
        if (!needKey && state.Version == image.Version) return null;

        var clock = Stopwatch.StartNew();
        if (!needKey)
        {
            var rects = ChangedRects(image, state.Baseline!, out var changedTiles, out var totalTiles);
            if (rects.Count == 0) { state.Version = image.Version; return null; }
            if (rects.Count <= MaxRects && changedTiles * 2 <= totalTiles)
            {
                var jpegs = new byte[rects.Count][];
                if (rects.Count == 1) jpegs[0] = EncodeRect(image, rects[0], encoder, quality);
                else Parallel.For(0, rects.Count, i => jpegs[i] = EncodeRect(image, rects[i], encoder, quality));
                var total = 0L;
                foreach (var j in jpegs) total += j.Length;
                if (total <= MaxMessageBytes)
                {
                    var tiles = new List<FrameTile>(rects.Count);
                    for (var i = 0; i < rects.Count; i++)
                    {
                        var r = rects[i];
                        tiles.Add(new FrameTile(r.X, r.Y, r.W, r.H, jpegs[i]));
                        CopyRect(image.Bgra, state.Baseline!, w, r);
                    }
                    state.Version = image.Version;
                    return new TiledFrame(w, h, image.Desktop, image.Monitor, false, tiles, image.CaptureMicros, Micros(clock));
                }
            }
        }

        var key = EncodeKey(image, encoder, quality);
        if (state.Baseline is null || state.Baseline.Length != image.Bgra.Length) state.Baseline = new byte[image.Bgra.Length];
        Buffer.BlockCopy(image.Bgra, 0, state.Baseline, 0, image.Bgra.Length);
        (state.Width, state.Height, state.Monitor, state.Desktop, state.Version) = (w, h, image.Monitor, image.Desktop, image.Version);
        return new TiledFrame(w, h, image.Desktop, image.Monitor, true, [new FrameTile(0, 0, w, h, key)], image.CaptureMicros, Micros(clock));
    }

    private static int Micros(Stopwatch clock) => (int)Math.Min(int.MaxValue, clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency);

    private static byte[] EncodeKey(RawImage image, ITileEncoder encoder, int quality)
    {
        int[] ladder = [quality, quality - 15, quality - 30, 15];
        var tried = new HashSet<int>();
        foreach (var q in ladder)
        {
            var clamped = Math.Max(10, q);
            if (!tried.Add(clamped)) continue;
            var jpeg = encoder.Encode(image.Bgra, image.Width * 4, image.Width, image.Height, clamped);
            if (jpeg.Length <= MaxMessageBytes) return jpeg;
        }
        throw new InvalidOperationException("The screen is too detailed to send as one frame yet.");
    }

    private static byte[] EncodeRect(RawImage image, Rect r, ITileEncoder encoder, int quality)
    {
        var stride = image.Width * 4;
        var start = r.Y * stride + r.X * 4;
        var length = (r.H - 1) * stride + r.W * 4;
        return encoder.Encode(image.Bgra.AsSpan(start, length), stride, r.W, r.H, quality);
    }

    private static void CopyRect(byte[] source, byte[] target, int width, Rect r)
    {
        var stride = width * 4;
        for (var y = r.Y; y < r.Y + r.H; y++)
            Buffer.BlockCopy(source, y * stride + r.X * 4, target, y * stride + r.X * 4, r.W * 4);
    }

    private static List<Rect> ChangedRects(RawImage image, byte[] baseline, out int changedTiles, out int totalTiles)
    {
        var w = image.Width;
        var h = image.Height;
        var cols = (w + TileSize - 1) / TileSize;
        var rows = (h + TileSize - 1) / TileSize;
        totalTiles = cols * rows;
        changedTiles = 0;
        var stride = w * 4;
        var rects = new List<Rect>();
        var open = new Dictionary<(int C0, int C1), int>(); // column span -> index in rects of the rectangle that ended on the previous tile row
        var next = new Dictionary<(int C0, int C1), int>();
        for (var ty = 0; ty < rows; ty++)
        {
            var y0 = ty * TileSize;
            var th = Math.Min(TileSize, h - y0);
            next.Clear();
            var tx = 0;
            while (tx < cols)
            {
                if (!TileDiffers(image.Bgra, baseline, stride, w, tx, y0, th)) { tx++; continue; }
                var c0 = tx;
                while (tx < cols && TileDiffers(image.Bgra, baseline, stride, w, tx, y0, th)) { changedTiles++; tx++; }
                var key = (c0, tx - 1);
                var x0 = c0 * TileSize;
                var rw = Math.Min(tx * TileSize, w) - x0;
                if (open.TryGetValue(key, out var index))
                {
                    var r = rects[index];
                    rects[index] = r with { H = y0 + th - r.Y };
                    next[key] = index;
                }
                else
                {
                    rects.Add(new Rect(x0, y0, rw, th));
                    next[key] = rects.Count - 1;
                }
            }
            (open, next) = (next, open);
        }
        return rects;
    }

    private static bool TileDiffers(byte[] current, byte[] baseline, int stride, int width, int tx, int y0, int th)
    {
        var x0 = tx * TileSize;
        var bytes = Math.Min(TileSize, width - x0) * 4;
        for (var y = 0; y < th; y++)
        {
            var offset = (y0 + y) * stride + x0 * 4;
            if (!current.AsSpan(offset, bytes).SequenceEqual(baseline.AsSpan(offset, bytes))) return true;
        }
        return false;
    }
}
