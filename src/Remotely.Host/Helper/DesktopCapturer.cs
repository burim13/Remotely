using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Remotely.Host.Interop;
using Remotely.HostCore;

namespace Remotely.Host.Helper;

/// <summary>Captures the whole virtual desktop of whichever desktop has input, using GDI (BitBlt). JPEG encoding happens on a pool thread, never on the desktop thread.</summary>
internal sealed class DesktopCapturer(DesktopThread desktop) : IFrameSource
{
    private const int MaxJpegBytes = 900_000;
    private static readonly long[] Qualities = [60, 45, 30, 15];

    private sealed record RawFrame(int Width, int Height, string Desktop, byte[] Bgra);

    public async Task<FrameInfo> CaptureAsync(CancellationToken ct)
    {
        var raw = await desktop.RunAsync(CaptureNow).ConfigureAwait(false);
        return await Task.Run(() => new FrameInfo(raw.Width, raw.Height, raw.Desktop, EncodeJpeg(raw)), ct).ConfigureAwait(false);
    }

    private static RawFrame CaptureNow(string desktopName)
    {
        var left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0) throw new InvalidOperationException($"Desktop '{desktopName}' reports no screen size.");

        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw DesktopThread.Fail("GetDC");
        var memory = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = Native.CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width, biHeight = -height, // negative: top-down rows
                biPlanes = 1, biBitCount = 32,
            };
            dib = Native.CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw DesktopThread.Fail("CreateDIBSection");
            previous = Native.SelectObject(memory, dib);
            if (!Native.BitBlt(memory, 0, 0, width, height, screen, left, top, Native.SRCCOPY | Native.CAPTUREBLT)) throw DesktopThread.Fail("BitBlt");
            Native.GdiFlush();
            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new RawFrame(width, height, desktopName, pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero) Native.SelectObject(memory, previous);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            if (memory != IntPtr.Zero) Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static byte[] EncodeJpeg(RawFrame raw)
    {
        var pin = GCHandle.Alloc(raw.Bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(raw.Width, raw.Height, raw.Width * 4, PixelFormat.Format32bppRgb, pin.AddrOfPinnedObject());
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            foreach (var quality in Qualities)
            {
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                using var stream = new MemoryStream();
                bitmap.Save(stream, codec, parameters);
                if (stream.Length <= MaxJpegBytes) return stream.ToArray();
            }
            throw new InvalidOperationException("The screen is too detailed to send as one frame yet.");
        }
        finally
        {
            pin.Free();
        }
    }
}
