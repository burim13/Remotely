using System.Diagnostics;
using Remotely.Host.Capture;
using Remotely.HostCore;

namespace Remotely.Host.Helper;

/// <summary>
/// Captures whichever desktop has input. Two ways to ask:
/// <see cref="CaptureAsync"/> is protocol version 1: the whole virtual desktop as one JPEG, via GDI, exactly as before.
/// <see cref="CaptureTilesAsync"/> is protocol version 2: one display or the whole desktop, only the rectangles that changed for the asking viewer,
/// the pointer separately, Desktop Duplication (DXGI) when Windows allows it and GDI otherwise. JPEG encoding runs on pool threads, never on the desktop thread.
/// </summary>
internal sealed class DesktopCapturer(DesktopThread desktop, Action<string>? log = null) : ITiledFrameSource, IDisposable
{
    private const int Attempts = 5;
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StatsEvery = TimeSpan.FromSeconds(10);
    private static readonly int[] LegacyQualities = [60, 45, 30, 15];

    private readonly ScreenCapturer _screen = new(log);
    private readonly CursorGrabber _cursor = new();
    private readonly JpegTileEncoder _encoder = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, TileSessionState> _states = [];
    private readonly Stats _stats = new();
    private int _logged;

    private sealed class Stats
    {
        public long Requests, Unchanged, Keys, Bytes, CaptureMicros, EncodeMicros, TotalMicros;
        public long Since = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Right after the screen locks or unlocks, Windows is switching the input desktop and GDI can fail briefly with "Access is denied" on the
    /// handle we just opened (seen on Windows 11 ARM64). Each retry re-opens the current input desktop, which resolves it.
    /// </summary>
    private async Task<T> RetryOnDesktopSwitchAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await work().ConfigureAwait(false); }
            catch (InvalidOperationException ex) when (attempt < Attempts && ex.Message.Contains("failed:", StringComparison.Ordinal))
            {
                log?.Invoke($"Capture attempt {attempt} failed ({ex.Message}) Retrying on the current input desktop.");
                await Task.Delay(150 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<FrameInfo> CaptureAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RetryOnDesktopSwitchAsync(async () =>
            {
                var shot = await desktop.RunAsync(name => _screen.Grab(name, -1, allowDxgi: false)).ConfigureAwait(false);
                var image = shot.Image;
                var jpeg = await Task.Run(() => EncodeFullFrame(image), ct).ConfigureAwait(false);
                return new FrameInfo(image.Width, image.Height, image.Desktop, jpeg);
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private byte[] EncodeFullFrame(RawImage image)
    {
        foreach (var quality in LegacyQualities)
        {
            var jpeg = _encoder.Encode(image.Bgra, image.Width * 4, image.Width, image.Height, quality);
            if (jpeg.Length <= TileEngine.MaxMessageBytes) return jpeg;
        }
        throw new InvalidOperationException("The screen is too detailed to send as one frame yet.");
    }

    public async Task<TileCapture> CaptureTilesAsync(TileRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (request.MonitorsOnly)
            {
                var monitors = await RetryOnDesktopSwitchAsync(() => desktop.RunAsync(_ => (IReadOnlyList<MonitorInfo>)_screen.Monitors().Select(m => m.Info).ToList()), ct).ConfigureAwait(false);
                return new TileCapture(monitors, null, null, null);
            }

            var (shot, cursor) = await RetryOnDesktopSwitchAsync(() => desktop.RunAsync(name =>
            {
                var s = _screen.Grab(name, request.Monitor, allowDxgi: true);
                var c = request.WantCursor ? _cursor.Read(request.KnownCursorId) : null;
                return (s, c);
            }), ct).ConfigureAwait(false);

            var state = StateFor(request.SessionId);
            var clock = Stopwatch.StartNew();
            var frame = await Task.Run(() => TileEngine.Build(shot.Image, state, _encoder, request.Quality, request.ForceKey), ct).ConfigureAwait(false);
            Record(shot, frame, clock.Elapsed);
            return new TileCapture(shot.Monitors, frame, cursor?.State, cursor?.NewShape);
        }
        finally { _gate.Release(); }
    }

    private TileSessionState StateFor(int sessionId)
    {
        var now = DateTime.UtcNow;
        foreach (var stale in _states.Where(p => now - p.Value.LastUsedUtc > StateLifetime).Select(p => p.Key).ToList()) _states.Remove(stale);
        if (!_states.TryGetValue(sessionId, out var state)) _states[sessionId] = state = new TileSessionState();
        return state;
    }

    /// <summary>Per-request numbers in the host log: the first few one by one, then a summary every ten seconds. They are the measurements docs/performance.md refers to.</summary>
    private void Record(Shot shot, TiledFrame? frame, TimeSpan buildTime)
    {
        if (log is null) return;
        var s = _stats;
        s.Requests++;
        s.CaptureMicros += shot.Image.CaptureMicros;
        s.TotalMicros += buildTime.Ticks / 10;
        if (frame is null) s.Unchanged++;
        else
        {
            s.EncodeMicros += frame.EncodeMicros;
            if (frame.IsKey) s.Keys++;
            foreach (var t in frame.Tiles) s.Bytes += t.Jpeg.Length;
        }
        if (_logged < 3)
        {
            _logged++;
            log($"Capture [{shot.Path}] {shot.Image.Width}x{shot.Image.Height} monitor={shot.Image.Monitor}: grab {shot.Image.CaptureMicros / 1000.0:0.0} ms, diff+encode {buildTime.TotalMilliseconds:0.0} ms, " +
                (frame is null ? "unchanged" : $"{(frame.IsKey ? "key frame" : frame.Tiles.Count + " tile(s)")}, {frame.Tiles.Sum(t => t.Jpeg.Length) / 1024.0:0.0} KB"));
        }
        var elapsed = Stopwatch.GetElapsedTime(s.Since);
        if (elapsed < StatsEvery) return;
        var sent = Math.Max(1, s.Requests - s.Unchanged);
        log($"Capture stats [{shot.Path}]: {s.Requests} requests in {elapsed.TotalSeconds:0.0} s ({s.Unchanged} unchanged, {s.Keys} key frames), " +
            $"grab {s.CaptureMicros / 1000.0 / s.Requests:0.0} ms avg, diff+encode {s.TotalMicros / 1000.0 / s.Requests:0.0} ms avg, {s.Bytes / 1024.0 / sent:0.0} KB per sent frame.");
        _stats.Requests = _stats.Unchanged = _stats.Keys = _stats.Bytes = _stats.CaptureMicros = _stats.EncodeMicros = _stats.TotalMicros = 0;
        _stats.Since = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        _screen.Dispose();
        _gate.Dispose();
    }
}
