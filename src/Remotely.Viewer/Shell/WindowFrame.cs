using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Remotely.Viewer.Shell;

/// <summary>Gives a frameless window the Windows 11 rounded corners and dark/light title treatment, the way Portico's windows do. Silently does nothing on older Windows.</summary>
internal static class WindowFrame
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Round(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var preference = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        };
    }
}
