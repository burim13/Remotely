using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Remotely.UI.Controls;

/// <summary>Draws one icon from <see cref="Icons"/> in the control's <c>Foreground</c>. Decorative: hidden from screen readers (put the meaning in the text next to it).</summary>
public sealed class PorticoIcon : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(PorticoIcon), new PropertyMetadata("", (d, _) => ((PorticoIcon)d).Rebuild()));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(PorticoIcon), new PropertyMetadata(16.0, (d, _) => ((PorticoIcon)d).Resize()));

    private readonly Viewbox _box = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Width = 16, Height = 16 };
    private readonly Canvas _canvas = new() { Width = 24, Height = 24 };
    private readonly Path _path = new();

    public PorticoIcon()
    {
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        _canvas.Children.Add(_path);
        _box.Child = _canvas;
        Content = _box;
        _path.Fill = Foreground;
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => _path.Fill = Foreground);
    }

    /// <summary>A key of <see cref="Icons.Paths"/>, for example <c>Monitor</c>.</summary>
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    /// <summary>Width and height in effective pixels (the icon is drawn on a 24 x 24 grid and scaled).</summary>
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private void Resize() { _box.Width = Size; _box.Height = Size; }

    private void Rebuild()
    {
        if (!Icons.Paths.TryGetValue(Glyph ?? "", out var data)) { _path.Data = null; return; }
        var holder = (Path)XamlReader.Load($"<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"{data}\" />");
        _path.Data = holder.Data;
    }
}
