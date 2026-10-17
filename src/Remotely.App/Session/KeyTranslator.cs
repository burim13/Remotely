using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Core;

namespace Remotely.App.Session;

/// <summary>
/// Turns a local key press into what the other PC needs: Win32 virtual-key code, scan code and the "extended key" flag. WinUI's <see cref="VirtualKey"/> values are the Win32 codes.
/// OWNER: agent A7 (input fidelity). The session window (A5) calls <see cref="TryTranslate(VirtualKey, out ushort, out ushort, out bool)"/>; keep this signature.
/// </summary>
public static class KeyTranslator
{
    private const ushort VkProcessKey = 0xE5, VkPacket = 0xE7;

    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);

    public static bool TryTranslate(VirtualKey key, out ushort virtualKey, out ushort scanCode, out bool extended)
    {
        virtualKey = (ushort)key;
        scanCode = 0;
        extended = false;
        // 0 = no key; 0xE5 = the input method is processing this key (its result arrives as a character); 0xE7 = a synthetic text packet.
        if (virtualKey is 0 or VkProcessKey or VkPacket) return false;
        scanCode = (ushort)MapVirtualKeyW(virtualKey, 0);
        extended = IsExtended(key);
        return true;
    }

    /// <summary>
    /// Same, but uses what the keyboard itself reported for the scan code and the extended flag. That is exact for the right-hand Ctrl and Alt (AltGr), the
    /// number pad's Enter and keys whose virtual key is the same on both sides, which the key-only overload can only guess from the key code.
    /// </summary>
    public static bool TryTranslate(VirtualKey key, CorePhysicalKeyStatus status, out ushort virtualKey, out ushort scanCode, out bool extended)
    {
        if (!TryTranslate(key, out virtualKey, out scanCode, out extended)) return false;
        if (status.ScanCode != 0) scanCode = (ushort)status.ScanCode;
        extended |= status.IsExtendedKey;
        return true;
    }

    private static bool IsExtended(VirtualKey key) => key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right
        or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Insert or VirtualKey.Delete
        or VirtualKey.RightControl or VirtualKey.RightMenu or VirtualKey.Divide or VirtualKey.NumberKeyLock or VirtualKey.Snapshot
        or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.Application;
}
