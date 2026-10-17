using System.Globalization;
using Remotely.UI.Localization;
using Remotely.ViewerCore;

namespace Remotely.App;

/// <summary>The text of the session stats overlay, shared by the session window and the preview page.</summary>
internal static class SessionStatsText
{
    /// <param name="received">Frames and bytes counted on this side (<c>ViewerSession.ReceivedStats</c>).</param>
    /// <param name="stream">The streaming engine's own measurements (<c>ViewerSession.Stats</c>); null when there are none. Its round trip is 0 until the tile stream has measured one.</param>
    public static string Format(StatsSnapshot received, StreamStats? stream, int frameWidth, int frameHeight)
    {
        var culture = CultureInfo.CurrentCulture;
        var tiled = stream is { Tiled: true };
        var latency = stream is { RoundTripMs: > 0 } s ? $"{s.RoundTripMs.ToString("0", culture)} ms" : Loc.Get("Session_Stats_NotMeasured");
        var lines = new List<string>
        {
            string.Format(Loc.Get("Session_Stats_Fps"), received.FramesPerSecond.ToString("0.0", culture)),
            string.Format(Loc.Get("Session_Stats_Bandwidth"), FormatRate(received.BytesPerSecond, culture)),
            string.Format(Loc.Get("Session_Stats_Latency"), latency),
            string.Format(Loc.Get("Session_Stats_Codec"), tiled ? Loc.Get("Session_Stats_CodecTiles") : received.Codec),
            string.Format(Loc.Get("Session_Stats_Resolution"), frameWidth, frameHeight),
        };
        if (tiled && stream is not null)
        {
            lines.Add(string.Format(Loc.Get("Session_Stats_Quality"), stream.Quality));
            lines.Add(string.Format(Loc.Get("Session_Stats_HostTime"), stream.HostCaptureMs.ToString("0.0", culture), stream.HostEncodeMs.ToString("0.0", culture)));
        }
        return string.Join('\n', lines);
    }

    private static string FormatRate(double bytesPerSecond, CultureInfo culture) =>
        bytesPerSecond < 1024 * 1024
            ? $"{(bytesPerSecond / 1024).ToString("0", culture)} KB/s"
            : $"{(bytesPerSecond / (1024 * 1024)).ToString("0.0", culture)} MB/s";
}
