using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Remotely.App.Session.Input;
using Remotely.HostCore;
using Remotely.ViewerCore;
using Remotely.ViewerCore.Mapping;
using Windows.Foundation;
using Windows.System;
using HostButton = Remotely.HostCore.MouseButton;

namespace Remotely.App;

/// <summary>
/// Shows the remote screen (fit, 100% with zoom and pan, or fill) and turns local mouse, touch, pen and keyboard input into <see cref="InputEvent"/>s.
/// All geometry lives in <see cref="ScreenMapping"/> (unit-tested); keys, touch/pen and wheel go through the input classes in <c>Session/Input</c>.
/// Positions are sent normalised (0..65535 across the remote desktop), so no knowledge of the other PC's resolution is needed.
/// </summary>
public sealed class RemoteScreen : Grid
{
    private const double EdgeMargin = 24;   // DIPs from the window edge where a picture larger than the window scrolls on its own
    private const double EdgeStep = 10;
    private const int MaxCachedCursors = 64;

    private readonly Canvas _canvas = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Image _cursor = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Dictionary<ulong, (WriteableBitmap Bitmap, CursorShape Shape)> _shapes = [];
    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly KeyboardInput _keyboard;
    private readonly PointerInput _pointer;
    private int _frameWidth, _frameHeight;
    private ScaleMode _mode = ScaleMode.Fit;
    private double _zoom = 1;
    private PointD _pan;
    private PixelRect? _monitor, _desktop;
    private CursorState? _cursorState;
    private PointD? _lastPointer;
    private bool _captureKeyboard = true;
    private bool _rootHooked;

    public RemoteScreen()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        _keyboard = new KeyboardInput(e => Input?.Invoke(e));
        _pointer = new PointerInput(e => Input?.Invoke(e), p => TryNormalise(new PointD(p.X, p.Y), true, out var x, out var y) ? (x, y) : null, this);
        _canvas.Children.Add(_image);
        _canvas.Children.Add(_cursor);
        Children.Add(_canvas);
        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerCanceled += (_, e) => { if (CanControl) _pointer.OnCanceled(e); };
        PointerWheelChanged += OnWheel;
        PointerEntered += (_, _) => _edgeTimer.Start();
        PointerExited += (_, e) => { _edgeTimer.Stop(); _lastPointer = null; if (CanControl) _pointer.OnExited(e); };
        KeyDown += (_, e) => { if (CanControl && _captureKeyboard && _keyboard.OnKeyDown(e)) e.Handled = true; };   // the other PC, not this window, should see Tab, Alt, F10 and the arrow keys
        KeyUp += (_, e) => { if (CanControl && _captureKeyboard && _keyboard.OnKeyUp(e)) e.Handled = true; };
        CharacterReceived += (_, e) => { if (CanControl && _captureKeyboard) _keyboard.OnCharacter(e); };
        LostFocus += (_, _) => ReleaseKeys();
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
            if (!value) ReleaseKeys();
        }
    }

    public ScaleMode Mode => _mode;
    public double Zoom => _zoom;
    public int FrameWidth => _frameWidth;
    public int FrameHeight => _frameHeight;

    /// <summary>Touch, pen and typed text can be sent (the host advertised them).</summary>
    public bool ExtendedInput { get => _pointer.Enabled; set => _pointer.Enabled = value; }

    /// <summary>Call when the session opens and whenever the host reports its keyboard layout.</summary>
    public void ConfigureKeyboard(KeyboardModeSetting setting, uint hostLayout, bool hostSupportsText) => _keyboard.Configure(setting, hostLayout, hostSupportsText);

    /// <summary>Lets go of every key and button on both ends, so nothing stays pressed on the other PC.</summary>
    public void ReleaseKeys()
    {
        _keyboard.ReleaseAll();
        FocusLost?.Invoke();
    }

    /// <summary>Selecting a mode resets zoom and pan.</summary>
    public void SetMode(ScaleMode mode)
    {
        _mode = mode;
        _zoom = 1;
        _pan = default;
        Relayout();
    }

    /// <summary>The part of the host desktop that is shown, and the host's whole virtual desktop. Null monitor: the picture is the whole desktop.</summary>
    public void SetMonitor(PixelRect? monitor, PixelRect? desktop)
    {
        _monitor = monitor;
        _desktop = desktop;
        Relayout();
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

    /// <summary>Remembers a pointer image the host sent (cached by id; <see cref="SetCursorState"/> says which one is current). UI thread.</summary>
    public void AddCursorShape(CursorShape shape)
    {
        if (shape.Width < 1 || shape.Height < 1 || shape.Bgra.Length != shape.Width * shape.Height * 4) return;
        if (_shapes.Count >= MaxCachedCursors) _shapes.Clear();
        var bitmap = new WriteableBitmap(shape.Width, shape.Height);
        var pixels = new byte[shape.Bgra.Length];
        for (var i = 0; i < pixels.Length; i += 4)   // the host sends straight alpha; the bitmap wants it premultiplied
        {
            var a = shape.Bgra[i + 3];
            pixels[i] = (byte)(shape.Bgra[i] * a / 255);
            pixels[i + 1] = (byte)(shape.Bgra[i + 1] * a / 255);
            pixels[i + 2] = (byte)(shape.Bgra[i + 2] * a / 255);
            pixels[i + 3] = a;
        }
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        _shapes[shape.Id] = (bitmap, shape);
        UpdateCursor();
    }

    /// <summary>Where the host's pointer is (virtual-desktop pixels) and which image it has. UI thread.</summary>
    public void SetCursorState(CursorState state)
    {
        _cursorState = state;
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        var rect = CurrentRect;
        if (_cursorState is { Visible: true } c && _shapes.TryGetValue(c.ShapeId, out var entry) && _frameWidth > 0
            && (_monitor ?? _desktop) is { IsEmpty: false } shown
            && ScreenMapping.TryMapFromDesktopPixel(c.X, c.Y, rect, shown, out var at))
        {
            var scale = rect.Width / _frameWidth;   // the pointer is drawn as big as the picture it sits on
            _cursor.Source = entry.Bitmap;
            _cursor.Width = entry.Shape.Width * scale;
            _cursor.Height = entry.Shape.Height * scale;
            Canvas.SetLeft(_cursor, at.X - entry.Shape.HotspotX * scale);
            Canvas.SetTop(_cursor, at.Y - entry.Shape.HotspotY * scale);
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

    /// <summary>The pointer moved to <paramref name="p"/> (view DIPs): the path a mouse move takes, also used by the session self-test.</summary>
    internal void MovePointer(PointD p)
    {
        _lastPointer = p;
        if (CanControl && TryNormalise(p, true, out var x, out var y)) Input?.Invoke(InputEvent.Move(x, y));
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = PositionOf(e);
        _lastPointer = p;
        if (CanControl && _pointer.OnMoved(e)) return;
        MovePointer(p);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        if (!CanControl) return;
        if (_pointer.OnPressed(e)) { CapturePointer(e.Pointer); e.Handled = true; return; }
        if (!TryNormalise(PositionOf(e), false, out var x, out var y)) return;   // a click in the bars around the picture is not a click on the other PC
        CapturePointer(e.Pointer);
        if (Button(e.GetCurrentPoint(this).Properties) is { } b) Input?.Invoke(InputEvent.Down(b, x, y));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (CanControl)
        {
            if (!_pointer.OnReleased(e) && TryNormalise(PositionOf(e), true, out var x, out var y) && Button(e.GetCurrentPoint(this).Properties) is { } b)
                Input?.Invoke(InputEvent.Up(b, x, y));
        }
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
        var props = e.GetCurrentPoint(this).Properties;
        if (_mode == ScaleMode.Actual && !props.IsHorizontalMouseWheel && e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) && _frameWidth > 0)
        {
            var (zoom, pan) = ScreenMapping.ZoomAt(ScreenMapping.ZoomStep(_zoom, props.MouseWheelDelta), PositionOf(e), ActualWidth, ActualHeight, _frameWidth, _frameHeight, RasterScale, _zoom, _pan);
            _zoom = zoom;
            _pan = pan;
            Relayout();
        }
        else if (CanControl)
        {
            _pointer.OnWheel(e);
        }
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
}
