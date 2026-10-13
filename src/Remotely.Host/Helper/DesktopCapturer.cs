using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using Remotely.Host.Interop;
using Remotely.HostCore;

namespace Remotely.Host.Helper;

/// <summary>
/// Captures whichever desktop currently receives input: the user's ("Default") or the secure one ("Winlogon": lock screen, sign-in, UAC).
/// Needs SYSTEM to open the secure desktop. Uses GDI (BitBlt) for this prototype; one dedicated thread owns all desktop switching,
/// because SetThreadDesktop applies per thread and fails if the thread owns windows.
/// </summary>
internal sealed class DesktopCapturer : IFrameSource, IDisposable
{
    private const int MaxJpegBytes = 900_000;
    private static readonly long[] Qualities = [60, 45, 30, 15];

    private sealed record RawFrame(int Width, int Height, string Desktop, byte[] Bgra);

    private readonly BlockingCollection<TaskCompletionSource<RawFrame>> _jobs = [];
    private readonly Thread _thread;
    private IntPtr _currentDesktop;

    public DesktopCapturer()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Remotely capture" };
        _thread.Start();
    }

    public async Task<FrameInfo> CaptureAsync(CancellationToken ct)
    {
        var job = new TaskCompletionSource<RawFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Add(job, ct);
        var raw = await job.Task.ConfigureAwait(false);
        // Encoding runs on a pool thread: GDI+ must never touch the thread that switches desktops.
        return await Task.Run(() => new FrameInfo(raw.Width, raw.Height, raw.Desktop, EncodeJpeg(raw)), ct).ConfigureAwait(false);
    }

    private void Loop()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try { job.SetResult(CaptureNow()); }
            catch (Exception ex) { job.SetException(ex); }
        }
    }

    private RawFrame CaptureNow()
    {
        var input = Native.OpenInputDesktop(0, false, Native.GENERIC_ALL);
        if (input == IntPtr.Zero) throw Fail("OpenInputDesktop");
        if (!Native.SetThreadDesktop(input))
        {
            var error = Fail("SetThreadDesktop");
            Native.CloseDesktop(input);
            throw error;
        }
        if (_currentDesktop != IntPtr.Zero) Native.CloseDesktop(_currentDesktop);
        _currentDesktop = input;
        var desktopName = Native.DesktopName(input);

        var left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0) throw new InvalidOperationException($"Desktop '{desktopName}' reports no screen size.");

        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw Fail("GetDC");
        var memory = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = Native.CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width, biHeight = -height, // negative: top-down rows
                biPlanes = 1, biBitCount = 32,
            };
            dib = Native.CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw Fail("CreateDIBSection");
            previous = Native.SelectObject(memory, dib);
            if (!Native.BitBlt(memory, 0, 0, width, height, screen, left, top, Native.SRCCOPY | Native.CAPTUREBLT)) throw Fail("BitBlt");
            Native.GdiFlush();
            var pixels = new byte[checked(width * height * 4)];
            System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
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
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(raw.Bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return EncodeJpeg(raw.Width, raw.Height, pin.AddrOfPinnedObject());
        }
        finally
        {
            pin.Free();
        }
    }

    private static byte[] EncodeJpeg(int width, int height, IntPtr bits)
    {
        using var bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, bits);
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

    private static InvalidOperationException Fail(string api)
    {
        var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        return new InvalidOperationException($"{api} failed: {new Win32Exception(code).Message} (error {code}).");
    }

    public void Dispose()
    {
        _jobs.CompleteAdding();
        _thread.Join(2000);
    }
}
