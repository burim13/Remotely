using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Remotely.HostCore;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Remotely.App;

public enum CompositeResult
{
    /// <summary>The picture changed in place.</summary>
    Updated,
    /// <summary>A new bitmap was made (first picture, or the size changed): hand <see cref="FrameCompositor.Source"/> to the view.</summary>
    NewBitmap,
    /// <summary>A partial update arrived without a picture to paint it on: ask the host for a complete one.</summary>
    NeedKeyFrame,
}

/// <summary>
/// Keeps the remote picture in one <see cref="WriteableBitmap"/>: a complete frame replaces it, tiles are painted over it where they belong. UI thread only.
/// </summary>
public sealed class FrameCompositor
{
    private WriteableBitmap? _bitmap;
    private int _width, _height;

    public ImageSource? Source => _bitmap;
    public int Width => _width;
    public int Height => _height;

    public async Task<CompositeResult> ApplyAsync(FrameInfo frame)
    {
        var result = CompositeResult.Updated;
        if (frame.Tiles is null)
        {
            var (w, h, pixels) = await DecodeAsync(frame.Jpeg);
            if (_bitmap is null || w != _width || h != _height)
            {
                _bitmap = new WriteableBitmap(w, h);
                (_width, _height) = (w, h);
                result = CompositeResult.NewBitmap;
            }
            using var stream = _bitmap.PixelBuffer.AsStream();
            stream.Write(pixels, 0, pixels.Length);
        }
        else
        {
            if (_bitmap is null || frame.Width != _width || frame.Height != _height) return CompositeResult.NeedKeyFrame;
            using var stream = _bitmap.PixelBuffer.AsStream();
            foreach (var tile in frame.Tiles)
            {
                var (w, h, pixels) = await DecodeAsync(tile.Jpeg);
                if (w != tile.Width || h != tile.Height || tile.X < 0 || tile.Y < 0 || tile.X + w > _width || tile.Y + h > _height) continue;
                for (var row = 0; row < h; row++)
                {
                    stream.Position = ((long)(tile.Y + row) * _width + tile.X) * 4;
                    stream.Write(pixels, row * w * 4, w * 4);
                }
            }
        }
        _bitmap.Invalidate();
        return result;
    }

    private static async Task<(int Width, int Height, byte[] Pixels)> DecodeAsync(byte[] jpeg)
    {
        using var memory = new InMemoryRandomAccessStream();
        await memory.WriteAsync(jpeg.AsBuffer());
        memory.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(memory);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return ((int)decoder.PixelWidth, (int)decoder.PixelHeight, data.DetachPixelData());
    }
}
