using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Input;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.App.Session.Input;

/// <summary>
/// Feeds the remote screen's key events into a <see cref="KeyboardEncoder"/> (which decides key code or typed text) and keeps the mode in step with the
/// keyboard layouts of both PCs.
/// <para>What WinUI 3 does and does not give us (stated, not verified on a real keyboard): <c>KeyDown</c>/<c>KeyUp</c> carry the key and its physical scan
/// code; <c>CharacterReceived</c> carries the characters the system produced after layout, dead-key and AltGr handling. An input method's composition
/// window belongs to the local PC and its uncommitted text is not mirrored to the other PC; only the committed characters are sent, as text, when
/// WinUI raises <c>CharacterReceived</c> for them. Whether it does for every input method is not verified.</para>
/// </summary>
public sealed class KeyboardInput
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);

    private readonly KeyboardEncoder _encoder;

    public KeyboardInput(Action<InputEvent> emit) => _encoder = new KeyboardEncoder(emit);

    public KeyboardMode Mode => _encoder.Mode;

    /// <summary>The keyboard layout handle (HKL) of the window that has the keyboard here, as the host's reply is compared against.</summary>
    public static uint LocalLayout() =>
        unchecked((uint)(long)GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero)));

    /// <summary>Call when the session opens, when the setting changes and whenever the host reports its layout (<see cref="ViewerSession.HostKeyboardLayoutChanged"/>).</summary>
    public void Configure(KeyboardModeSetting setting, uint hostLayout, bool hostSupportsText)
    {
        _encoder.TextEnabled = hostSupportsText;
        _encoder.Mode = KeyboardModeChooser.Resolve(setting, LocalLayout(), hostLayout, hostSupportsText);
    }

    /// <summary>Returns true if the event was consumed (the other PC, not this window, should see it).</summary>
    public bool OnKeyDown(KeyRoutedEventArgs e)
    {
        if (!KeyTranslator.TryTranslate(e.Key, e.KeyStatus, out var vk, out var scan, out var extended)) return false;
        _encoder.KeyDown(vk, scan, extended);
        return true;
    }

    public bool OnKeyUp(KeyRoutedEventArgs e)
    {
        if (!KeyTranslator.TryTranslate(e.Key, e.KeyStatus, out var vk, out var scan, out var extended)) return false;
        _encoder.KeyUp(vk, scan, extended);
        return true;
    }

    /// <summary>A character produced by the system or by an input method (UIElement.CharacterReceived).</summary>
    public void OnCharacter(CharacterReceivedRoutedEventArgs e)
    {
        _encoder.Character(e.Character);
        e.Handled = true;
    }

    /// <summary>Focus lost, session closing: release every key on both ends.</summary>
    public void ReleaseAll() => _encoder.ReleaseAll();
}
