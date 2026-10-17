using System.Runtime.InteropServices;
using Remotely.Core.ServerManager;

namespace Remotely.ServerManager;

/// <summary>
/// The notification-area icon. WinUI 3 has none of its own, so this is the plain Win32 one: a hidden window that receives the icon's mouse messages, an icon drawn
/// from <see cref="TrayIconRenderer"/> in the status colour, and balloon notifications (Windows shows them as toasts).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint WmTray = WmApp + 1;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10;
    private const uint NiifInfo = 1;
    private const uint MfString = 0;
    private const uint TpmRightButton = 2, TpmReturnCmd = 0x100;
    private const int SmCxSmIcon = 49;
    private const string ClassName = "RemotelyServerManagerTray";

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WndClassEx cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[]? bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    private readonly WndProc _proc;   // kept in a field so the garbage collector cannot free the delegate Windows calls
    private IntPtr _hwnd;
    private IntPtr _icon;
    private uint _taskbarCreated;
    private string _tip = "";
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    private TrayIcon() => _proc = OnMessage;

    /// <summary>Creates the icon, or returns null when Windows would not (no notification area, for example). The Manager then works without it.</summary>
    public static TrayIcon? TryCreate(TrayStatus status, string tip)
    {
        var tray = new TrayIcon { _tip = tip };
        try
        {
            if (!tray.Create(status)) { tray.Dispose(); return null; }
            return tray;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or InvalidOperationException)
        {
            App.LogCrash(ex);
            tray.Dispose();
            return null;
        }
    }

    private bool Create(TrayStatus status)
    {
        var instance = GetModuleHandleW(null);
        var cls = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance,
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref cls);   // fails harmlessly when the class is already registered
        _hwnd = CreateWindowExW(0, ClassName, "Remotely Server Manager tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return false;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = MakeIcon(status);
        return Notify(NimAdd, NifMessage | NifIcon | NifTip);
    }

    private NotifyIconData Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WmTray,
        hIcon = _icon,
        szTip = _tip.Length > 127 ? _tip[..127] : _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private bool Notify(uint message, uint flags)
    {
        var data = Data(flags);
        return Shell_NotifyIconW(message, ref data);
    }

    private static IntPtr MakeIcon(TrayStatus status)
    {
        var size = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        var color = CreateBitmap(size, size, 1, 32, TrayIconRenderer.RenderBgra(size, status));
        var mask = CreateBitmap(size, size, 1, 1, new byte[(size + 15) / 16 * 2 * size]);
        var info = new IconInfo { fIcon = 1, hbmMask = mask, hbmColor = color };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    public void SetStatus(TrayStatus status, string tip)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        var old = _icon;
        _icon = MakeIcon(status);
        _tip = tip;
        Notify(NimModify, NifIcon | NifTip);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    public void Balloon(string title, string text)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        var data = Data(NifInfo);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = NiifInfo;
        Shell_NotifyIconW(NimModify, ref data);
    }

    private IntPtr OnMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WmTray)
            {
                var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                if (mouse == WmLButtonUp) OpenRequested?.Invoke();
                else if (mouse == WmRButtonUp) ShowMenu();
                return IntPtr.Zero;
            }
            if (_taskbarCreated != 0 && msg == _taskbarCreated)
            {
                Notify(NimAdd, NifMessage | NifIcon | NifTip);   // Explorer restarted and lost the icon
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);   // an exception must not escape into Windows' message dispatch
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MfString, (UIntPtr)1, Ui.T("Tray_Open"));
        AppendMenuW(menu, MfString, (UIntPtr)2, Ui.T("Tray_Exit"));
        GetCursorPos(out var point);
        SetForegroundWindow(_hwnd);   // without this the menu does not close when the user clicks elsewhere
        var command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (command == 1) OpenRequested?.Invoke();
        else if (command == 2) ExitRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            Notify(NimDelete, 0);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }
}
