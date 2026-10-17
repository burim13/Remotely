using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Remotely.HostCore;
using Windows.Foundation;

namespace Remotely.App.Session.Input;

/// <summary>
/// Touch, pen and wheel input for the remote screen. The mouse path stays in the session window; call these first and skip the mouse handling when
/// they return true. Touch and pen only leave this class when the host advertises the extension (the session drops them otherwise, so a touch screen
/// still works as a mouse through the normal pointer events).
/// </summary>
public sealed class PointerInput(Action<InputEvent> emit, Func<Point, (ushort X, ushort Y)?> normalise, UIElement relativeTo)
{
    private bool _penInRange;

    /// <summary>True when the extended touch and pen events can be sent. Set from <c>ViewerSession.SupportsExtendedInput</c>.</summary>
    public bool Enabled { get; set; }

    public bool OnPressed(PointerRoutedEventArgs e) => Handle(e, down: true, up: false);

    public bool OnMoved(PointerRoutedEventArgs e) => Handle(e, down: false, up: false);

    public bool OnReleased(PointerRoutedEventArgs e) => Handle(e, down: false, up: true);

    /// <summary>Pen leaving the screen's range (PointerExited).</summary>
    public bool OnExited(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(relativeTo);
        if (!Enabled || point.PointerDeviceType != PointerDeviceType.Pen || !_penInRange || normalise(point.Position) is not { } at) return false;
        _penInRange = false;
        emit(InputEvent.Pen(PenPhase.Leave, at.X, at.Y, 0, 0, 0));
        return true;
    }

    /// <summary>A canceled pointer (touch palm rejection, window lost the pointer): release it on the other PC too.</summary>
    public bool OnCanceled(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(relativeTo);
        if (!Enabled || normalise(point.Position) is not { } at) return false;
        if (point.PointerDeviceType == PointerDeviceType.Touch) { emit(InputEvent.Touch(TouchPhase.Cancel, (ushort)point.PointerId, at.X, at.Y, 0)); return true; }
        if (point.PointerDeviceType == PointerDeviceType.Pen) { emit(InputEvent.Pen(PenPhase.Cancel, at.X, at.Y, 0, 0, 0)); return true; }
        return false;
    }

    /// <summary>
    /// Wheel, vertical or horizontal, at full resolution: a precision touchpad reports small steps, and the whole amount is sent (split into several
    /// events when it does not fit one) instead of being clamped.
    /// </summary>
    public void OnWheel(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(relativeTo);
        if (normalise(point.Position) is not { } at) return;
        foreach (var scroll in InputEvent.ScrollMany(point.Properties.MouseWheelDelta, at.X, at.Y, point.Properties.IsHorizontalMouseWheel)) emit(scroll);
    }

    private bool Handle(PointerRoutedEventArgs e, bool down, bool up)
    {
        var point = e.GetCurrentPoint(relativeTo);
        var device = point.PointerDeviceType;
        if (!Enabled || device is not (PointerDeviceType.Touch or PointerDeviceType.Pen) || normalise(point.Position) is not { } at) return false;
        var pressure = (int)Math.Round(point.Properties.Pressure * InputEvent.MaxPressure);
        if (device == PointerDeviceType.Touch)
        {
            var phase = down ? TouchPhase.Down : up ? TouchPhase.Up : TouchPhase.Move;
            if (phase == TouchPhase.Move && !point.IsInContact) return true;
            emit(InputEvent.Touch(phase, (ushort)point.PointerId, at.X, at.Y, pressure));
            return true;
        }
        var props = point.Properties;
        var penPhase = down ? PenPhase.Down : up ? PenPhase.Up : point.IsInContact ? PenPhase.Move : PenPhase.Hover;
        _penInRange = true;
        emit(InputEvent.Pen(penPhase, at.X, at.Y, up ? 0 : pressure, (int)Math.Round(props.XTilt), (int)Math.Round(props.YTilt),
            barrel: props.IsBarrelButtonPressed, eraser: props.IsEraser, inverted: props.IsInverted));
        return true;
    }
}
