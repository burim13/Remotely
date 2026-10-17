using System.Drawing;
using System.Drawing.Imaging;
using Remotely.HostCore;

namespace Remotely.Host.Capture;

/// <summary>GDI+ JPEG encoder over a rectangle of a BGRA buffer. GDI+ bitmaps are independent, so several rectangles can be encoded at once.</summary>
internal sealed class JpegTileEncoder : ITileEncoder
{
    private static readonly ImageCodecInfo Codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");

    public unsafe byte[] Encode(ReadOnlySpan<byte> bgra, int stride, int width, int height, int quality)
    {
        fixed (byte* first = bgra)
        {
            using var bitmap = new Bitmap(width, height, stride, PixelFormat.Format32bppRgb, (IntPtr)first);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            using var stream = new MemoryStream(Math.Max(1024, width * height / 8));
            bitmap.Save(stream, Codec, parameters);
            return stream.ToArray();
        }
    }
}
