using System.Runtime.InteropServices;
using Remotely.Host.Interop;
using Remotely.HostCore;

namespace Remotely.Host.Capture;

/// <summary>Reads the mouse pointer on the thread's current desktop: where it is, and (only when the viewer does not have it yet) what it looks like.</summary>
internal sealed class CursorGrabber
{
    private const int MaxSide = 128;
    private readonly Dictionary<IntPtr, CursorShape?> _shapes = [];

    /// <summary>Must run on the desktop thread. Returns null when the pointer cannot be read.</summary>
    public (CursorState State, CursorShape? NewShape)? Read(ulong knownShapeId)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!CaptureNative.GetCursorInfo(ref info)) return null;
        var visible = (info.flags & CaptureNative.CURSOR_SHOWING) != 0 && info.hCursor != IntPtr.Zero;
        var id = (ulong)(long)info.hCursor;
        var state = new CursorState(visible, id, info.ptX, info.ptY);
        if (!visible || id == knownShapeId) return (state, null);
        if (!_shapes.TryGetValue(info.hCursor, out var shape))
        {
            shape = Render(info.hCursor, id);
            if (_shapes.Count > 64) _shapes.Clear();
            _shapes[info.hCursor] = shape;
        }
        return (state, shape);
    }

    /// <summary>
    /// Draws the cursor twice, on black and on white, and recovers colour and transparency from the two results. This handles cursors with a real alpha
    /// channel and the old two-bitmap kind alike; "invert the screen" cursors (the text I-beam on some themes) come out as semi-transparent grey.
    /// </summary>
    private static CursorShape? Render(IntPtr cursor, ulong id)
    {
        if (!CaptureNative.GetIconInfo(cursor, out var icon)) return null;
        IntPtr screen = IntPtr.Zero, dc = IntPtr.Zero, black = IntPtr.Zero, white = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            var source = icon.hbmColor != IntPtr.Zero ? icon.hbmColor : icon.hbmMask;
            if (CaptureNative.GetObjectW(source, Marshal.SizeOf<BITMAP>(), out var bitmap) == 0) return null;
            var width = bitmap.bmWidth;
            var height = icon.hbmColor != IntPtr.Zero ? bitmap.bmHeight : bitmap.bmHeight / 2;
            if (width < 1 || height < 1 || width > MaxSide || height > MaxSide) return null;

            screen = Native.GetDC(IntPtr.Zero);
            dc = Native.CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            black = Native.CreateDIBSection(screen, ref header, 0, out var blackBits, IntPtr.Zero, 0);
            white = Native.CreateDIBSection(screen, ref header, 0, out var whiteBits, IntPtr.Zero, 0);
            if (black == IntPtr.Zero || white == IntPtr.Zero || blackBits == IntPtr.Zero || whiteBits == IntPtr.Zero) return null;
            var bytes = width * height * 4;

            var onBlack = new byte[bytes];
            var onWhite = new byte[bytes];
            Array.Fill(onWhite, (byte)255);
            Marshal.Copy(onBlack, 0, blackBits, bytes);
            Marshal.Copy(onWhite, 0, whiteBits, bytes);

            previous = Native.SelectObject(dc, black);
            CaptureNative.DrawIconEx(dc, 0, 0, cursor, width, height, 0, IntPtr.Zero, CaptureNative.DI_NORMAL);
            Native.GdiFlush();
            Marshal.Copy(blackBits, onBlack, 0, bytes);
            Native.SelectObject(dc, white);
            CaptureNative.DrawIconEx(dc, 0, 0, cursor, width, height, 0, IntPtr.Zero, CaptureNative.DI_NORMAL);
            Native.GdiFlush();
            Marshal.Copy(whiteBits, onWhite, 0, bytes);

            var output = new byte[bytes];
            for (var i = 0; i < bytes; i += 4)
            {
                var difference = (255 - (onWhite[i] - onBlack[i]) + 255 - (onWhite[i + 1] - onBlack[i + 1]) + 255 - (onWhite[i + 2] - onBlack[i + 2])) / 3;
                var alpha = Math.Clamp(difference, 0, 255);
                output[i + 3] = (byte)alpha;
                if (alpha == 0) continue;
                for (var c = 0; c < 3; c++) output[i + c] = (byte)Math.Min(255, onBlack[i + c] * 255 / alpha);
            }
            return new CursorShape(id, icon.xHotspot, icon.yHotspot, width, height, output);
        }
        finally
        {
            if (previous != IntPtr.Zero) Native.SelectObject(dc, previous);
            if (black != IntPtr.Zero) Native.DeleteObject(black);
            if (white != IntPtr.Zero) Native.DeleteObject(white);
            if (dc != IntPtr.Zero) Native.DeleteDC(dc);
            if (screen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screen);
            if (icon.hbmColor != IntPtr.Zero) Native.DeleteObject(icon.hbmColor);
            if (icon.hbmMask != IntPtr.Zero) Native.DeleteObject(icon.hbmMask);
        }
    }
}
