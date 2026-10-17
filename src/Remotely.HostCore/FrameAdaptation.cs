namespace Remotely.HostCore;

/// <summary>
/// Chooses the JPEG quality and the pause between frame requests from what the viewer measures: how long a request took, how many bytes came back
/// and how long the host says it spent capturing and encoding. Pure logic with no clock of its own, so it is testable.
/// </summary>
public sealed class FrameAdaptation
{
    public const int MinQuality = 25, MaxQuality = 85;
    private const double Alpha = 0.3;
    private const double TargetTransferMs = 50;   // time the pixels may spend on the wire per frame while keeping about 30 fps
    private const int Cooldown = 4;                // frame replies between two quality changes

    private int _sinceChange;
    private int _announced;
    private double _bytesPerFrame;

    public FrameAdaptation(int startQuality = 65)
    {
        Quality = _announced = Math.Clamp(startQuality, MinQuality, MaxQuality);
    }

    public int Quality { get; private set; }
    /// <summary>Round trip of a request that carried (almost) no pixels: the network's latency, not the host's work.</summary>
    public double RoundTripMs { get; private set; }
    /// <summary>Measured transfer speed of frames in bytes per second; 0 until a frame big enough to measure has arrived.</summary>
    public double BandwidthBytesPerSecond { get; private set; }
    public double BytesPerFrame => _bytesPerFrame;
    /// <summary>Time the viewer should leave between two requests while the screen is changing.</summary>
    public TimeSpan ChangedInterval { get; private set; } = TimeSpan.FromMilliseconds(33);
    /// <summary>The same when the host answered "unchanged".</summary>
    public TimeSpan IdleInterval { get; } = TimeSpan.FromMilliseconds(50);

    /// <param name="bytes">Size of the reply message (0 for "unchanged").</param>
    /// <param name="roundTrip">Request sent until reply received.</param>
    /// <param name="hostWork">The host's capture plus encode time from the frame header (zero if unknown).</param>
    public void OnReply(int bytes, TimeSpan roundTrip, TimeSpan hostWork)
    {
        var network = Math.Max(0.1, roundTrip.TotalMilliseconds - hostWork.TotalMilliseconds);
        if (bytes < 2048)
        {
            RoundTripMs = RoundTripMs == 0 ? network : RoundTripMs * (1 - Alpha) + Math.Min(network, RoundTripMs * 4) * Alpha;
            if (bytes > 0) _sinceChange++;
            return;
        }
        var transfer = Math.Max(1, network - RoundTripMs);
        var sample = bytes / (transfer / 1000.0);
        BandwidthBytesPerSecond = BandwidthBytesPerSecond == 0 ? sample : BandwidthBytesPerSecond * (1 - Alpha) + sample * Alpha;
        _bytesPerFrame = _bytesPerFrame == 0 ? bytes : _bytesPerFrame * (1 - Alpha) + bytes * Alpha;
        _sinceChange++;
        if (_sinceChange < Cooldown) return;

        var estimatedMs = _bytesPerFrame / BandwidthBytesPerSecond * 1000;
        if (estimatedMs > TargetTransferMs * 1.5 && Quality > MinQuality) { Quality = Math.Max(MinQuality, Quality - 10); _sinceChange = 0; }
        else if (estimatedMs < TargetTransferMs * 0.4 && Quality < MaxQuality) { Quality = Math.Min(MaxQuality, Quality + 5); _sinceChange = 0; }

        // Already at the lowest quality and still too slow: send fewer frames instead of queueing them.
        ChangedInterval = Quality == MinQuality && estimatedMs > TargetTransferMs * 2
            ? TimeSpan.FromMilliseconds(Math.Min(250, estimatedMs))
            : TimeSpan.FromMilliseconds(33);
    }

    /// <summary>True when the quality moved far enough from what the host was last told to be worth a message.</summary>
    public bool TryTakeQualityChange(out int quality)
    {
        quality = Quality;
        if (Math.Abs(Quality - _announced) < 5) return false;
        _announced = Quality;
        return true;
    }
}
