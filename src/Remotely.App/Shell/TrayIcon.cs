using System.Runtime.InteropServices;

namespace Remotely.App.Shell;

internal sealed record TrayMenuItem(int Id, string Text, bool Checked = false, bool Separator = false);

/// <summary>
/// Notification-area icon through the Win32 <c>Shell_NotifyIcon</c> API on a hidden message-only window. WinUI 3 has no tray API of its own; this needs
/// no package, works unpackaged, and runs on the UI thread that creates it (the window procedure is called on that thread). Menu: a native popup menu.
/// Left click opens the app, right click shows the menu; the icon is added again when Explorer restarts ("TaskbarCreated").
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x0400 + 1;       // WM_USER + 1
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x1;
    private const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, WM_NULL = 0;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 0x8;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    private const int SM_CXSMICON = 49;
    private static readonly IntPtr HwndMessage = new(-3);
    private const string WindowClass = "Remotely.TrayWindow";

    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private readonly Action<int> _command;
    private readonly Action _open;
    private readonly string _tooltip;
    private readonly WndProcDelegate _wndProc;
    private readonly uint _taskbarCreated;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;

    public TrayIcon(string iconPath, string tooltip, Func<IReadOnlyList<TrayMenuItem>> menu, Action<int> command, Action open)
    {
        _tooltip = tooltip;
        _menu = menu;
        _command = command;
        _open = open;
        _wndProc = WndProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        AllowDarkMenus();

        var instance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = instance,
            lpszClassName = WindowClass,
        };
        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
            throw new InvalidOperationException("Could not register the tray window class (error " + Marshal.GetLastWin32Error() + ").");
        _hwnd = CreateWindowEx(0, WindowClass, "Remotely tray", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the tray window (error " + Marshal.GetLastWin32Error() + ").");

        var size = GetSystemMetrics(SM_CXSMICON);
        _icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, size, size, LR_LOADFROMFILE);   // zero when the file is missing: Windows then shows a blank icon, the menu still works
        Add();
    }

    public bool IsAdded => _added;

    /// <summary>Replaces the icon (the taskbar theme changed). Keeps the old one when the file cannot be loaded.</summary>
    public void SetIcon(string iconPath)
    {
        var size = GetSystemMetrics(SM_CXSMICON);
        var icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        if (icon == IntPtr.Zero) return;
        var old = _icon;
        _icon = icon;
        if (_added)
        {
            var data = NewData(NIF_ICON);
            data.hIcon = _icon;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    /// <summary>Shows a balloon / toast-style notification from the tray icon.</summary>
    public void Notify(string title, string text)
    {
        if (!_added) return;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Native popup menus follow the system dark mode only when the process opts in. This uses uxtheme.dll ordinals 135 (SetPreferredAppMode) and 136 (FlushMenuThemes):
    /// undocumented, but used by many programs on Windows 10 1903 and later. If the entry points are missing the menu simply stays light.
    /// </summary>
    private static void AllowDarkMenus()
    {
        try
        {
            var lib = LoadLibrary("uxtheme.dll");
            if (lib == IntPtr.Zero) return;
            var setMode = GetProcAddress(lib, new IntPtr(135));
            var flush = GetProcAddress(lib, new IntPtr(136));
            if (setMode == IntPtr.Zero || flush == IntPtr.Zero) return;
            Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(setMode)(1 /* AllowDark: follow the system setting */);
            Marshal.GetDelegateForFunctionPointer<FlushMenuThemesDelegate>(flush)();
        }
        catch (Exception ex) when (ex is ArgumentException or MarshalDirectiveException or SEHException or DllNotFoundException)
        {
            App.LogCrash(ex);
        }
    }

    private delegate int SetPreferredAppModeDelegate(int mode);
    private delegate void FlushMenuThemesDelegate();

    private void Add()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = Truncate(_tooltip, 127);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == CallbackMessage)
            {
                switch ((uint)(lParam.ToInt64() & 0xFFFF))
                {
                    case WM_LBUTTONUP: _open(); break;
                    case WM_RBUTTONUP:
                    case WM_CONTEXTMENU: ShowMenu(hwnd); break;
                }
                return IntPtr.Zero;
            }
            if (_taskbarCreated != 0 && message == _taskbarCreated) Add();
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);   // a throw out of a window procedure would take the whole process down
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        try
        {
            foreach (var item in _menu())
                AppendMenu(menu, item.Separator ? MF_SEPARATOR : MF_STRING | (item.Checked ? MF_CHECKED : 0), (UIntPtr)(uint)item.Id, item.Separator ? null : item.Text);
            GetCursorPos(out var p);
            SetForegroundWindow(hwnd);   // without this the menu does not close when the user clicks elsewhere
            var id = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, p.X, p.Y, hwnd, IntPtr.Zero);
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (id > 0) _command(id);
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
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
    private struct NOTIFYICONDATA
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
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
}
