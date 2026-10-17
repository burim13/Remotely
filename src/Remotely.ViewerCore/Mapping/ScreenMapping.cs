namespace Remotely.ViewerCore.Mapping;

public readonly record struct PointD(double X, double Y);

/// <summary>A rectangle in device-independent pixels of the viewing control.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>A rectangle in pixels of the host's virtual desktop. X and Y are negative for monitors left of or above the primary monitor.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect Union(IEnumerable<PixelRect> rects)
    {
        var any = false;
        int left = 0, top = 0, right = 0, bottom = 0;
        foreach (var r in rects)
        {
            if (r.IsEmpty) continue;
            if (!any) { (left, top, right, bottom) = (r.X, r.Y, r.Right, r.Bottom); any = true; continue; }
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }
        return any ? new PixelRect(left, top, right - left, bottom - top) : default;
    }
}

public enum ScaleMode
{
    /// <summary>Whole remote screen visible, aspect ratio kept, bars where it does not fill the window.</summary>
    Fit,
    /// <summary>One remote pixel is one local screen pixel (times the zoom factor).</summary>
    Actual,
    /// <summary>Remote screen covers the whole window, aspect ratio kept, the overflow is cropped and can be panned to.</summary>
    Fill,
}

/// <summary>
/// Pure geometry for the remote screen view: how the remote picture sits inside the window, and how a local pointer position becomes the
/// 0..65535 position the host expects (normalised across the host's whole virtual desktop, see <c>InputEvent</c>).
/// No UI types, so it is unit-tested on any OS.
/// </summary>
public static class ScreenMapping
{
    public const double MinZoom = 0.25;
    public const double MaxZoom = 4.0;
    private const double Epsilon = 0.001;

    public static double ClampZoom(double zoom) => double.IsNaN(zoom) ? 1 : Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>Local DIPs per remote pixel. <paramref name="rasterScale"/> is the local display scale (1.5 for 150%); at 100% one remote pixel covers one physical local pixel.</summary>
    public static double Scale(ScaleMode mode, double viewWidth, double viewHeight, int sourceWidth, int sourceHeight, double rasterScale, double zoom)
    {
        if (viewWidth <= 0 || viewHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0) return 0;
        return mode switch
        {
            ScaleMode.Fit => Math.Min(viewWidth / sourceWidth, viewHeight / sourceHeight),
            ScaleMode.Fill => Math.Max(viewWidth / sourceWidth, viewHeight / sourceHeight),
            _ => ClampZoom(zoom) / (rasterScale > 0 ? rasterScale : 1),
        };
    }

    /// <summary>Where the remote picture is drawn. <paramref name="pan"/> shifts a picture larger than the view; it is clamped so the picture always covers the view, and ignored (centred) when the picture is smaller.</summary>
    public static RectD ImageRect(ScaleMode mode, double viewWidth, double viewHeight, int sourceWidth, int sourceHeight, double rasterScale, double zoom, PointD pan)
    {
        var scale = Scale(mode, viewWidth, viewHeight, sourceWidth, sourceHeight, rasterScale, zoom);
        if (scale <= 0) return default;
        var w = sourceWidth * scale;
        var h = sourceHeight * scale;
        return new RectD((viewWidth - w) / 2 + ClampAxis(pan.X, viewWidth, w), (viewHeight - h) / 2 + ClampAxis(pan.Y, viewHeight, h), w, h);
    }

    public static PointD ClampPan(PointD pan, double viewWidth, double viewHeight, double imageWidth, double imageHeight) =>
        new(ClampAxis(pan.X, viewWidth, imageWidth), ClampAxis(pan.Y, viewHeight, imageHeight));

    private static double ClampAxis(double pan, double view, double size)
    {
        var overflow = size - view;
        if (overflow <= Epsilon) return 0;
        var half = overflow / 2;
        return Math.Clamp(pan, -half, half);
    }

    /// <summary>One mouse-wheel notch is a delta of 120; each notch changes the zoom by 10%.</summary>
    public static double ZoomStep(double zoom, int wheelDelta) => ClampZoom(ClampZoom(zoom) * Math.Pow(1.1, wheelDelta / 120.0));

    /// <summary>New zoom and pan (100% mode) that keep the remote pixel under <paramref name="anchor"/> under it.</summary>
    public static (double Zoom, PointD Pan) ZoomAt(double newZoom, PointD anchor, double viewWidth, double viewHeight, int sourceWidth, int sourceHeight, double rasterScale, double oldZoom, PointD oldPan)
    {
        newZoom = ClampZoom(newZoom);
        var before = ImageRect(ScaleMode.Actual, viewWidth, viewHeight, sourceWidth, sourceHeight, rasterScale, oldZoom, oldPan);
        if (before.Width <= 0) return (newZoom, default);
        var u = (anchor.X - before.X) / before.Width;
        var v = (anchor.Y - before.Y) / before.Height;
        var scale = Scale(ScaleMode.Actual, viewWidth, viewHeight, sourceWidth, sourceHeight, rasterScale, newZoom);
        var w = sourceWidth * scale;
        var h = sourceHeight * scale;
        var pan = new PointD(anchor.X - u * w - (viewWidth - w) / 2, anchor.Y - v * h - (viewHeight - h) / 2);
        return (newZoom, ClampPan(pan, viewWidth, viewHeight, w, h));
    }

    /// <summary>
    /// Local position to the host's normalised position. <paramref name="monitor"/> is the part of the host desktop shown (null or empty: the whole
    /// picture is the whole desktop); <paramref name="desktop"/> is the host's virtual desktop (null or empty: same as the monitor).
    /// With <paramref name="clampOutside"/> false, a position in the bars outside the picture is refused. For a single monitor the result is kept
    /// half a pixel inside its edges so a pointer pushed against an edge cannot land on the neighbouring monitor.
    /// </summary>
    public static bool TryMapToDesktop(PointD view, RectD image, PixelRect? monitor, PixelRect? desktop, bool clampOutside, out ushort x, out ushort y)
    {
        x = y = 0;
        if (image.Width <= 0 || image.Height <= 0) return false;
        var u = (view.X - image.X) / image.Width;
        var v = (view.Y - image.Y) / image.Height;
        if (!clampOutside && (u < 0 || u > 1 || v < 0 || v > 1)) return false;
        u = Math.Clamp(u, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double fx = u, fy = v;
        if (monitor is { IsEmpty: false } m)
        {
            var d = desktop is { IsEmpty: false } dd ? dd : m;
            var hx = Math.Clamp(m.X + u * m.Width, m.X + 0.5, m.Right - 0.5);
            var hy = Math.Clamp(m.Y + v * m.Height, m.Y + 0.5, m.Bottom - 0.5);
            fx = Math.Clamp((hx - d.X) / d.Width, 0, 1);
            fy = Math.Clamp((hy - d.Y) / d.Height, 0, 1);
        }
        x = (ushort)Math.Round(fx * 65535);
        y = (ushort)Math.Round(fy * 65535);
        return true;
    }

    /// <summary>The inverse: where a host position (normalised, as the host reports a cursor) falls in the view. False when it is outside the monitor shown.</summary>
    public static bool TryMapFromDesktop(ushort x, ushort y, RectD image, PixelRect? monitor, PixelRect? desktop, out PointD view)
    {
        view = default;
        if (image.Width <= 0 || image.Height <= 0) return false;
        double u = x / 65535.0, v = y / 65535.0;
        if (monitor is { IsEmpty: false } m)
        {
            var d = desktop is { IsEmpty: false } dd ? dd : m;
            var hx = d.X + u * d.Width;
            var hy = d.Y + v * d.Height;
            if (hx < m.X || hx > m.Right || hy < m.Y || hy > m.Bottom) return false;
            u = (hx - m.X) / m.Width;
            v = (hy - m.Y) / m.Height;
        }
        view = new PointD(image.X + u * image.Width, image.Y + v * image.Height);
        return true;
    }

    /// <summary>
    /// Where a host position in virtual-desktop pixels (the cursor, as the host reports it) falls in the view. <paramref name="shown"/> is the area
    /// of the host desktop the picture covers (the selected monitor, or the whole virtual desktop). False when the position is outside it.
    /// </summary>
    public static bool TryMapFromDesktopPixel(double hostX, double hostY, RectD image, PixelRect shown, out PointD view)
    {
        view = default;
        if (image.Width <= 0 || image.Height <= 0 || shown.IsEmpty) return false;
        var u = (hostX - shown.X) / shown.Width;
        var v = (hostY - shown.Y) / shown.Height;
        if (u < 0 || u > 1 || v < 0 || v > 1) return false;
        view = new PointD(image.X + u * image.Width, image.Y + v * image.Height);
        return true;
    }
}
