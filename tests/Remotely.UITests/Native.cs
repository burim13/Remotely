using System.Runtime.InteropServices;
using System.Text;

namespace Remotely.UITests;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(int x, int y, uint flags);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    internal const uint GW_OWNER = 4;
    internal const int SW_RESTORE = 9;
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    internal const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    /// <summary>Per-monitor v2, so rectangles from Win32 and UI Automation are physical pixels, like the screen capture.</summary>
    internal static void MakeDpiAware() => SetProcessDpiAwarenessContext(new IntPtr(-4));

    [DllImport("user32.dll")] internal static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr handle);

    /// <summary>The unicode text on the clipboard, or null when there is none or it cannot be opened.</summary>
    internal static string? ClipboardText()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero)) { Thread.Sleep(100); continue; }
            try
            {
                var handle = GetClipboardData(13);
                if (handle == IntPtr.Zero) return null;
                var ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(ptr); }
                finally { GlobalUnlock(handle); }
            }
            finally { CloseClipboard(); }
        }
        return null;
    }

    // ---- display configuration (scaling) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct DpiGet
    {
        public uint Type, Size, AdapterLow;
        public int AdapterHigh;
        public uint Id;
        public int MinRel, CurRel, MaxRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DpiSet
    {
        public uint Type, Size, AdapterLow;
        public int AdapterHigh;
        public uint Id;
        public int ScaleRel;
    }

    [DllImport("user32.dll")] internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] internal static extern int QueryDisplayConfig(uint flags, ref uint paths, [In, Out] byte[] pathArray, ref uint modes, [In, Out] byte[] modeArray, IntPtr topology);
    [DllImport("user32.dll")] internal static extern int DisplayConfigGetDeviceInfo(ref DpiGet request);
    [DllImport("user32.dll")] internal static extern int DisplayConfigSetDeviceInfo(ref DpiSet request);

    // ---- display modes ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency, ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int ChangeDisplaySettings(ref DEVMODE devMode, uint flags);

    // ---- high contrast ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIGHCONTRAST
    {
        public uint Size, Flags;
        public IntPtr DefaultScheme;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    internal static extern bool SystemParametersInfo(uint action, uint param, ref HIGHCONTRAST value, uint winIni);
}
