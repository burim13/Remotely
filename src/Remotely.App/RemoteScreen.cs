using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Remotely.HostCore;
using Windows.Foundation;
using Windows.System;
using HostButton = Remotely.HostCore.MouseButton;

namespace Remotely.App;

/// <summary>
/// Shows the remote screen scaled to fit and turns local mouse and keyboard input into <see cref="InputEvent"/>s.
/// Positions are sent normalised (0..65535 across the remote desktop), so no knowledge of the other PC's resolution is needed.
/// WinUI's <see cref="VirtualKey"/> values are the Win32 virtual-key codes, so keys pass straight through.
/// </summary>
public sealed class RemoteScreen : Grid
{
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);

    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private int _frameWidth, _frameHeight;

    public RemoteScreen()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        Children.Add(_image);
        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnWheel;
        KeyDown += (_, e) => SendKey(e, true);
        KeyUp += (_, e) => SendKey(e, false);
        LostFocus += (_, _) => FocusLost?.Invoke(); // let go of everything so no key or button is left stuck down on the other PC
    }

    public Action<InputEvent>? Input { get; set; }
    public Action? FocusLost { get; set; }
    public bool CanControl { get; set; }

    public void SetFrame(BitmapImage image, int width, int height)
    {
        _frameWidth = width;
        _frameHeight = height;
        _image.Source = image;
    }

    public void Clear()
    {
        _image.Source = null;
        _frameWidth = _frameHeight = 0;
    }

    // ---- mouse ----

    private bool TryNormalise(PointerRoutedEventArgs e, out ushort x, out ushort y)
    {
        x = y = 0;
        if (_frameWidth <= 0 || _frameHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return false;
        var scale = Math.Min(ActualWidth / _frameWidth, ActualHeight / _frameHeight);
        var w = _frameWidth * scale;
        var h = _frameHeight * scale;
        var left = (ActualWidth - w) / 2;
        var top = (ActualHeight - h) / 2;
        var p = e.GetCurrentPoint(this).Position;
        x = (ushort)Math.Round(Math.Clamp((p.X - left) / w, 0, 1) * 65535);
        y = (ushort)Math.Round(Math.Clamp((p.Y - top) / h, 0, 1) * 65535);
        return true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (CanControl && TryNormalise(e, out var x, out var y)) Input?.Invoke(InputEvent.Move(x, y));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        if (!CanControl || !TryNormalise(e, out var x, out var y)) return;
        var props = e.GetCurrentPoint(this).Properties;
        CapturePointer(e.Pointer);
        if (Button(props, pressed: true) is { } b) Input?.Invoke(InputEvent.Down(b, x, y));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (CanControl && TryNormalise(e, out var x, out var y) && Button(e.GetCurrentPoint(this).Properties, pressed: false) is { } b)
            Input?.Invoke(InputEvent.Up(b, x, y));
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // WinUI reports the button that changed through the update kind, so the same code serves press and release.
    private static HostButton? Button(PointerPointProperties p, bool pressed) => p.PointerUpdateKind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => HostButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => HostButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => HostButton.Middle,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => HostButton.X1,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => HostButton.X2,
        _ => null,
    };

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (CanControl && TryNormalise(e, out var x, out var y))
        {
            var props = e.GetCurrentPoint(this).Properties;
            Input?.Invoke(InputEvent.Scroll((short)Math.Clamp(props.MouseWheelDelta, short.MinValue, short.MaxValue), x, y, props.IsHorizontalMouseWheel));
        }
        e.Handled = true;
    }

    // ---- keyboard ----

    private void SendKey(KeyRoutedEventArgs e, bool down)
    {
        if (!CanControl) return;
        var vk = (ushort)e.Key;
        if (vk == 0) return;
        var scan = (ushort)MapVirtualKeyW(vk, 0);
        Input?.Invoke(InputEvent.Key(down, vk, scan, IsExtended(e.Key)));
        e.Handled = true; // the other PC, not this window, should see Tab, Alt, F10 and the arrow keys
    }

    private static bool IsExtended(VirtualKey key) => key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right
        or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Insert or VirtualKey.Delete
        or VirtualKey.RightControl or VirtualKey.RightMenu or VirtualKey.Divide or VirtualKey.NumberKeyLock or VirtualKey.Snapshot
        or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.Application;
}
