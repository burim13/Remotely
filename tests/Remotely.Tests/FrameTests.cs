using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Remotely.Core.Crypto;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.Tests;

/// <summary>Capture engine: wire formats, tile diffing, adaptive quality, and interoperation with peers that speak protocol version 1.</summary>
public class FrameTests
{
    // ---------- test doubles ----------

    /// <summary>Stand-in for JPEG: header + the raw pixels, so tests can rebuild the picture exactly from tiles.</summary>
    internal sealed class RawEncoder : ITileEncoder
    {
        public int Calls;
        public long PixelsEncoded;
        public byte[] Encode(ReadOnlySpan<byte> bgra, int stride, int width, int height, int quality)
        {
            Interlocked.Increment(ref Calls);
            Interlocked.Add(ref PixelsEncoded, (long)width * height);
            var output = new byte[5 + width * height * 4];
            BinaryPrimitives.WriteUInt16BigEndian(output, (ushort)width);
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), (ushort)height);
            output[4] = (byte)quality;
            for (var y = 0; y < height; y++) bgra.Slice(y * stride, width * 4).CopyTo(output.AsSpan(5 + y * width * 4));
            return output;
        }
    }

    /// <summary>What a viewer does with tiles: paint them over the previous picture.</summary>
    internal sealed class Canvas(int width, int height)
    {
        public readonly byte[] Pixels = new byte[width * height * 4];
        public void Apply(FrameTile tile)
        {
            var w = BinaryPrimitives.ReadUInt16BigEndian(tile.Jpeg);
            var h = BinaryPrimitives.ReadUInt16BigEndian(tile.Jpeg.AsSpan(2));
            Assert.Equal((tile.Width, tile.Height), (w, h));
            for (var y = 0; y < h; y++)
                tile.Jpeg.AsSpan(5 + y * w * 4, w * 4).CopyTo(Pixels.AsSpan(((tile.Y + y) * width + tile.X) * 4));
        }
        public void Apply(TiledFrame frame) { foreach (var t in frame.Tiles) Apply(t); }
        public void Apply(FrameInfo frame)
        {
            if (frame.Tiles is null) Apply(new FrameTile(0, 0, frame.Width, frame.Height, frame.Jpeg));
            else foreach (var t in frame.Tiles) Apply(t);
        }
    }

    private static RawImage Image(byte[] pixels, int w, int h, long version, string desktop = "Default", int monitor = -1) =>
        new(w, h, desktop, monitor, pixels, version, 1500);

    private static byte[] Noise(int w, int h, int seed)
    {
        var bytes = new byte[w * h * 4];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static void Paint(byte[] pixels, int w, int x, int y, int rw, int rh, byte value)
    {
        for (var row = y; row < y + rh; row++) Array.Fill(pixels, value, (row * w + x) * 4, rw * 4);
    }

    internal sealed class MemoryChannel : IMessageChannel
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private MemoryChannel? _peer;
        public static (MemoryChannel A, MemoryChannel B) Pair()
        {
            var a = new MemoryChannel();
            var b = new MemoryChannel { _peer = a };
            a._peer = b;
            return (a, b);
        }
        public Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
        {
            _peer!._incoming.Writer.TryWrite(plaintext.ToArray());
            return Task.CompletedTask;
        }
        public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
        {
            try { return await _incoming.Reader.ReadAsync(ct); }
            catch (ChannelClosedException) { return null; }
        }
        public Task CloseAsync() { _peer?._incoming.Writer.TryComplete(); _incoming.Writer.TryComplete(); return Task.CompletedTask; }
        public void Dispose() { }
    }

    /// <summary>A capture source that tracks one picture per viewer session through the real <see cref="TileEngine"/>.</summary>
    internal sealed class FakeTiledSource(int width, int height) : ITiledFrameSource
    {
        public readonly RawEncoder Encoder = new();
        public byte[] Pixels = new byte[width * height * 4];
        public long Version = 1;
        public int LegacyCaptures, TileCaptures;
        public readonly List<TileRequest> Requests = [];
        public MonitorInfo[] Monitors = [new(0, "\\\\.\\DISPLAY1", 0, 0, width, height, true), new(1, "\\\\.\\DISPLAY2", width, 0, width, height, false)];
        public CursorShape? Shape;
        private readonly Dictionary<int, TileSessionState> _states = [];

        public void Change(Action<byte[]> edit) { lock (this) { edit(Pixels); Version++; } }

        public Task<FrameInfo> CaptureAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref LegacyCaptures);
            return Task.FromResult(new FrameInfo(width, height, "Default", Encoding.ASCII.GetBytes("legacy-" + Version)));
        }

        public Task<TileCapture> CaptureTilesAsync(TileRequest request, CancellationToken ct)
        {
            lock (this)
            {
                lock (Requests) Requests.Add(request);
                if (request.MonitorsOnly) return Task.FromResult(new TileCapture(Monitors, null, null, null));
                Interlocked.Increment(ref TileCaptures);
                if (!_states.TryGetValue(request.SessionId, out var state)) _states[request.SessionId] = state = new TileSessionState();
                var frame = TileEngine.Build(Image((byte[])Pixels.Clone(), width, height, Version, monitor: request.Monitor), state, Encoder, request.Quality, request.ForceKey);
                CursorShape? shape = request.WantCursor && Shape is { } s && s.Id != request.KnownCursorId ? s : null;
                var cursor = request.WantCursor ? new CursorState(true, Shape?.Id ?? 0, 10, 20) : null;
                return Task.FromResult(new TileCapture(Monitors, frame, cursor, shape));
            }
        }
    }

    private static async Task<T> Eventually<T>(Func<T?> probe, string what) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } v) return v;
            await Task.Delay(10);
        }
        throw new TimeoutException("Timed out waiting for " + what);
    }

    private static HostAgent NewHost(IFrameSource source, Action<string>? log = null) =>
        new(DeviceIdentity.Generate(), new HostServices(source) { IsTrusted = _ => true }, log ?? (_ => { }));

    // ---------- wire formats ----------

    [Fact]
    public void Monitor_list_tile_frame_cursor_and_requests_roundtrip()
    {
        MonitorInfo[] monitors = [new(0, "\\\\.\\DISPLAY1", 0, 0, 1920, 1080, true), new(1, "Ünïcode", -1280, -200, 1280, 1024, false)];
        Assert.Equal(monitors, FrameWire.DecodeMonitors(FrameWire.EncodeMonitors(monitors)));

        var frame = new TiledFrame(1920, 1080, "Winlogon", 1, false, [new(0, 64, 128, 64, [1, 2, 3]), new(1792, 1016, 128, 64, [9])], 1234, 5678);
        var back = FrameWire.DecodeTileFrame(FrameWire.EncodeTileFrame(frame));
        Assert.Equal((1920, 1080, "Winlogon", 1, false, 1234, 5678), (back.Width, back.Height, back.Desktop, back.Monitor, back.IsKey, back.CaptureMicros, back.EncodeMicros));
        Assert.Equal(2, back.Tiles.Count);
        Assert.Equal(frame.Tiles[1].Jpeg, back.Tiles[1].Jpeg);
        Assert.Equal((1792, 1016, 128, 64), (back.Tiles[1].X, back.Tiles[1].Y, back.Tiles[1].Width, back.Tiles[1].Height));
        Assert.Equal(-1, FrameWire.DecodeTileFrame(FrameWire.EncodeTileFrame(frame with { Monitor = -1 })).Monitor);

        var shape = new CursorShape(0xFEEDFACE12345678, 3, 4, 2, 2, new byte[16]);
        Assert.Equal(shape.Id, FrameWire.DecodeShape(FrameWire.EncodeShape(shape)).Id);
        var state = new CursorState(true, 77, -5, 900);
        Assert.Equal(state, FrameWire.DecodeState(FrameWire.EncodeState(state)));

        var request = new TileRequest(42, -1, 70, true, false, 99, true);
        Assert.True(FrameWire.TryDecodeRequest(FrameWire.EncodeRequest(request), out var r));
        Assert.Equal(request, r);
        Assert.False(FrameWire.TryDecodeRequest([], out _));

        var capture = new TileCapture(monitors, frame, state, shape);
        var c2 = FrameWire.DecodeCapture(FrameWire.EncodeCapture(capture));
        Assert.Equal(monitors, c2.Monitors);
        Assert.Equal(state, c2.Cursor);
        Assert.Equal(shape.Id, c2.NewShape!.Id);
        Assert.Equal(2, c2.Frame!.Tiles.Count);
        Assert.Null(FrameWire.DecodeCapture(FrameWire.EncodeCapture(new TileCapture(monitors, null, null, null))).Frame);
    }

    [Fact]
    public void Malformed_capture_messages_are_rejected_not_trusted()
    {
        var good = FrameWire.EncodeTileFrame(new TiledFrame(100, 100, "Default", -1, false, [new(0, 0, 50, 50, [1, 2, 3])], 0, 0));
        Assert.Throws<FormatException>(() => FrameWire.DecodeTileFrame(good[..^1]));
        Assert.Throws<FormatException>(() => FrameWire.DecodeTileFrame([.. good, 0]));
        Assert.Throws<FormatException>(() => FrameWire.DecodeTileFrame([]));
        var outside = FrameWire.EncodeTileFrame(new TiledFrame(100, 100, "Default", -1, false, [new(60, 0, 50, 50, [1])], 0, 0));
        Assert.Throws<FormatException>(() => FrameWire.DecodeTileFrame(outside));   // 60 + 50 > 100
        var partialKey = FrameWire.EncodeTileFrame(new TiledFrame(100, 100, "Default", -1, true, [new(0, 0, 50, 50, [1])], 0, 0));
        Assert.Throws<FormatException>(() => FrameWire.DecodeTileFrame(partialKey));
        var lying = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(lying.AsSpan(lying.Length - 3 - 4), 0xFFFFFFF0);
        Assert.Throws<FormatException>(() => FrameWire.DecodeTileFrame(lying));
        Assert.Throws<FormatException>(() => FrameWire.DecodeMonitors([5, 0]));
        Assert.Throws<FormatException>(() => FrameWire.DecodeShape(new byte[16]));
        Assert.Throws<FormatException>(() => FrameWire.DecodeState(new byte[3]));
        Assert.Throws<FormatException>(() => FrameWire.DecodeCapture([0, 0, 0, 9]));
    }

    [Fact]
    public void A_version_1_viewer_can_read_the_version_2_hello_and_a_version_2_viewer_reads_both()
    {
        var caps = HostCapabilities.Control | HostCapabilities.Monitors | HostCapabilities.CursorShape;
        var v2 = new HelloInfo(HostMessages.ProtocolVersion, caps, [new MonitorInfo(0, "m", 0, 0, 800, 600, true)]).Encode();

        // The decoder shipped in protocol version 1 (HostMessages.cs and mac/RemotelyKit): exactly this, nothing else.
        static (int Version, byte Caps)? V1Decode(byte[] m) => m.Length >= 3 && m[0] == 0x80 ? (m[1], m[2]) : null;
        Assert.Equal((1, (byte)caps), V1Decode(v2));
        Assert.Equal(1, V1Decode(new HelloInfo(1, HostCapabilities.Clipboard).Encode())!.Value.Version);
        Assert.Equal(3, new HelloInfo(1, HostCapabilities.Clipboard).Encode().Length);   // byte-for-byte what version 1 hosts send

        var decoded = HelloInfo.Decode(v2)!;
        Assert.Equal(2, decoded.Version);
        Assert.Equal(caps, decoded.Capabilities);
        Assert.Single(decoded.MonitorList);
        var old = HelloInfo.Decode([0x80, 1, (byte)HostCapabilities.Files])!;
        Assert.Equal((1, HostCapabilities.Files), (old.Version, old.Capabilities));
        Assert.Empty(old.MonitorList);
        Assert.Equal(1, HelloInfo.Decode([.. v2[..4], 200, 200])!.Version);   // a damaged extension is ignored, the base hello still works
    }

    // ---------- tile engine ----------

    [Fact]
    public void Tiles_reproduce_the_screen_exactly_and_only_changed_areas_are_encoded()
    {
        const int W = 400, H = 300;
        var screen = Noise(W, H, 1);
        var encoder = new RawEncoder();
        var state = new TileSessionState();
        var canvas = new Canvas(W, H);

        var first = TileEngine.Build(Image(screen, W, H, 1), state, encoder, 0, false)!;
        Assert.True(first.IsKey);
        Assert.Single(first.Tiles);
        canvas.Apply(first);
        Assert.Equal(screen, canvas.Pixels);

        Assert.Null(TileEngine.Build(Image(screen, W, H, 1), state, encoder, 0, false));                       // same version: nothing
        Assert.Null(TileEngine.Build(Image((byte[])screen.Clone(), W, H, 2), state, encoder, 0, false));        // new version, same pixels: nothing

        var before = encoder.PixelsEncoded;
        var edited = (byte[])screen.Clone();
        Paint(edited, W, 70, 70, 10, 10, 7);                                                                     // inside tile (1,1)
        var partial = TileEngine.Build(Image(edited, W, H, 3), state, encoder, 0, false)!;
        Assert.False(partial.IsKey);
        Assert.Equal([new FrameTile(64, 64, 64, 64, partial.Tiles[0].Jpeg)], partial.Tiles);
        Assert.Equal(64 * 64, encoder.PixelsEncoded - before);
        canvas.Apply(partial);
        Assert.Equal(edited, canvas.Pixels);
    }

    [Fact]
    public void Adjacent_changed_tiles_are_merged_and_edge_tiles_are_clipped()
    {
        const int W = 300, H = 200;                                  // not multiples of 64: right and bottom tiles are partial
        var screen = Noise(W, H, 2);
        var state = new TileSessionState();
        var encoder = new RawEncoder();
        TileEngine.Build(Image(screen, W, H, 1), state, encoder, 0, false);
        var edited = (byte[])screen.Clone();
        Paint(edited, W, 0, 0, 130, 70, 9);                           // covers tile columns 0..2 and rows 0..1
        var frame = TileEngine.Build(Image(edited, W, H, 2), state, encoder, 0, false)!;
        Assert.Single(frame.Tiles);                                    // one rectangle, not six
        Assert.Equal((0, 0, 192, 128), (frame.Tiles[0].X, frame.Tiles[0].Y, frame.Tiles[0].Width, frame.Tiles[0].Height));

        var corner = (byte[])edited.Clone();
        Paint(corner, W, 290, 195, 10, 5, 3);                          // bottom-right corner: a 44x8 partial tile
        var f2 = TileEngine.Build(Image(corner, W, H, 3), state, encoder, 0, false)!;
        Assert.Equal((256, 192, 44, 8), (f2.Tiles[0].X, f2.Tiles[0].Y, f2.Tiles[0].Width, f2.Tiles[0].Height));
    }

    [Fact]
    public void Random_edits_always_leave_the_viewer_with_the_exact_screen()
    {
        const int W = 517, H = 389;
        var random = new Random(1234);
        var screen = Noise(W, H, 3);
        var state = new TileSessionState();
        var encoder = new RawEncoder();
        var canvas = new Canvas(W, H);
        long version = 1;
        for (var step = 0; step < 150; step++)
        {
            if (step > 0)
            {
                version++;
                var rw = random.Next(1, step % 10 == 0 ? W : 120);
                var rh = random.Next(1, step % 10 == 0 ? H : 120);
                Paint(screen, W, random.Next(0, W - rw + 1), random.Next(0, H - rh + 1), rw, rh, (byte)random.Next(256));
            }
            var frame = TileEngine.Build(Image((byte[])screen.Clone(), W, H, version), state, encoder, 0, step % 37 == 36);
            if (frame is not null) canvas.Apply(frame);
            Assert.Equal(screen, canvas.Pixels);
        }
    }

    [Fact]
    public void A_mostly_changed_screen_or_a_new_size_or_desktop_sends_a_complete_picture()
    {
        const int W = 256, H = 256;
        var screen = Noise(W, H, 4);
        var state = new TileSessionState();
        var encoder = new RawEncoder();
        TileEngine.Build(Image(screen, W, H, 1), state, encoder, 0, false);
        Assert.True(TileEngine.Build(Image(Noise(W, H, 5), W, H, 2), state, encoder, 0, false)!.IsKey);          // everything changed
        Assert.True(TileEngine.Build(Image(Noise(W / 2, H, 6), W / 2, H, 3), state, encoder, 0, false)!.IsKey);   // resolution changed
        Assert.True(TileEngine.Build(Image(Noise(W / 2, H, 6), W / 2, H, 4, "Winlogon"), state, encoder, 0, false)!.IsKey); // lock screen
        Assert.True(TileEngine.Build(Image(Noise(W / 2, H, 6), W / 2, H, 5, "Winlogon", 1), state, encoder, 0, false)!.IsKey); // other monitor
        Assert.True(TileEngine.Build(Image(Noise(W / 2, H, 6), W / 2, H, 5, "Winlogon", 1), state, encoder, 0, true)!.IsKey);  // asked for
        state.Invalidate();
        Assert.True(TileEngine.Build(Image(Noise(W / 2, H, 6), W / 2, H, 5, "Winlogon", 1), state, encoder, 0, false)!.IsKey);
    }

    [Fact]
    public void Each_viewer_gets_what_it_is_missing_not_what_another_viewer_already_has()
    {
        const int W = 192, H = 128;
        var screen = Noise(W, H, 7);
        var encoder = new RawEncoder();
        TileSessionState a = new(), b = new();
        var canvasA = new Canvas(W, H);
        var canvasB = new Canvas(W, H);
        canvasA.Apply(TileEngine.Build(Image(screen, W, H, 1), a, encoder, 0, false)!);
        Paint(screen, W, 0, 0, 10, 10, 1);
        canvasA.Apply(TileEngine.Build(Image(screen, W, H, 2), a, encoder, 0, false)!);      // A has seen both versions
        var forB = TileEngine.Build(Image(screen, W, H, 2), b, encoder, 0, false)!;           // B joins late: complete picture
        Assert.True(forB.IsKey);
        canvasB.Apply(forB);
        Assert.Equal(canvasA.Pixels, canvasB.Pixels);
        Assert.Equal(screen, canvasB.Pixels);
    }

    [Fact]
    public void A_screen_too_detailed_to_compress_fails_with_a_message_instead_of_sending_garbage()
    {
        var state = new TileSessionState();
        var ex = Assert.Throws<InvalidOperationException>(() => TileEngine.Build(Image(Noise(512, 512, 8), 512, 512, 1), state, new RawEncoder(), 0, false));
        Assert.Contains("too detailed", ex.Message);
    }

    // ---------- adaptive quality ----------

    [Fact]
    public void Quality_falls_on_a_slow_link_and_recovers_on_a_fast_one()
    {
        var slow = new FrameAdaptation();
        for (var i = 0; i < 8; i++) slow.OnReply(0, TimeSpan.FromMilliseconds(40), TimeSpan.Zero);   // 40 ms round trip, idle
        for (var i = 0; i < 40; i++) slow.OnReply(120_000, TimeSpan.FromMilliseconds(40 + 480), TimeSpan.FromMilliseconds(10));   // 120 KB in ~430 ms = ~280 KB/s
        Assert.Equal(FrameAdaptation.MinQuality, slow.Quality);
        Assert.InRange(slow.BandwidthBytesPerSecond, 200_000, 400_000);
        Assert.InRange(slow.RoundTripMs, 30, 50);
        Assert.True(slow.ChangedInterval > TimeSpan.FromMilliseconds(100));      // degraded to fewer frames per second at the floor
        Assert.True(slow.TryTakeQualityChange(out var q) && q == FrameAdaptation.MinQuality);
        Assert.False(slow.TryTakeQualityChange(out _));

        var fast = new FrameAdaptation(40);
        for (var i = 0; i < 8; i++) fast.OnReply(0, TimeSpan.FromMilliseconds(2), TimeSpan.Zero);
        for (var i = 0; i < 60; i++) fast.OnReply(60_000, TimeSpan.FromMilliseconds(2 + 6 + 8), TimeSpan.FromMilliseconds(8));  // 60 KB in ~6 ms = 10 MB/s
        Assert.Equal(FrameAdaptation.MaxQuality, fast.Quality);
        Assert.Equal(TimeSpan.FromMilliseconds(33), fast.ChangedInterval);
    }

    // ---------- host and viewer together, and against peers of the previous version ----------

    [Fact]
    public async Task A_version_2_viewer_and_host_exchange_tiles_cursor_and_monitor_choices()
    {
        var source = new FakeTiledSource(256, 192) { Shape = new CursorShape(5, 1, 1, 2, 2, new byte[16]) };
        Array.Copy(Noise(256, 192, 9), source.Pixels, source.Pixels.Length);
        var host = NewHost(source);
        var (hostSide, viewerSide) = MemoryChannel.Pair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = host.ServeChannelAsync(hostSide, "test-viewer", control: false, cts.Token);

        await using var session = await ViewerSession.OpenAsync(viewerSide, "host", cts.Token);
        Assert.Equal(2, session.Hello.Version);
        Assert.True(session.SupportsTiledFrames && session.SupportsCursorShape);
        Assert.Equal(2, session.Monitors.Count);
        Assert.Equal((256, 192), (session.Monitors[0].Width, session.Monitors[0].Height));

        var canvas = new Canvas(256, 192);
        var frames = new List<FrameInfo>();
        var shapes = new List<CursorShape>();
        var cursors = new List<CursorState>();
        session.FrameReceived += f => { lock (frames) { frames.Add(f); if (f.Tiles is not null || Encoding.ASCII.GetString(f.Jpeg.AsSpan(0, Math.Min(7, f.Jpeg.Length))) != "legacy-") canvas.Apply(f); } };
        session.CursorShapeReceived += shapes.Add;
        session.CursorMoved += cursors.Add;
        await session.EnableTiledFramesAsync();

        await Eventually(() => { lock (frames) return frames.Any(f => f.Tiles is null && f.Jpeg.Length > 100) ? frames : null; }, "key frame");
        await Eventually(() => shapes.Count > 0 && cursors.Count > 0 ? shapes : null, "cursor");
        Assert.Equal((ulong)5, shapes[0].Id);
        Assert.Equal((10, 20), (cursors[0].X, cursors[0].Y));

        source.Change(p => Paint(p, 256, 130, 70, 8, 8, 200));
        var partial = await Eventually(() => { lock (frames) return frames.FirstOrDefault(f => f.Tiles is not null); }, "partial frame");
        Assert.Empty(partial.Jpeg);
        Assert.Equal((128, 64), (partial.Tiles![0].X, partial.Tiles[0].Y));
        Assert.True(partial.CaptureMicros > 0);
        await Eventually(() => { lock (frames) return canvas.Pixels.AsSpan().SequenceEqual(source.Pixels) ? frames : null; }, "viewer picture equals host picture");

        await session.SelectMonitorAsync(1);
        await Eventually(() => { lock (source.Requests) return source.Requests.LastOrDefault(r => !r.MonitorsOnly && r.Monitor == 1 && r.ForceKey); }, "request for monitor 1 with a key frame");
        Assert.Equal(1, source.Requests.Last(r => !r.MonitorsOnly).Monitor);
    }

    [Fact]
    public async Task A_version_1_viewer_still_gets_full_frame_jpeg_from_a_version_2_host()
    {
        var source = new FakeTiledSource(64, 64);
        var host = NewHost(source);
        var (hostSide, viewerSide) = MemoryChannel.Pair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = host.ServeChannelAsync(hostSide, "v1-viewer", control: false, cts.Token);

        // Everything below uses only what protocol version 1 defined.
        var hello = await viewerSide.ReceiveAsync(cts.Token);
        Assert.True(hello!.Length >= 3 && hello[0] == 0x80 && hello[1] == 1);
        for (var i = 0; i < 3; i++)
        {
            await viewerSide.SendAsync(new byte[] { 0x01 }, cts.Token);
            var reply = await viewerSide.ReceiveAsync(cts.Token);
            if (i == 0)
            {
                Assert.Equal(0x81, reply![0]);
                var frame = FrameCodec.Decode(reply.AsSpan(1));
                Assert.Equal((64, 64, "Default", "legacy-1"), (frame.Width, frame.Height, frame.Desktop, Encoding.ASCII.GetString(frame.Jpeg)));
                Assert.Null(frame.Tiles);
            }
            else Assert.Equal(0x82, reply![0]);    // unchanged
        }
        Assert.Equal(0, source.TileCaptures);
        Assert.DoesNotContain(source.Requests, r => !r.MonitorsOnly);

        await viewerSide.SendAsync(new byte[] { 0x01 }, cts.Token);
        source.Change(_ => { });
        var changed = await viewerSide.ReceiveAsync(cts.Token);
        Assert.Contains(changed![0], new byte[] { 0x81, 0x82 });
        await viewerSide.CloseAsync();
        await serving;
    }

    [Fact]
    public async Task A_version_2_viewer_works_with_a_version_1_host()
    {
        var (hostSide, viewerSide) = MemoryChannel.Pair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // A host exactly as protocol version 1 defined it: 3-byte hello, 0x81 frames, 0x82 when unchanged.
        var oldHost = Task.Run(async () =>
        {
            await hostSide.SendAsync(new byte[] { 0x80, 1, (byte)HostCapabilities.Clipboard });
            var sent = 0;
            while (await hostSide.ReceiveAsync(cts.Token) is { } m)
            {
                if (m[0] == 0x01)
                    await hostSide.SendAsync(sent++ % 4 == 0 ? HostMessages.Prefixed(0x81, FrameCodec.Encode(new FrameInfo(32, 24, "Default", [1, 2, 3]))) : new byte[] { 0x82 });
                else await hostSide.SendAsync(HostMessages.Prefixed(0xFF, Encoding.UTF8.GetBytes("That request is not available.")));
            }
        });

        await using var session = await ViewerSession.OpenAsync(viewerSide, "old-host", cts.Token);
        Assert.Equal(1, session.Hello.Version);
        Assert.False(session.SupportsTiledFrames);
        Assert.Empty(session.Monitors);
        var errors = new List<string>();
        session.HostError += errors.Add;
        var frames = new List<FrameInfo>();
        session.FrameReceived += f => { lock (frames) frames.Add(f); };
        await session.EnableTiledFramesAsync();   // must be a no-op: nothing is sent that a version 1 host would answer with an error
        await session.SelectMonitorAsync(1);
        await Eventually(() => { lock (frames) return frames.Count > 0 ? frames : null; }, "frame");
        await Task.Delay(300);
        Assert.False(session.TiledFramesEnabled);
        Assert.Empty(errors);
        Assert.Equal(new byte[] { 1, 2, 3 }, frames[0].Jpeg);
        Assert.Null(frames[0].Tiles);
        await session.DisposeAsync();
        await hostSide.CloseAsync();
        await oldHost;
    }

    [Fact]
    public async Task A_host_whose_source_cannot_tile_refuses_stream_config_with_an_error()
    {
        var host = NewHost(new HostCoreTests.FakeScreen(() => new FrameInfo(8, 8, "Default", [1])));
        var (hostSide, viewerSide) = MemoryChannel.Pair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = host.ServeChannelAsync(hostSide, "viewer", control: false, cts.Token);
        var hello = HelloInfo.Decode((await viewerSide.ReceiveAsync(cts.Token))!)!;
        Assert.Equal(1, hello.Version);
        Assert.False(hello.Capabilities.HasFlag(HostCapabilities.Monitors));
        await viewerSide.SendAsync(FrameMessages.EncodeStreamConfig(true, true, 50, -1), cts.Token);
        var reply = await viewerSide.ReceiveAsync(cts.Token);
        Assert.Equal(0xFF, reply![0]);
        await viewerSide.CloseAsync();
        await serving;
    }
}
