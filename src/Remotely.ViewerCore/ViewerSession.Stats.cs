namespace Remotely.ViewerCore;

/// <summary>A reading of <see cref="SessionStats"/>. <see cref="RoundTrip"/> is null until something reports one.</summary>
public readonly record struct StatsSnapshot(double FramesPerSecond, double BytesPerSecond, long TotalFrames, long TotalBytes, TimeSpan? RoundTrip, TimeSpan? SinceLastFrame, string Codec);

/// <summary>
/// Read-only session numbers derived from <see cref="ViewerSession.FrameReceived"/>. Bytes are the compressed picture bytes of received frames
/// (input, clipboard and file traffic are not counted). The protocol has no ping today, so the round trip is only known if a caller reports one.
/// </summary>
public sealed class SessionStats
{
    private const long WindowMs = 2000;
    private readonly Func<long> _nowMs;
    private readonly object _gate = new();
    private readonly Queue<(long At, int Bytes)> _recent = new();
    private readonly long _startedAt;
    private long _frames, _bytes, _lastFrameAt = -1;
    private TimeSpan? _roundTrip;

    public SessionStats() : this(() => Environment.TickCount64) { }

    /// <summary>For tests: a clock that returns milliseconds.</summary>
    public SessionStats(Func<long> nowMs)
    {
        _nowMs = nowMs;
        _startedAt = nowMs();
    }

    public void RecordFrame(int bytes)
    {
        lock (_gate)
        {
            var now = _nowMs();
            _recent.Enqueue((now, bytes));
            _frames++;
            _bytes += bytes;
            _lastFrameAt = now;
            Trim(now);
        }
    }

    /// <summary>For the protocol layer to feed a measured request-to-reply time.</summary>
    public void ReportRoundTrip(TimeSpan roundTrip)
    {
        lock (_gate) _roundTrip = roundTrip;
    }

    public StatsSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _nowMs();
            Trim(now);
            var window = Math.Min(WindowMs, Math.Max(1, now - _startedAt)) / 1000.0;
            long bytes = 0;
            foreach (var r in _recent) bytes += r.Bytes;
            return new StatsSnapshot(_recent.Count / window, bytes / window, _frames, _bytes, _roundTrip,
                _lastFrameAt < 0 ? null : TimeSpan.FromMilliseconds(now - _lastFrameAt), "JPEG");
        }
    }

    private void Trim(long now)
    {
        while (_recent.Count > 0 && now - _recent.Peek().At > WindowMs) _recent.Dequeue();
    }
}

public sealed partial class ViewerSession
{
    private SessionStats? _stats;
    private readonly object _receivedStatsGate = new();

    /// <summary>Counts frames from the first time this is read, so read it right after opening the session.</summary>
    public SessionStats ReceivedStats
    {
        get
        {
            lock (_receivedStatsGate)
            {
                if (_stats is not null) return _stats;
                var stats = new SessionStats();
                FrameReceived += f => stats.RecordFrame(f.Jpeg.Length);
                if (LatestFrame is { } early) stats.RecordFrame(early.Jpeg.Length);   // a frame that arrived before the first read still counts
                return _stats = stats;
            }
        }
    }
}
