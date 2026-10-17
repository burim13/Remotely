using System.Drawing;
using System.Drawing.Imaging;
using Remotely.UITests.Imaging;

namespace Remotely.UITests;

/// <summary>Screen capture of one window and PNG reading/writing. Windows only (System.Drawing).</summary>
internal static class Capture
{
    public const int MaxWidth = 1600;

    /// <summary>The window's visible frame (without the invisible resize border and shadow) in physical pixels, clipped to the virtual screen.</summary>
    public static Rectangle FrameOf(IntPtr hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0)
            Native.GetWindowRect(hwnd, out r);
        var frame = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        var screen = new Rectangle(Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
        frame.Intersect(screen);
        return frame;
    }

    /// <summary>Copies the pixels of the window's frame from the screen. The window must be on top and not covered: the caller brings it to the front first.</summary>
    public static PixelImage Window(IntPtr hwnd)
    {
        var frame = FrameOf(hwnd);
        if (frame.Width < 2 || frame.Height < 2) throw new InvalidOperationException($"the window's visible area is {frame.Width}x{frame.Height}");
        using var bmp = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(frame.Left, frame.Top, 0, 0, frame.Size, CopyPixelOperation.SourceCopy);
        return FromBitmap(bmp);
    }

    public static PixelImage FromBitmap(Bitmap source)
    {
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[source.Width * source.Height * 4];
            for (var y = 0; y < source.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * source.Width * 4, source.Width * 4);
            for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            return new PixelImage(source.Width, source.Height, bytes);
        }
        finally { source.UnlockBits(data); }
    }

    public static PixelImage Load(string path)
    {
        using var bmp = new Bitmap(path);
        return FromBitmap(bmp);
    }

    public static void Save(PixelImage image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var bmp = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < image.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(image.Bgra, y * image.Width * 4, data.Scan0 + y * data.Stride, image.Width * 4);
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>True when the picture shows more than one flat colour: a black or all-white capture (nothing rendered, window not composited) fails this.</summary>
    public static (bool HasContent, int Colours) LooksRendered(PixelImage image)
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < image.Bgra.Length; i += 4 * 7)
        {
            seen.Add((image.Bgra[i] >> 4) | (image.Bgra[i + 1] >> 4) << 4 | (image.Bgra[i + 2] >> 4) << 8);
            if (seen.Count >= 12) break;
        }
        return (seen.Count >= 6, seen.Count);
    }

    /// <summary>Paints the given rectangles (window-relative physical pixels) in a flat colour. Used on the copy that is compared with baselines.</summary>
    public static PixelImage Mask(PixelImage image, IEnumerable<Rectangle> rectangles)
    {
        var copy = new PixelImage(image.Width, image.Height, (byte[])image.Bgra.Clone());
        foreach (var rectangle in rectangles)
        {
            var r = Rectangle.Intersect(rectangle, new Rectangle(0, 0, image.Width, image.Height));
            for (var y = r.Top; y < r.Bottom; y++)
                for (var x = r.Left; x < r.Right; x++) copy.Set(x, y, 200, 0, 200);
        }
        return copy;
    }
}
