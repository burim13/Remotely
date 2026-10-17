using Remotely.HostCore;

namespace Remotely.ViewerCore;

/// <summary>How typed keys travel to the host.</summary>
public enum KeyboardMode
{
    /// <summary>Every key is sent as virtual key and scan code; the host's own layout decides what it types. Exact for games and shortcuts; right only when both PCs use the same layout.</summary>
    ScanCode,
    /// <summary>Keys that type a character are sent as that character (Unicode), whatever the host's layout is. Shortcuts, function and navigation keys still go as key codes.</summary>
    Unicode,
}

/// <summary>What the person chose in settings.</summary>
public enum KeyboardModeSetting { Auto, ScanCode, Unicode }

public static class KeyboardModeChooser
{
    /// <summary>
    /// Auto picks key codes when the local and the host's keyboard layout handles are equal, and typed characters otherwise (including when the
    /// host's layout is unknown, because wrong letters are worse than a game that ignores typed characters). A host that does not advertise the
    /// extension cannot receive typed characters at all, so it always gets key codes.
    /// </summary>
    public static KeyboardMode Resolve(KeyboardModeSetting setting, uint localLayout, uint hostLayout, bool hostSupportsText)
    {
        if (!hostSupportsText) return KeyboardMode.ScanCode;
        return setting switch
        {
            KeyboardModeSetting.ScanCode => KeyboardMode.ScanCode,
            KeyboardModeSetting.Unicode => KeyboardMode.Unicode,
            _ => hostLayout != 0 && hostLayout == localLayout ? KeyboardMode.ScanCode : KeyboardMode.Unicode,
        };
    }
}

/// <summary>
/// Turns the local key events (key down, key up, character received) into input events, deciding per key whether it travels as a key code or as text.
/// Pure logic with no UI types, so the rules are unit-tested; the WinUI layer only feeds it.
/// <para>Rules in <see cref="KeyboardMode.Unicode"/>: modifiers are always sent as key codes; a key that types a character, pressed with no Ctrl (unless AltGr), Alt or Windows key,
/// is held back and its character (from the character event, which the system produces after layout, dead-key and AltGr processing) is sent as text; every other key
/// is sent as a key code. Characters that arrive with no key behind them (input method commit, character picker) are sent as text in both modes.</para>
/// </summary>
public sealed class KeyboardEncoder(Action<InputEvent> emit)
{
    private const ushort VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C, VkProcessKey = 0xE5, VkPacket = 0xE7;

    private readonly Dictionary<(ushort Vk, bool Extended), Held> _down = [];
    private readonly List<(ushort Vk, bool Extended)> _order = [];

    public KeyboardMode Mode { get; set; } = KeyboardMode.ScanCode;

    /// <summary>When false (the host cannot take typed characters) character events are ignored.</summary>
    public bool TextEnabled { get; set; } = true;

    public bool AnyDown => _down.Count > 0;

    public static bool IsModifier(ushort vk) => vk is VkShift or VkControl or VkMenu or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or VkLWin or VkRWin;

    /// <summary>Keys that normally type a character: letters, digits, space, the number pad's digits and operators, and the punctuation keys.</summary>
    public static bool IsTextKey(ushort vk) =>
        vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or 0x20 or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xC2 or >= 0xDB and <= 0xDF or 0xE2;

    public void KeyDown(ushort vk, ushort scan, bool extended)
    {
        if (vk is 0 or VkProcessKey or VkPacket) return; // input method in progress / synthetic packets: not keys
        var key = (vk, extended);
        if (_down.TryGetValue(key, out var held))
        {
            if (held.Forwarded) emit(InputEvent.Key(true, vk, scan, extended)); // auto-repeat: the host repeats it too
            return;
        }
        held = new Held(scan);
        if (Mode == KeyboardMode.Unicode && TextEnabled && IsTextKey(vk) && !ShortcutActive())
            held.Deferred = true;
        else
        {
            held.Forwarded = true;
            emit(InputEvent.Key(true, vk, scan, extended));
        }
        _down[key] = held;
        _order.Add(key);
    }

    public void KeyUp(ushort vk, ushort scan, bool extended)
    {
        var key = (vk, extended);
        if (!_down.Remove(key, out var held))
        {
            // Released without our having seen the press (focus moved in while held): let the host see a release only if we ever forwarded it.
            return;
        }
        _order.Remove(key);
        if (held.Forwarded) emit(InputEvent.Key(false, vk, held.Scan != 0 ? held.Scan : scan, extended));
    }

    /// <summary>A character the system produced (UTF-16 code unit).</summary>
    public void Character(ushort unit)
    {
        if (!TextEnabled || unit < 0x20 || unit is 0x7F or (>= 0x80 and <= 0x9F)) return;
        var pending = _order.Select(k => _down[k]).FirstOrDefault(h => h.Deferred && !h.GotChar);
        if (pending is not null) pending.GotChar = true;
        else if (_order.Any(k => _down[k].Forwarded && !IsModifier(k.Vk))) return; // the host types this one itself from the key code
        emit(InputEvent.Text(unit, true));
        emit(InputEvent.Text(unit, false));
    }

    /// <summary>Lets go of everything: key-ups for what was sent, then a ReleaseAll so the host drops anything else it still holds. Call on focus loss.</summary>
    public void ReleaseAll()
    {
        foreach (var key in _order.ToArray())
            if (_down[key].Forwarded) emit(InputEvent.Key(false, key.Vk, _down[key].Scan, key.Extended));
        _down.Clear();
        _order.Clear();
        emit(new InputEvent(InputKind.ReleaseAll));
    }

    private bool ShortcutActive()
    {
        var ctrl = IsDown(VkControl) || IsDown(0xA2) || IsDown(0xA3);
        var win = IsDown(VkLWin) || IsDown(VkRWin);
        var rightAlt = _down.ContainsKey((VkMenu, true)) || IsDown(0xA5);
        var leftAlt = _down.ContainsKey((VkMenu, false)) || IsDown(0xA4);
        var altGr = ctrl && rightAlt;
        return win || leftAlt || (ctrl && !altGr);
    }

    private bool IsDown(ushort vk) => _down.ContainsKey((vk, false)) || _down.ContainsKey((vk, true));

    private sealed class Held(ushort scan)
    {
        public ushort Scan { get; } = scan;
        public bool Forwarded { get; set; }
        public bool Deferred { get; set; }
        public bool GotChar { get; set; }
    }
}
