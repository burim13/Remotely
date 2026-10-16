using System.Runtime.InteropServices;
using Windows.System;

namespace Remotely.App.Session;

/// <summary>
/// Turns a local key press into what the other PC needs: Win32 virtual-key code, scan code and the "extended key" flag. WinUI's <see cref="VirtualKey"/> values are the Win32 codes.
/// OWNER: agent A7 (input fidelity). The session window (A5) calls <see cref="TryTranslate"/> and nothing else; keep this signature.
/// </summary>
public static class KeyTranslator
{
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);

    public static bool TryTranslate(VirtualKey key, out ushort virtualKey, out ushort scanCode, out bool extended)
    {
        virtualKey = (ushort)key;
        scanCode = 0;
        extended = false;
        if (virtualKey == 0) return false;
        scanCode = (ushort)MapVirtualKeyW(virtualKey, 0);
        extended = IsExtended(key);
        return true;
    }

    private static bool IsExtended(VirtualKey key) => key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right
        or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Insert or VirtualKey.Delete
        or VirtualKey.RightControl or VirtualKey.RightMenu or VirtualKey.Divide or VirtualKey.NumberKeyLock or VirtualKey.Snapshot
        or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.Application;
}
