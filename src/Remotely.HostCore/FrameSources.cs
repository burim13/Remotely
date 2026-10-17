namespace Remotely.HostCore;

/// <summary>
/// A frame source that can also answer "what changed for this viewer since its last frame" and list the displays. Sources that do not implement
/// it are served as full-frame JPEG to every viewer (protocol version 1 behaviour).
/// </summary>
public interface ITiledFrameSource : IFrameSource
{
    /// <summary>
    /// Captures for one viewer (<see cref="TileRequest.SessionId"/>). <see cref="TileRequest.MonitorsOnly"/> returns just the display list.
    /// The returned <see cref="TileCapture.Frame"/> is null when the viewer already has the current picture.
    /// </summary>
    Task<TileCapture> CaptureTilesAsync(TileRequest request, CancellationToken ct);
}

public static class FrameSessions
{
    private static int _next;

    /// <summary>A number no other viewer session in this process has, for <see cref="TileRequest.SessionId"/>.</summary>
    public static int NewId() => Interlocked.Increment(ref _next);
}
