using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Input;
using Remotely.HostCore;
using Remotely.ViewerCore.Mapping;
using Windows.Foundation;
using Windows.System;
using HostButton = Remotely.HostCore.MouseButton;

namespace Remotely.App;

/// <summary>
/// Shows the remote screen (fit, 100% with zoom and pan, or fill) and turns local mouse and keyboard input into <see cref="InputEvent"/>s.
/// All geometry lives in <see cref="ScreenMapping"/> (unit-tested); this class only reads sizes and pointer positions and applies the result.
/// Positions are sent normalised (0..65535 across the remote desktop), so no knowledge of the other PC's resolution is needed.
/// WinUI's <see cref="VirtualKey"/> values are the Win32 virtual-key codes, so keys pass straight through.
/// </summary>
public sealed class RemoteScreen : Grid
{
    private const double EdgeMargin = 24;   // DIPs from the window edge where a picture larger than the window scrolls on its own
    private const double EdgeStep = 10;

    private readonly Canvas _canvas = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Polygon _cursor = new()
    {
        Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
        Stroke = new SolidColorBrush(Microsoft.UI.Colors.Black),
        StrokeThickness = 1,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };
    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private int _frameWidth, _frameHeight;
    private ScaleMode _mode = ScaleMode.Fit;
    private double _zoom = 1;
    private PointD _pan;
    private PixelRect? _monitor, _desktop;
    private IRemoteCursorSource? _cursorSource;
    private PointD? _lastPointer;
    private bool _captureKeyboard = true;
    private bool _rootHooked;

    public RemoteScreen()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        foreach (var point in new[] { new Point(0, 0), new Point(0, 16), new Point(4, 12), new Point(7, 19), new Point(9, 18), new Point(6, 11), new Point(11, 11) })
            _cursor.Points.Add(point);
        _canvas.Children.Add(_image);
        _canvas.Children.Add(_cursor);
        Children.Add(_canvas);
        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnWheel;
        PointerEntered += (_, _) => _edgeTimer.Start();
        PointerExited += (_, _) => { _edgeTimer.Stop(); _lastPointer = null; };
        KeyDown += (_, e) => SendKey(e, true);
        KeyUp += (_, e) => SendKey(e, false);
        LostFocus += (_, _) => FocusLost?.Invoke(); // let go of everything so no key or button is left stuck down on the other PC
        SizeChanged += (_, _) => Relayout();
        Loaded += OnLoaded;
        _edgeTimer.Tick += OnEdgeTick;
    }

    public Action<InputEvent>? Input { get; set; }
    public Action? FocusLost { get; set; }
    public bool CanControl { get; set; }

    /// <summary>Raised when the mode, zoom or pan changed (the window refreshes its labels).</summary>
    public event Action? ViewChanged;

    /// <summary>Whether key presses go to the other PC. When off, keys stay with this window (Tab moves between buttons, shortcuts work locally).</summary>
    public bool CaptureKeyboard
    {
        get => _captureKeyboard;
        set
        {
            if (_captureKeyboard == value) return;
            _captureKeyboard = value;
            if (!value) FocusLost?.Invoke(); // release anything still held on the other PC
        }
    }

    public ScaleMode Mode => _mode;
    public double Zoom => _zoom;
    public int FrameWidth => _frameWidth;
    public int FrameHeight => _frameHeight;

    /// <summary>Selecting a mode resets zoom and pan.</summary>
    public void SetMode(ScaleMode mode)
    {
        _mode = mode;
        _zoom = 1;
        _pan = default;
        Relayout();
    }

    /// <summary>The part of the host desktop that is shown, and the host's whole virtual desktop. Null: the picture is the whole desktop.</summary>
    public void SetMonitor(PixelRect? monitor, PixelRect? desktop)
    {
        _monitor = monitor;
        _desktop = desktop;
        Relayout();
    }

    public IRemoteCursorSource? CursorSource
    {
        get => _cursorSource;
        set
        {
            if (_cursorSource is not null) _cursorSource.Changed -= OnCursorChanged;
            _cursorSource = value;
            if (value is not null) value.Changed += OnCursorChanged;
            UpdateCursor();
        }
    }

    public void SetFrame(ImageSource image, int width, int height)
    {
        var sizeChanged = width != _frameWidth || height != _frameHeight;
        _frameWidth = width;
        _frameHeight = height;
        _image.Source = image;
        if (sizeChanged) Relayout();
    }

    public void Clear()
    {
        _image.Source = null;
        _frameWidth = _frameHeight = 0;
        Relayout();
    }

    // ---- layout ----

    private double RasterScale
    {
        get
        {
            var scale = XamlRoot?.RasterizationScale ?? 1.0;
            return scale > 0 ? scale : 1.0;
        }
    }

    private RectD CurrentRect => ScreenMapping.ImageRect(_mode, ActualWidth, ActualHeight, _frameWidth, _frameHeight, RasterScale, _zoom, _pan);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_rootHooked || XamlRoot is null) return;
        _rootHooked = true;
        XamlRoot.Changed += (_, _) => Relayout();   // moving the window to a monitor with another scale changes how big 100% is
        Relayout();
    }

    private void Relayout()
    {
        if (ActualWidth > 0 && ActualHeight > 0) Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
        var rect = CurrentRect;
        _pan = ScreenMapping.ClampPan(_pan, ActualWidth, ActualHeight, rect.Width, rect.Height);
        if (rect.Width <= 0)
        {
            _image.Visibility = Visibility.Collapsed;
        }
        else
        {
            _image.Visibility = Visibility.Visible;
            _image.Width = rect.Width;
            _image.Height = rect.Height;
            Canvas.SetLeft(_image, rect.X);
            Canvas.SetTop(_image, rect.Y);
        }
        UpdateCursor();
        ViewChanged?.Invoke();
    }

    // ---- remote cursor (drawn locally) ----

    private void OnCursorChanged() => DispatcherQueue.TryEnqueue(UpdateCursor);

    private void UpdateCursor()
    {
        if (_cursorSource?.Current is { } c && ScreenMapping.TryMapFromDesktop(c.X, c.Y, CurrentRect, _monitor, _desktop, out var p))
        {
            Canvas.SetLeft(_cursor, p.X);
            Canvas.SetTop(_cursor, p.Y);
            _cursor.Visibility = Visibility.Visible;
        }
        else
        {
            _cursor.Visibility = Visibility.Collapsed;
        }
    }

    // ---- mouse ----

    private bool TryNormalise(PointD p, bool clampOutside, out ushort x, out ushort y) =>
        ScreenMapping.TryMapToDesktop(p, CurrentRect, _monitor, _desktop, clampOutside, out x, out y);

    private PointD PositionOf(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        return new PointD(p.X, p.Y);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = PositionOf(e);
        _lastPointer = p;
        if (CanControl && TryNormalise(p, true, out var x, out var y)) Input?.Invoke(InputEvent.Move(x, y));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        var p = PositionOf(e);
        if (!CanControl || !TryNormalise(p, false, out var x, out var y)) return;   // a click in the bars around the picture is not a click on the other PC
        var props = e.GetCurrentPoint(this).Properties;
        CapturePointer(e.Pointer);
        if (Button(props) is { } b) Input?.Invoke(InputEvent.Down(b, x, y));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (CanControl && TryNormalise(PositionOf(e), true, out var x, out var y) && Button(e.GetCurrentPoint(this).Properties) is { } b)
            Input?.Invoke(InputEvent.Up(b, x, y));
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // WinUI reports the button that changed through the update kind, so the same code serves press and release.
    private static HostButton? Button(PointerPointProperties p) => p.PointerUpdateKind switch
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
        var p = PositionOf(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (_mode == ScaleMode.Actual && !props.IsHorizontalMouseWheel && e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) && _frameWidth > 0)
        {
            var (zoom, pan) = ScreenMapping.ZoomAt(ScreenMapping.ZoomStep(_zoom, props.MouseWheelDelta), p, ActualWidth, ActualHeight, _frameWidth, _frameHeight, RasterScale, _zoom, _pan);
            _zoom = zoom;
            _pan = pan;
            Relayout();
            e.Handled = true;
            return;
        }
        if (CanControl && TryNormalise(p, true, out var x, out var y))
            Input?.Invoke(InputEvent.Scroll((short)Math.Clamp(props.MouseWheelDelta, short.MinValue, short.MaxValue), x, y, props.IsHorizontalMouseWheel));
        e.Handled = true;
    }

    /// <summary>A picture larger than the window scrolls towards the edge the pointer is pushed against.</summary>
    private void OnEdgeTick(object? sender, object e)
    {
        if (_lastPointer is not { } p || _frameWidth <= 0) return;
        var rect = CurrentRect;
        double dx = 0, dy = 0;
        if (rect.Width > ActualWidth + 0.5) dx = p.X < EdgeMargin ? EdgeStep : p.X > ActualWidth - EdgeMargin ? -EdgeStep : 0;
        if (rect.Height > ActualHeight + 0.5) dy = p.Y < EdgeMargin ? EdgeStep : p.Y > ActualHeight - EdgeMargin ? -EdgeStep : 0;
        if (dx == 0 && dy == 0) return;
        _pan = new PointD(_pan.X + dx, _pan.Y + dy);
        Relayout();
        if (CanControl && TryNormalise(p, true, out var x, out var y)) Input?.Invoke(InputEvent.Move(x, y)); // the remote pixel under a still pointer changed
    }

    // ---- keyboard ----

    private void SendKey(KeyRoutedEventArgs e, bool down)
    {
        if (!CanControl || !_captureKeyboard || !Session.KeyTranslator.TryTranslate(e.Key, out var vk, out var scan, out var extended)) return;
        Input?.Invoke(InputEvent.Key(down, vk, scan, extended));
        e.Handled = true; // the other PC, not this window, should see Tab, Alt, F10 and the arrow keys
    }
}
