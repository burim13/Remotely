using Remotely.UITests.Imaging;

namespace Remotely.Tests;

/// <summary>The pure image logic of the UI test project (tests/Remotely.UITests/Imaging), compiled here so it runs on Linux.</summary>
public class ImageDiffTests
{
    private static PixelImage Grey(int w = 200, int h = 100, byte v = 128) => PixelImage.Solid(w, h, v, v, v);

    [Fact]
    public void Identical_pictures_pass()
    {
        var result = ImageDiff.Compare(Grey(), Grey());
        Assert.True(result.Passed);
        Assert.Equal(0, result.DifferingPixels);
    }

    [Fact]
    public void Noise_below_the_channel_tolerance_is_ignored()
    {
        var noisy = Grey(v: 140);   // every pixel changes by 12, tolerance is 16
        Assert.True(ImageDiff.Compare(Grey(), noisy).Passed);
    }

    [Fact]
    public void A_visible_block_fails()
    {
        var changed = Grey();
        for (var y = 10; y < 20; y++)
            for (var x = 10; x < 30; x++) changed.Set(x, y, 0, 0, 0);
        var result = ImageDiff.Compare(Grey(), changed);
        Assert.False(result.Passed);
        Assert.Equal(200, result.DifferingPixels);
        Assert.Equal((10, 10, 29, 19), (result.MinX, result.MinY, result.MaxX, result.MaxY));
        Assert.NotNull(result.Highlight);
    }

    [Fact]
    public void A_single_pixel_passes_the_default_but_fails_a_strict_comparison()
    {
        var changed = Grey();
        changed.Set(5, 5, 0, 0, 0);
        Assert.True(ImageDiff.Compare(Grey(), changed).Passed);   // a blinking caret must not fail a run
        var strict = new DiffOptions(MaxDifferingPixels: 0, MaxDifferingFraction: 0);
        var result = ImageDiff.Compare(Grey(), changed, strict);
        Assert.False(result.Passed);
        Assert.Equal(1, result.DifferingPixels);
    }

    [Fact]
    public void Different_sizes_fail()
    {
        var result = ImageDiff.Compare(Grey(200, 100), Grey(200, 101));
        Assert.False(result.SizeMatches);
        Assert.False(result.Passed);
    }

    [Fact]
    public void The_allowance_grows_with_the_area()
    {
        var options = new DiffOptions();
        Assert.Equal(24, options.AllowedFor(100, 100));
        Assert.Equal(200, options.AllowedFor(1000, 1000));
    }

    [Fact]
    public void Downscale_keeps_aspect_ratio_and_averages()
    {
        var image = Grey(3200, 1600);
        for (var y = 0; y < 1600; y++)
            for (var x = 0; x < 1600; x++) image.Set(x, y, 0, 0, 0);   // left half black
        var small = image.DownscaleToWidth(1600);
        Assert.Equal(1600, small.Width);
        Assert.Equal(800, small.Height);
        Assert.Equal(0, small.Bgra[0]);
        Assert.Equal(128, small.Bgra[(0 * 1600 + 1599) * 4]);
    }

    [Fact]
    public void Downscale_leaves_a_narrow_picture_alone()
    {
        var image = Grey(800, 600);
        Assert.Same(image, image.DownscaleToWidth(1600));
    }

    [Fact]
    public void Downscaled_edge_pixels_are_averaged()
    {
        var image = Grey(4, 2);
        image.Set(0, 0, 0, 0, 0);
        image.Set(1, 0, 255, 255, 255);
        var half = image.DownscaleToWidth(2);
        Assert.Equal(2, half.Width);
        Assert.Equal(1, half.Height);
        Assert.InRange(half.Bgra[0], 100, 160);   // mean of 0, 255, 128, 128
    }

    [Fact]
    public void A_buffer_of_the_wrong_length_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new PixelImage(2, 2, new byte[15]));
    }
}
