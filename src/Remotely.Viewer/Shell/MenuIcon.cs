using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Remotely.Viewer.Shell;

/// <summary>
/// The icon slot of every Portico menu item: a Geometry (an Icon.* resource) becomes a 16-px stroke glyph in the
/// secondary color; a UIElement (a category's color dot) is shown as it is; nothing keeps the slot empty so the
/// labels of a menu still line up.
/// </summary>
public sealed class MenuIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Geometry geometry)
        {
            var path = new System.Windows.Shapes.Path { Data = geometry, Width = 24, Height = 24, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            path.SetResourceReference(Shape.StrokeProperty, "Brush.Secondary");
            return new Viewbox { Width = 16, Height = 16, Child = path };
        }
        if (value is UIElement element) return element;
        return new Border { Width = 16, Height = 16 };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
