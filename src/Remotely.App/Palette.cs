using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Remotely.App;

/// <summary>Portico colours needed from code (XAML uses theme resources). Chosen by the element's actual theme so a per-window dark or light choice is respected.</summary>
internal static class Palette
{
    private static bool Dark(FrameworkElement e) => e.ActualTheme != ElementTheme.Light;
    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));

    public static Brush Connected(FrameworkElement e) => Dark(e) ? Rgb(0x6F, 0xCF, 0x97) : Rgb(0x1F, 0x7A, 0x4D);
    public static Brush Error(FrameworkElement e) => Dark(e) ? Rgb(0xFF, 0x9B, 0x8A) : Rgb(0xB3, 0x26, 0x1E);
    public static Brush AccentText(FrameworkElement e) => Dark(e) ? Rgb(0xF7, 0xBE, 0x55) : Rgb(0x8A, 0x5A, 0x00);
    public static Brush Offline(FrameworkElement e) => Dark(e) ? Rgb(0x55, 0x5F, 0x68) : Rgb(0xC5, 0xCC, 0xD2);
}
