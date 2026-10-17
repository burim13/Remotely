namespace Remotely.UITests.Imaging;

/// <summary>How strict a comparison is. A pixel counts as different when any colour channel differs by more than <see cref="ChannelTolerance"/> (anti-aliasing and
/// dithering noise stays below it); a picture fails when more than the allowed number of pixels differ or the sizes differ.</summary>
public sealed record DiffOptions(int ChannelTolerance = 16, int MaxDifferingPixels = 24, double MaxDifferingFraction = 0.0002)
{
    /// <summary>The number of differing pixels a picture of this size may have: the larger of the fixed count and the fraction of the area.</summary>
    public int AllowedFor(int width, int height) => Math.Max(MaxDifferingPixels, (int)Math.Ceiling(width * (double)height * MaxDifferingFraction));
}

public sealed record DiffResult(bool SizeMatches, int DifferingPixels, int Allowed, int MaxChannelDelta, int MinX, int MinY, int MaxX, int MaxY, PixelImage? Highlight)
{
    public bool Passed => SizeMatches && DifferingPixels <= Allowed;

    public string Describe() =>
        !SizeMatches ? "size differs"
        : DifferingPixels == 0 ? "identical within tolerance"
        : $"{DifferingPixels} differing pixel(s) (allowed {Allowed}), largest channel change {MaxChannelDelta}, inside x {MinX}-{MaxX}, y {MinY}-{MaxY}";
}

public static class ImageDiff
{
    public static DiffResult Compare(PixelImage baseline, PixelImage actual, DiffOptions? options = null)
    {
        options ??= new DiffOptions();
        if (baseline.Width != actual.Width || baseline.Height != actual.Height)
            return new DiffResult(false, -1, 0, 0, 0, 0, 0, 0, null);

        var w = baseline.Width;
        var h = baseline.Height;
        var count = 0;
        var maxDelta = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var highlight = new byte[baseline.Bgra.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var delta = Math.Max(Math.Abs(baseline.Bgra[i] - actual.Bgra[i]), Math.Max(Math.Abs(baseline.Bgra[i + 1] - actual.Bgra[i + 1]), Math.Abs(baseline.Bgra[i + 2] - actual.Bgra[i + 2])));
                if (delta > options.ChannelTolerance)
                {
                    count++;
                    if (delta > maxDelta) maxDelta = delta;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    highlight[i] = 0; highlight[i + 1] = 0; highlight[i + 2] = 255; highlight[i + 3] = 255;
                }
                else
                {
                    // a faded copy of the actual picture, so the red marks stay readable
                    highlight[i] = (byte)(actual.Bgra[i] / 4 + 190);
                    highlight[i + 1] = (byte)(actual.Bgra[i + 1] / 4 + 190);
                    highlight[i + 2] = (byte)(actual.Bgra[i + 2] / 4 + 190);
                    highlight[i + 3] = 255;
                }
            }
        }
        if (count == 0) minX = minY = maxX = maxY = 0;
        return new DiffResult(true, count, options.AllowedFor(w, h), maxDelta, minX, minY, maxX, maxY, new PixelImage(w, h, highlight));
    }
}
