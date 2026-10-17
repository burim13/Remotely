namespace Remotely.UITests.Imaging;

/// <summary>A decoded picture: 32-bit pixels in B, G, R, A order, rows top to bottom, no padding. Pure data, so the diff logic runs and is tested on any OS.</summary>
public sealed class PixelImage
{
    public PixelImage(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "An image has at least one pixel.");
        if (bgra.Length != checked(width * height * 4)) throw new ArgumentException("The pixel buffer does not match width x height x 4.", nameof(bgra));
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public static PixelImage Solid(int width, int height, byte b, byte g, byte r)
    {
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4) { data[i] = b; data[i + 1] = g; data[i + 2] = r; data[i + 3] = 255; }
        return new PixelImage(width, height, data);
    }

    public void Set(int x, int y, byte b, byte g, byte r)
    {
        var i = (y * Width + x) * 4;
        Bgra[i] = b; Bgra[i + 1] = g; Bgra[i + 2] = r; Bgra[i + 3] = 255;
    }

    /// <summary>
    /// Shrinks the picture (box filter: each target pixel is the mean of the source pixels it covers) so it is at most <paramref name="maxWidth"/> wide, keeping the aspect ratio.
    /// A picture that is already narrow enough is returned as it is. Used to keep the committed screenshots small.
    /// </summary>
    public PixelImage DownscaleToWidth(int maxWidth)
    {
        if (maxWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        if (Width <= maxWidth) return this;
        var newWidth = maxWidth;
        var newHeight = Math.Max(1, (int)Math.Round((double)Height * maxWidth / Width));
        var result = new byte[newWidth * newHeight * 4];
        for (var ty = 0; ty < newHeight; ty++)
        {
            var y0 = (int)((long)ty * Height / newHeight);
            var y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * Height / newHeight));
            for (var tx = 0; tx < newWidth; tx++)
            {
                var x0 = (int)((long)tx * Width / newWidth);
                var x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * Width / newWidth));
                long b = 0, g = 0, r = 0, a = 0;
                var count = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var i = (y * Width + x) * 4;
                        b += Bgra[i]; g += Bgra[i + 1]; r += Bgra[i + 2]; a += Bgra[i + 3];
                        count++;
                    }
                }
                var o = (ty * newWidth + tx) * 4;
                result[o] = (byte)(b / count); result[o + 1] = (byte)(g / count); result[o + 2] = (byte)(r / count); result[o + 3] = (byte)(a / count);
            }
        }
        return new PixelImage(newWidth, newHeight, result);
    }
}
