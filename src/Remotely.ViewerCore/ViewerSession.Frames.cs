using System.Diagnostics;
using Remotely.HostCore;

namespace Remotely.ViewerCore;

/// <summary>What the stream looked like over the last couple of seconds, for a statistics overlay.</summary>
public sealed record StreamStats(double FramesPerSecond, double BytesPerFrame, double RoundTripMs, double BandwidthKBytesPerSecond, int Quality,
    double HostCaptureMs, double HostEncodeMs, bool Tiled);

public sealed partial class ViewerSession
{
    private static readonly TimeSpan StatsWindow = TimeSpan.FromSeconds(2);

    private readonly FrameAdaptation _adaptation = new();
    private readonly Queue<long> _frameTimes = new();
    private readonly object _statsGate = new();
    private volatile bool _tiles;
    private volatile bool _adaptive = true;
    private int _monitor = -1;
    private volatile IReadOnlyList<MonitorInfo>? _monitors;
    private int _fixedQuality;
    private int _replyBytes, _hostCaptureMicros, _hostEncodeMicros;
    private double _lastCaptureMs, _lastEncodeMs;

    /// <summary>True when the host speaks protocol version 2 and offers tiled frames, the display list and cursor messages.</summary>
    public bool SupportsTiledFrames => Hello.Version >= 2 && Hello.Capabilities.HasFlag(HostCapabilities.Monitors);
    public bool SupportsCursorShape => Hello.Version >= 2 && Hello.Capabilities.HasFlag(HostCapabilities.CursorShape);
    /// <summary>The host's displays (empty for a version 1 host). Updated when the host sends a new list.</summary>
    public IReadOnlyList<MonitorInfo> Monitors => _monitors ?? Hello.MonitorList;
    /// <summary>Which display is being streamed: its index, or -1 for the whole virtual desktop.</summary>
    public int SelectedMonitor => Volatile.Read(ref _monitor);
    public bool TiledFramesEnabled => _tiles;
    /// <summary>The JPEG quality the viewer currently asks the host for.</summary>
    public int Quality => _adaptation.Quality;

    /// <summary>When true (the default) the quality follows the measured round trip and bandwidth.</summary>
    public bool AdaptiveQuality { get => _adaptive; set => _adaptive = value; }

    public event Action<IReadOnlyList<MonitorInfo>>? MonitorsChanged;
    /// <summary>The pointer image changed. Cache it by <see cref="CursorShape.Id"/>; <see cref="CursorMoved"/> says which one is current.</summary>
    public event Action<CursorShape>? CursorShapeReceived;
    public event Action<CursorState>? CursorMoved;

    /// <summary>
    /// Switches this session to tiled frames. After this, <see cref="FrameReceived"/> delivers either a full picture (<see cref="FrameInfo.Tiles"/> is null and
    /// <see cref="FrameInfo.Jpeg"/> is the whole screen) or only the rectangles that changed (<see cref="FrameInfo.Tiles"/> is set and Jpeg is empty); the
    /// receiver must paint tiles over the previous picture. Does nothing on a version 1 host. Sessions that never call this get full-frame JPEG as before.
    /// </summary>
    public Task EnableTiledFramesAsync()
    {
        if (!SupportsTiledFrames) return Task.CompletedTask;
        _tiles = true;
        return SendStreamConfigAsync();
    }

    /// <summary>Streams one display (its <see cref="MonitorInfo.Index"/>) or, with -1, the whole virtual desktop. The next frame is a complete picture.</summary>
    public Task SelectMonitorAsync(int monitor)
    {
        if (!SupportsTiledFrames) return Task.CompletedTask;
        Volatile.Write(ref _monitor, monitor);
        return SendStreamConfigAsync();
    }

    /// <summary>Turns adaptation off and asks the host for this JPEG quality (10 to 95; 0 leaves the choice to the host).</summary>
    public Task SetFixedQualityAsync(int quality)
    {
        _adaptive = false;
        _fixedQuality = quality;
        return SupportsTiledFrames ? SendStreamConfigAsync() : Task.CompletedTask;
    }

    /// <summary>Asks for a complete picture, for example after the window was hidden or restored.</summary>
    public Task RequestKeyFrameAsync() =>
        SupportsTiledFrames ? _secure.SendAsync(new[] { FrameMessages.KeyFrameRequest }, _cts.Token) : Task.CompletedTask;

    public StreamStats Stats
    {
        get
        {
            double fps;
            lock (_statsGate)
            {
                Trim(Stopwatch.GetTimestamp());
                fps = _frameTimes.Count / StatsWindow.TotalSeconds;
            }
            return new StreamStats(fps, _adaptation.BytesPerFrame, _adaptation.RoundTripMs, _adaptation.BandwidthBytesPerSecond / 1024.0,
                _adaptation.Quality, _lastCaptureMs, _lastEncodeMs, _tiles);
        }
    }

    private Task SendStreamConfigAsync() =>
        _secure.SendAsync(FrameMessages.EncodeStreamConfig(_tiles, SupportsCursorShape, _adaptive ? _adaptation.Quality : _fixedQuality, Volatile.Read(ref _monitor)), _cts.Token);

    private void Trim(long now)
    {
        var limit = now - (long)(StatsWindow.TotalSeconds * Stopwatch.Frequency);
        while (_frameTimes.Count > 0 && _frameTimes.Peek() < limit) _frameTimes.Dequeue();
    }

    private void NoteFrame()
    {
        var now = Stopwatch.GetTimestamp();
        lock (_statsGate) { _frameTimes.Enqueue(now); Trim(now); }
    }

    /// <summary>Host to viewer messages 0xA0-0xAF, called from the receive loop.</summary>
    private void HandleFrameMessage(byte[] message)
    {
        try
        {
            switch (message[0])
            {
                case FrameMessages.TileFrame:
                    var frame = FrameWire.DecodeTileFrame(message.AsSpan(1));
                    if (_tiles)
                    {
                        _replyBytes = message.Length;
                        _hostCaptureMicros = frame.CaptureMicros;
                        _hostEncodeMicros = frame.EncodeMicros;
                        FrameReceived?.Invoke(frame.IsKey
                            ? new FrameInfo(frame.Width, frame.Height, frame.Desktop, frame.Tiles[0].Jpeg) { FromTileStream = true, Monitor = frame.Monitor, CaptureMicros = frame.CaptureMicros, EncodeMicros = frame.EncodeMicros }
                            : new FrameInfo(frame.Width, frame.Height, frame.Desktop, []) { FromTileStream = true, Tiles = frame.Tiles, Monitor = frame.Monitor, CaptureMicros = frame.CaptureMicros, EncodeMicros = frame.EncodeMicros });
                        NoteFrame();
                    }
                    _frameWaiter.TrySetResult(true);
                    break;
                case FrameMessages.CursorShapeReply:
                    CursorShapeReceived?.Invoke(FrameWire.DecodeShape(message.AsSpan(1)));
                    break;
                case FrameMessages.CursorStateReply:
                    CursorMoved?.Invoke(FrameWire.DecodeState(message.AsSpan(1)));
                    break;
                case FrameMessages.MonitorList:
                    var list = FrameWire.DecodeMonitors(message.AsSpan(1));
                    _monitors = list;
                    MonitorsChanged?.Invoke(list);
                    break;
            }
        }
        catch (FormatException ex)
        {
            HostError?.Invoke("Bad frame message: " + ex.Message);
            if (message[0] == FrameMessages.TileFrame) _frameWaiter.TrySetResult(false);
        }
    }

    private async Task FrameLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                _replyBytes = 0;
                var waiter = _frameWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await _secure.SendAsync(new[] { HostMessages.FrameRequest }, _cts.Token).ConfigureAwait(false);
                var changed = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(started);

                var interval = changed ? MinFrameInterval : UnchangedBackoff;
                if (_tiles)
                {
                    var hostWork = TimeSpan.FromMicroseconds(_hostCaptureMicros + (long)_hostEncodeMicros);
                    _lastCaptureMs = _hostCaptureMicros / 1000.0;
                    _lastEncodeMs = _hostEncodeMicros / 1000.0;
                    _adaptation.OnReply(changed ? _replyBytes : 0, elapsed, changed ? hostWork : TimeSpan.Zero);
                    interval = changed ? _adaptation.ChangedInterval : _adaptation.IdleInterval;
                    if (_adaptive && _adaptation.TryTakeQualityChange(out _)) await SendStreamConfigAsync().ConfigureAwait(false);
                }
                var wait = interval - elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (TimeoutException) { _ = EndAsync("The host stopped responding."); }
        catch (Exception ex) { _ = EndAsync(ex.Message); }
    }
}
