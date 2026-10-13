namespace Remotely.HostCore;

public interface IFrameSource
{
    /// <summary>Captures the screen the user would currently see. Throws if no capture is possible right now.</summary>
    Task<FrameInfo> CaptureAsync(CancellationToken ct);
}
