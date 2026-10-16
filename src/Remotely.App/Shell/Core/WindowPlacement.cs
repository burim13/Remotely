namespace Remotely.App.Shell;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>The overlap with another rectangle; Width and Height are 0 when they do not touch.</summary>
    public PixelRect Intersect(PixelRect o)
    {
        var x = Math.Max(X, o.X);
        var y = Math.Max(Y, o.Y);
        var r = Math.Min(Right, o.Right);
        var b = Math.Min(Bottom, o.Bottom);
        return r > x && b > y ? new PixelRect(x, y, r - x, b - y) : new PixelRect(x, y, 0, 0);
    }
}

public sealed record WindowBounds(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>
/// Decides where the main window opens. A remembered position is kept only while its title strip is still reachable on a connected monitor
/// (a removed monitor or a changed resolution must never leave the window where nobody can grab it); otherwise it is centred on the first work area.
/// All values are physical pixels.
/// </summary>
public static class WindowPlacement
{
    public const int TitleStripHeight = 32;
    public const int MinVisibleWidth = 120;

    /// <param name="workAreas">Monitor work areas, the primary monitor first.</param>
    public static WindowBounds Fit(WindowBounds? saved, IReadOnlyList<PixelRect> workAreas, int defaultWidth, int defaultHeight, int minWidth, int minHeight)
    {
        if (workAreas.Count == 0) return saved ?? new WindowBounds(0, 0, defaultWidth, defaultHeight, false);
        var primary = workAreas[0];
        if (saved is null || saved.Width <= 0 || saved.Height <= 0) return Centered(primary, defaultWidth, defaultHeight, false);

        var width = Math.Max(saved.Width, minWidth);
        var height = Math.Max(saved.Height, minHeight);
        var strip = new PixelRect(saved.X, saved.Y, width, Math.Min(height, TitleStripHeight));
        var best = workAreas.Select(a => (Area: a, Overlap: strip.Intersect(a))).OrderByDescending(t => (long)t.Overlap.Width * t.Overlap.Height).First();
        var visible = best.Overlap.Width >= Math.Min(MinVisibleWidth, width) && best.Overlap.Height >= strip.Height;
        if (!visible) return Centered(primary, width, height, saved.Maximized);

        var area = best.Area;
        if (width <= area.Width && height <= area.Height) return new WindowBounds(saved.X, saved.Y, width, height, saved.Maximized);
        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        var x = Math.Clamp(saved.X, area.X, area.Right - width);
        var y = Math.Clamp(saved.Y, area.Y, area.Bottom - height);
        return new WindowBounds(x, y, width, height, saved.Maximized);
    }

    private static WindowBounds Centered(PixelRect area, int width, int height, bool maximized)
    {
        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        return new WindowBounds(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height, maximized);
    }
}
