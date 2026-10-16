using System.Globalization;
using Remotely.UI.Localization;
using Remotely.ViewerCore;

namespace Remotely.App;

/// <summary>The text of the session stats overlay, shared by the session window and the preview page.</summary>
internal static class SessionStatsText
{
    public static string Format(StatsSnapshot s, int frameWidth, int frameHeight)
    {
        var culture = CultureInfo.CurrentCulture;
        var latency = s.RoundTrip is { } rt ? $"{rt.TotalMilliseconds.ToString("0", culture)} ms" : Loc.Get("Session_Stats_NotMeasured");
        return string.Join('\n',
            string.Format(Loc.Get("Session_Stats_Fps"), s.FramesPerSecond.ToString("0.0", culture)),
            string.Format(Loc.Get("Session_Stats_Bandwidth"), FormatRate(s.BytesPerSecond, culture)),
            string.Format(Loc.Get("Session_Stats_Latency"), latency),
            string.Format(Loc.Get("Session_Stats_Codec"), s.Codec),
            string.Format(Loc.Get("Session_Stats_Resolution"), frameWidth, frameHeight));
    }

    private static string FormatRate(double bytesPerSecond, CultureInfo culture) =>
        bytesPerSecond < 1024 * 1024
            ? $"{(bytesPerSecond / 1024).ToString("0", culture)} KB/s"
            : $"{(bytesPerSecond / (1024 * 1024)).ToString("0.0", culture)} MB/s";
}
