using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Remotely.HostCore;
using HostButton = Remotely.HostCore.MouseButton;

namespace Remotely.Viewer.Views;

/// <summary>
/// Draws the remote screen scaled to fit and turns local mouse and keyboard input into <see cref="InputEvent"/>s.
/// Positions are sent normalised (0..65535 across the remote desktop), so the viewer needs no knowledge of the host's resolution.
/// </summary>
public sealed class RemoteScreen : FrameworkElement
{
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);

    private ImageSource? _frame;
    private Rect _imageRect;

    public RemoteScreen()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        AllowDrop = true;
    }

    /// <summary>Where input goes. Null (or <see cref="CanControl"/> false) means view only.</summary>
    public Action<InputEvent>? Input { get; set; }
    public bool CanControl { get; set; }
    public Action? FocusLost { get; set; }

    public void SetFrame(ImageSource frame)
    {
        _frame = frame;
        InvalidateVisual();
    }

    public void Clear()
    {
        _frame = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle((Brush)TryFindResource("Brush.Well") ?? Brushes.Black, null, new Rect(RenderSize));
        if (_frame is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var scale = Math.Min(ActualWidth / _frame.Width, ActualHeight / _frame.Height);
        var w = _frame.Width * scale;
        var h = _frame.Height * scale;
        _imageRect = new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
        RenderOptions.SetBitmapScalingMode(this, scale < 1 ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        dc.DrawImage(_frame, _imageRect);
    }

    // ---- mouse ----

    private bool TryNormalise(Point p, out ushort x, out ushort y)
    {
        x = y = 0;
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0) return false;
        var nx = Math.Clamp((p.X - _imageRect.X) / _imageRect.Width, 0, 1);
        var ny = Math.Clamp((p.Y - _imageRect.Y) / _imageRect.Height, 0, 1);
        x = (ushort)Math.Round(nx * 65535);
        y = (ushort)Math.Round(ny * 65535);
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (CanControl && TryNormalise(e.GetPosition(this), out var x, out var y)) Input?.Invoke(InputEvent.Move(x, y));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (!CanControl || !TryNormalise(e.GetPosition(this), out var x, out var y)) return;
        CaptureMouse();
        Input?.Invoke(InputEvent.Down(Map(e.ChangedButton), x, y));
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (CanControl && TryNormalise(e.GetPosition(this), out var x, out var y)) Input?.Invoke(InputEvent.Up(Map(e.ChangedButton), x, y));
        if (Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released && Mouse.MiddleButton == MouseButtonState.Released)
            ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (CanControl && TryNormalise(e.GetPosition(this), out var x, out var y))
            Input?.Invoke(InputEvent.Scroll((short)Math.Clamp(e.Delta, short.MinValue, short.MaxValue), x, y));
        e.Handled = true;
    }

    private static HostButton Map(System.Windows.Input.MouseButton b) => b switch
    {
        System.Windows.Input.MouseButton.Right => HostButton.Right,
        System.Windows.Input.MouseButton.Middle => HostButton.Middle,
        System.Windows.Input.MouseButton.XButton1 => HostButton.X1,
        System.Windows.Input.MouseButton.XButton2 => HostButton.X2,
        _ => HostButton.Left,
    };

    // ---- keyboard ----

    protected override void OnPreviewKeyDown(KeyEventArgs e) => SendKey(e, down: true);
    protected override void OnPreviewKeyUp(KeyEventArgs e) => SendKey(e, down: false);

    private void SendKey(KeyEventArgs e, bool down)
    {
        if (!CanControl) return;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, Key.DeadCharProcessed => e.DeadCharProcessedKey, _ => e.Key };
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        var scan = (ushort)MapVirtualKeyW((uint)vk, 0);
        Input?.Invoke(InputEvent.Key(down, (ushort)vk, scan, IsExtended(key)));
        e.Handled = true; // the remote PC, not this window, should see Tab, Alt, F10 and the arrow keys
    }

    private static bool IsExtended(Key key) => key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown
        or Key.Insert or Key.Delete or Key.RightCtrl or Key.RightAlt or Key.Divide or Key.NumLock or Key.PrintScreen or Key.LWin or Key.RWin or Key.Apps;

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        FocusLost?.Invoke(); // let go of everything so no key or button is left stuck down on the remote PC
    }
}
