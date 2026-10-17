namespace Remotely.Core.Diagnostics;

public enum QualityLevel { Unknown, Good, Fair, Poor, Lost }

/// <summary>Why the level is what it is; the session window turns this into a sentence (resource key <c>Diag_Quality_{Reason}</c>).</summary>
public enum QualityReason { NoData, Healthy, SlowRoundTrip, UnstableRoundTrip, FramesStalled, NoReply, Reconnecting }

public sealed record QualitySnapshot(QualityLevel Level, QualityReason Reason, double? RoundTripMs, double? JitterMs, double? FramesPerSecond, int Stalls);

/// <summary>
/// The limits that turn measurements into Good, Fair and Poor. They are starting values chosen by reading what is usual for remote desktop use, not numbers that were
/// measured on real networks; change them here and the tests that name the limits show what moves.
/// </summary>
public sealed record QualityThresholds
{
    /// <summary>How far back samples count.</summary>
    public int WindowMs { get; init; } = 10_000;
    public double GoodRoundTripMs { get; init; } = 100;
    public double FairRoundTripMs { get; init; } = 250;
    public double GoodJitterMs { get; init; } = 40;
    public double FairJitterMs { get; init; } = 100;
    /// <summary>Nothing heard (no round trip, no frame) for this long: Lost.</summary>
    public int NoReplyMs { get; init; } = 5_000;
    /// <summary>A better level is shown only after it has held this long, so the indicator does not flicker. A worse level is shown at once.</summary>
    public int ImproveAfterMs { get; init; } = 3_000;
    public int PoorStalls { get; init; } = 3;
}

/// <summary>
/// The connection quality indicator, without any UI: the session feeds it round-trip times (a ping and its answer), received frames and stalls (no frame when one was due),
/// and asks for a <see cref="QualitySnapshot"/> now and then. Time comes from <c>nowMs</c> (a monotonic millisecond clock), so tests need no waiting.
/// </summary>
public sealed class ConnectionQualityMonitor(Func<long> nowMs, QualityThresholds? thresholds = null)
{
    private readonly object _gate = new();
    private readonly QualityThresholds _t = thresholds ?? new QualityThresholds();
    private readonly List<(long At, double Ms)> _roundTrips = [];
    private readonly List<long> _frames = [];
    private readonly List<long> _stalls = [];
    private long _lastSignal = -1;
    private bool _reconnecting;
    private QualityLevel _shown = QualityLevel.Unknown;
    private QualityReason _shownReason = QualityReason.NoData;
    private QualityLevel _candidate = QualityLevel.Unknown;
    private long _candidateSince;

    public void RecordRoundTrip(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || milliseconds < 0) return;
        lock (_gate)
        {
            var now = nowMs();
            _roundTrips.Add((now, milliseconds));
            _lastSignal = now;
            _reconnecting = false;
        }
    }

    public void RecordFrame()
    {
        lock (_gate)
        {
            var now = nowMs();
            _frames.Add(now);
            _lastSignal = now;
            _reconnecting = false;
        }
    }

    /// <summary>A frame was due and did not arrive in time.</summary>
    public void RecordStall()
    {
        lock (_gate) _stalls.Add(nowMs());
    }

    public void SetReconnecting(bool reconnecting)
    {
        lock (_gate) _reconnecting = reconnecting;
    }

    public QualitySnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = nowMs();
            var from = now - _t.WindowMs;
            _roundTrips.RemoveAll(s => s.At < from);
            _frames.RemoveAll(f => f < from);
            _stalls.RemoveAll(s => s < from);

            double? rtt = _roundTrips.Count > 0 ? Median(_roundTrips.Select(s => s.Ms).ToList()) : null;
            double? jitter = _roundTrips.Count >= 2 ? Jitter(_roundTrips.Select(s => s.Ms).ToList()) : null;
            double? fps = _frames.Count >= 2 ? (_frames.Count - 1) * 1000.0 / Math.Max(1, _frames[^1] - _frames[0]) : null;

            var (raw, reason) = Evaluate(now, rtt, jitter);
            var level = Smooth(raw, reason, now);
            return new QualitySnapshot(level, _shownReason, rtt, jitter, fps, _stalls.Count);
        }
    }

    private (QualityLevel Level, QualityReason Reason) Evaluate(long now, double? rtt, double? jitter)
    {
        if (_reconnecting) return (QualityLevel.Lost, QualityReason.Reconnecting);
        if (_lastSignal < 0) return (QualityLevel.Unknown, QualityReason.NoData);
        if (now - _lastSignal >= _t.NoReplyMs) return (QualityLevel.Lost, QualityReason.NoReply);

        var level = QualityLevel.Good;
        var reason = QualityReason.Healthy;
        void Worse(QualityLevel l, QualityReason r)
        {
            if (l > level) { level = l; reason = r; }
        }

        if (rtt is { } r1)
        {
            if (r1 > _t.FairRoundTripMs) Worse(QualityLevel.Poor, QualityReason.SlowRoundTrip);
            else if (r1 > _t.GoodRoundTripMs) Worse(QualityLevel.Fair, QualityReason.SlowRoundTrip);
        }
        if (jitter is { } j)
        {
            if (j > _t.FairJitterMs) Worse(QualityLevel.Poor, QualityReason.UnstableRoundTrip);
            else if (j > _t.GoodJitterMs) Worse(QualityLevel.Fair, QualityReason.UnstableRoundTrip);
        }
        if (_stalls.Count >= _t.PoorStalls) Worse(QualityLevel.Poor, QualityReason.FramesStalled);
        else if (_stalls.Count > 0) Worse(QualityLevel.Fair, QualityReason.FramesStalled);
        return (level, reason);
    }

    private QualityLevel Smooth(QualityLevel raw, QualityReason reason, long now)
    {
        // Unknown has no "better": the first real level is shown at once. Lost and worse levels are shown at once; an improvement waits.
        if (_shown == QualityLevel.Unknown || raw == QualityLevel.Unknown || raw >= _shown)
        {
            _shown = raw;
            _shownReason = reason;
            _candidate = raw;
            _candidateSince = now;
            return _shown;
        }
        if (raw != _candidate) { _candidate = raw; _candidateSince = now; }
        if (now - _candidateSince >= _t.ImproveAfterMs) { _shown = raw; _shownReason = reason; }
        return _shown;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    /// <summary>Mean absolute difference between neighbouring round trips (the same idea as RFC 3550 jitter, without the smoothing).</summary>
    private static double Jitter(List<double> values)
    {
        var sum = 0.0;
        for (var i = 1; i < values.Count; i++) sum += Math.Abs(values[i] - values[i - 1]);
        return sum / (values.Count - 1);
    }
}
