using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Remotely.HostCore;
using Remotely.ViewerCore;
using Remotely.ViewerCore.Mapping;

namespace Remotely.Tests;

public class ScreenMappingTests
{
    private static readonly PixelRect Left = new(-1920, 0, 1920, 1080);           // left of the primary monitor: negative X
    private static readonly PixelRect Primary = new(0, 0, 2560, 1440);
    private static readonly PixelRect Above = new(0, -1080, 1920, 1080);         // above the primary monitor: negative Y
    private static readonly PixelRect Desktop = PixelRect.Union([Left, Primary, Above]);

    private static (double X, double Y) HostPixel(ushort nx, ushort ny, PixelRect desktop) =>
        (desktop.X + nx / 65535.0 * desktop.Width, desktop.Y + ny / 65535.0 * desktop.Height);

    [Fact]
    public void Union_covers_negative_coordinates_and_ignores_empty_rects()
    {
        Assert.Equal(new PixelRect(-1920, -1080, 4480, 2520), Desktop);
        Assert.Equal(default, PixelRect.Union([]));
        Assert.Equal(Primary, PixelRect.Union([default, Primary]));
    }

    [Fact]
    public void Fit_letterboxes_a_square_picture_in_a_wide_window()
    {
        var rect = ScreenMapping.ImageRect(ScaleMode.Fit, 1000, 500, 1000, 1000, 1, 1, default);
        Assert.Equal(new RectD(250, 0, 500, 500), rect);

        Assert.True(ScreenMapping.TryMapToDesktop(new PointD(250, 250), rect, null, null, false, out var x, out var y));
        Assert.Equal((0, 32768), (x, y));
        Assert.True(ScreenMapping.TryMapToDesktop(new PointD(750, 500), rect, null, null, false, out x, out y));
        Assert.Equal((65535, 65535), (x, y));
    }

    [Fact]
    public void A_position_in_the_bars_is_refused_for_clicks_and_clamped_for_drags()
    {
        var rect = ScreenMapping.ImageRect(ScaleMode.Fit, 1000, 500, 1000, 1000, 1, 1, default);
        Assert.False(ScreenMapping.TryMapToDesktop(new PointD(100, 250), rect, null, null, false, out _, out _));
        Assert.True(ScreenMapping.TryMapToDesktop(new PointD(100, 250), rect, null, null, true, out var x, out var y));
        Assert.Equal((0, 32768), (x, y));
        Assert.False(ScreenMapping.TryMapToDesktop(new PointD(1, 1), default, null, null, true, out _, out _));
    }

    [Fact]
    public void Fill_covers_the_window_and_crops_the_overflow()
    {
        var rect = ScreenMapping.ImageRect(ScaleMode.Fill, 1000, 500, 1000, 1000, 1, 1, default);
        Assert.Equal(new RectD(0, -250, 1000, 1000), rect);
        // Panning down by 100 shows the part further down; the picture stays covering the window, and cannot be panned past its edge.
        Assert.Equal(-350, ScreenMapping.ImageRect(ScaleMode.Fill, 1000, 500, 1000, 1000, 1, 1, new PointD(0, -100)).Y);
        Assert.Equal(-500, ScreenMapping.ImageRect(ScaleMode.Fill, 1000, 500, 1000, 1000, 1, 1, new PointD(0, -9999)).Y);
        Assert.Equal(0, ScreenMapping.ImageRect(ScaleMode.Fill, 1000, 500, 1000, 1000, 1, 1, new PointD(0, 9999)).Y);
    }

    [Fact]
    public void Pan_is_ignored_when_the_picture_is_smaller_than_the_window()
    {
        var rect = ScreenMapping.ImageRect(ScaleMode.Fit, 1000, 500, 1000, 1000, 1, 1, new PointD(300, 300));
        Assert.Equal(new RectD(250, 0, 500, 500), rect);
    }

    [Theory]
    [InlineData(1.0, 1920, 1080)]
    [InlineData(1.25, 1536, 864)]
    [InlineData(1.5, 1280, 720)]
    [InlineData(2.0, 960, 540)]
    public void At_100_percent_one_remote_pixel_is_one_local_physical_pixel(double rasterScale, double expectedWidth, double expectedHeight)
    {
        var rect = ScreenMapping.ImageRect(ScaleMode.Actual, 4000, 3000, 1920, 1080, rasterScale, 1, default);
        Assert.Equal(expectedWidth, rect.Width, 6);
        Assert.Equal(expectedHeight, rect.Height, 6);
    }

    [Fact]
    public void Pointer_mapping_is_the_same_whatever_the_local_scale()
    {
        // The picture is 1920x1080. With the window showing it at 100% on a 150% display, the window's centre is still the picture's centre.
        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            var rect = ScreenMapping.ImageRect(ScaleMode.Actual, 3000 / scale, 2000 / scale, 1920, 1080, scale, 1, default);
            Assert.True(ScreenMapping.TryMapToDesktop(new PointD(1500 / scale, 1000 / scale), rect, null, null, false, out var x, out var y));
            Assert.Equal((32768, 32768), (x, y));
        }
    }

    [Fact]
    public void Panning_at_100_percent_is_clamped_to_the_picture()
    {
        // 3000x2000 picture on a 150% display = 2000x1333.33 DIPs, window 1000x800.
        var rect = ScreenMapping.ImageRect(ScaleMode.Actual, 1000, 800, 3000, 2000, 1.5, 1, new PointD(-99999, 99999));
        Assert.Equal(-1000, rect.X, 6);                       // right edge of the picture flush with the window's right edge
        Assert.Equal(0, rect.Y, 6);                           // bottom pan request clamped to the top edge
        Assert.Equal(1333.3333333333, rect.Bottom, 6);
    }

    [Fact]
    public void Zoom_steps_by_ten_percent_per_notch_and_is_bounded()
    {
        Assert.Equal(1.1, ScreenMapping.ZoomStep(1, 120), 9);
        Assert.Equal(1 / 1.1, ScreenMapping.ZoomStep(1, -120), 9);
        Assert.Equal(ScreenMapping.MaxZoom, ScreenMapping.ZoomStep(3.9, 120 * 20));
        Assert.Equal(ScreenMapping.MinZoom, ScreenMapping.ZoomStep(0.3, -120 * 20));
    }

    [Fact]
    public void Zooming_keeps_the_remote_pixel_under_the_pointer()
    {
        var anchor = new PointD(700, 300);
        var before = ScreenMapping.ImageRect(ScaleMode.Actual, 1000, 800, 3000, 2000, 1, 1, new PointD(-200, 100));
        Assert.True(ScreenMapping.TryMapToDesktop(anchor, before, null, null, false, out var x0, out var y0));

        var (zoom, pan) = ScreenMapping.ZoomAt(1.5, anchor, 1000, 800, 3000, 2000, 1, 1, new PointD(-200, 100));
        Assert.Equal(1.5, zoom);
        var after = ScreenMapping.ImageRect(ScaleMode.Actual, 1000, 800, 3000, 2000, 1, zoom, pan);
        Assert.True(ScreenMapping.TryMapToDesktop(anchor, after, null, null, false, out var x1, out var y1));
        Assert.InRange(Math.Abs(x1 - x0), 0, 1);
        Assert.InRange(Math.Abs(y1 - y0), 0, 1);
    }

    [Fact]
    public void A_monitor_left_of_the_primary_one_maps_into_the_virtual_desktop_with_negative_origin()
    {
        var rect = new RectD(0, 0, 960, 540);                       // 1920x1080 picture at half size
        Assert.True(ScreenMapping.TryMapToDesktop(new PointD(480, 270), rect, Left, Desktop, false, out var x, out var y));
        Assert.Equal((ushort)Math.Round((-960 - Desktop.X) / (double)Desktop.Width * 65535), x);
        Assert.Equal((ushort)Math.Round((540 - Desktop.Y) / (double)Desktop.Height * 65535), y);

        var (hx, hy) = HostPixel(x, y, Desktop);
        Assert.InRange(hx, -960 - 1, -960 + 1);
        Assert.InRange(hy, 540 - 1, 540 + 1);
    }

    [Fact]
    public void A_monitor_above_the_primary_one_has_negative_y()
    {
        var rect = new RectD(0, 0, 960, 540);
        Assert.True(ScreenMapping.TryMapToDesktop(new PointD(0, 0), rect, Above, Desktop, false, out var x, out var y));
        var (hx, hy) = HostPixel(x, y, Desktop);
        Assert.InRange(hx, 0, 1);
        Assert.InRange(hy, -1080, -1079);
    }

    [Fact]
    public void A_pointer_pushed_against_a_monitor_edge_stays_on_that_monitor()
    {
        var rect = new RectD(0, 0, 960, 540);
        foreach (var monitor in new[] { Left, Primary, Above })
        {
            foreach (var corner in new[] { new PointD(0, 0), new PointD(960, 0), new PointD(0, 540), new PointD(960, 540) })
            {
                Assert.True(ScreenMapping.TryMapToDesktop(corner, rect, monitor, Desktop, false, out var x, out var y));
                var (hx, hy) = HostPixel(x, y, Desktop);
                // Windows takes the pixel the position falls in; allow the rounding of one normalised step (about 0.07 px here).
                Assert.True(hx >= monitor.X - 0.1 && hx < monitor.Right + 0.1, $"x {hx} outside {monitor}");
                Assert.True(hy >= monitor.Y - 0.1 && hy < monitor.Bottom + 0.1, $"y {hy} outside {monitor}");
            }
        }
    }

    [Fact]
    public void Mapping_to_the_host_and_back_returns_the_same_view_position()
    {
        var rect = ScreenMapping.ImageRect(ScaleMode.Fit, 1280, 720, 2560, 1440, 1.25, 1, default);
        foreach (var monitor in new PixelRect?[] { null, Left, Primary, Above })
        {
            var point = new PointD(rect.X + rect.Width * 0.3, rect.Y + rect.Height * 0.7);
            Assert.True(ScreenMapping.TryMapToDesktop(point, rect, monitor, monitor is null ? null : Desktop, false, out var x, out var y));
            Assert.True(ScreenMapping.TryMapFromDesktop(x, y, rect, monitor, monitor is null ? null : Desktop, out var back));
            Assert.InRange(Math.Abs(back.X - point.X), 0, 0.1);
            Assert.InRange(Math.Abs(back.Y - point.Y), 0, 0.1);
        }
    }

    [Fact]
    public void A_host_position_on_another_monitor_is_not_drawn_in_this_view()
    {
        var rect = new RectD(0, 0, 960, 540);
        var onPrimary = (ushort)Math.Round((1000 - Desktop.X) / (double)Desktop.Width * 65535);
        var y = (ushort)Math.Round((500 - Desktop.Y) / (double)Desktop.Height * 65535);
        Assert.False(ScreenMapping.TryMapFromDesktop(onPrimary, y, rect, Left, Desktop, out _));
        Assert.True(ScreenMapping.TryMapFromDesktop(onPrimary, y, rect, Primary, Desktop, out _));
    }

    [Fact]
    public void Reconnect_retries_on_its_own_only_after_an_error_and_then_gives_up()
    {
        Assert.True(ReconnectSchedule.ShouldAutoRetry("The host stopped responding."));
        Assert.False(ReconnectSchedule.ShouldAutoRetry(null));
        Assert.False(ReconnectSchedule.ShouldAutoRetry(""));

        var delays = Enumerable.Range(0, ReconnectSchedule.MaxAttempts).Select(i => ReconnectSchedule.DelayBefore(i)!.Value).ToArray();
        Assert.All(delays, d => Assert.InRange(d, 1, 60));
        Assert.Equal(delays.OrderBy(d => d), delays);                  // never retries faster as time goes on
        Assert.Null(ReconnectSchedule.DelayBefore(ReconnectSchedule.MaxAttempts));
        Assert.Null(ReconnectSchedule.DelayBefore(-1));

        var due = new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc);
        Assert.Equal(10, ReconnectSchedule.SecondsLeft(due, due.AddSeconds(-10)));
        Assert.Equal(1, ReconnectSchedule.SecondsLeft(due, due.AddSeconds(-0.2)));
        Assert.Equal(0, ReconnectSchedule.SecondsLeft(due, due.AddSeconds(5)));
    }

    [Fact]
    public void Stats_count_frames_and_bytes_over_a_sliding_window()
    {
        long now = 1_000_000;
        var stats = new SessionStats(() => now);
        Assert.Equal(0, stats.Snapshot().FramesPerSecond);
        Assert.Null(stats.Snapshot().SinceLastFrame);

        for (var i = 0; i < 30; i++)
        {
            now += 100;                                    // 10 frames per second for 3 seconds
            stats.RecordFrame(10_000);
        }
        var s = stats.Snapshot();
        Assert.Equal(10, s.FramesPerSecond, 0);
        Assert.InRange(s.BytesPerSecond, 95_000, 105_000);
        Assert.Equal((30L, 300_000L), (s.TotalFrames, s.TotalBytes));
        Assert.Equal(TimeSpan.Zero, s.SinceLastFrame);
        Assert.Null(s.RoundTrip);
        Assert.Equal("JPEG", s.Codec);

        now += 5000;                                       // stalled: the rate decays to zero, the totals stay
        s = stats.Snapshot();
        Assert.Equal(0, s.FramesPerSecond);
        Assert.Equal(30, s.TotalFrames);
        Assert.Equal(TimeSpan.FromSeconds(5), s.SinceLastFrame);

        stats.ReportRoundTrip(TimeSpan.FromMilliseconds(42));
        Assert.Equal(TimeSpan.FromMilliseconds(42), stats.Snapshot().RoundTrip);
    }

    [Fact]
    public async Task A_live_session_exposes_non_zero_stats_once_frames_arrive()
    {
        var screen = new HostCoreTests.FakeScreen(() => new FrameInfo(800, 600, "Default", Encoding.ASCII.GetBytes("jpeg-" + Environment.TickCount64)));
        await using var rig = await HostCoreTests.Rig.StartAsync(v => new HostServices(screen) { IsTrusted = id => id == v.DeviceId });
        await using var session = await rig.OpenAsync();
        var stats = session.Stats;
        Assert.Same(stats, session.Stats);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (stats.Snapshot().TotalFrames < 3 && DateTime.UtcNow < deadline) await Task.Delay(25);

        var s = stats.Snapshot();
        Assert.True(s.TotalFrames >= 3, "frames counted: " + s.TotalFrames);
        Assert.True(s.TotalBytes > 0);
        Assert.True(s.FramesPerSecond > 0);
        Assert.True(s.BytesPerSecond > 0);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static Dictionary<string, string> Strings(string language) =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "Remotely.App", "Strings", language, "Resources.resw")).Root!
            .Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    [Fact]
    public void Every_session_string_key_used_by_the_window_exists_in_English_and_Albanian_with_the_same_placeholders()
    {
        var folder = Path.Combine(RepoRoot(), "src", "Remotely.App", "Session");
        var used = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(Session_[A-Za-z_]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet();
        Assert.NotEmpty(used);

        var en = Strings("en-US");
        var sq = Strings("sq-AL");
        foreach (var key in used)
        {
            Assert.True(en.ContainsKey(key), "missing in en-US: " + key);
            Assert.True(sq.ContainsKey(key), "missing in sq-AL: " + key);
            var placeholders = (string s) => Regex.Matches(s, "\\{\\d+\\}").Select(m => m.Value).Order().ToArray();
            Assert.Equal(placeholders(en[key]), placeholders(sq[key]));
        }
    }
}
