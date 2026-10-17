using System.Runtime.InteropServices;

namespace Remotely.Host.Capture;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEXW
{
    public int cbSize;
    public RECT rcMonitor, rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CURSORINFO
{
    public int cbSize;
    public int flags;
    public IntPtr hCursor;
    public int ptX, ptY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO
{
    public bool fIcon;
    public int xHotspot, yHotspot;
    public IntPtr hbmMask, hbmColor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAP
{
    public int bmType, bmWidth, bmHeight, bmWidthBytes;
    public ushort bmPlanes, bmBitsPixel;
    public IntPtr bmBits;
}

internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

internal static class CaptureNative
{
    public const uint MONITORINFOF_PRIMARY = 1;
    public const int CURSOR_SHOWING = 1;
    public const uint DI_NORMAL = 3;

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEXW info);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] public static extern int GetObjectW(IntPtr handle, int size, out BITMAP bitmap);
}
