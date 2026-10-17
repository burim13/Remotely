using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Remotely.App;

/// <summary>Portico colours needed from code (XAML uses theme resources). Chosen by the element's actual theme so a per-window dark or light choice is respected.</summary>
internal static class Palette
{
    private static bool Dark(FrameworkElement e) => e.ActualTheme != ElementTheme.Light;
    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));

    /// <summary>In a Windows high-contrast theme every colour below is replaced by the user's text colour, so status never depends on a fixed hue.</summary>
    private static bool HighContrast => new AccessibilitySettings().HighContrast;
    private static SolidColorBrush SystemText() => new(new UISettings().GetColorValue(UIColorType.Foreground));

    public static Brush Connected(FrameworkElement e) => HighContrast ? SystemText() : Dark(e) ? Rgb(0x6F, 0xCF, 0x97) : Rgb(0x1F, 0x7A, 0x4D);
    public static Brush Error(FrameworkElement e) => HighContrast ? SystemText() : Dark(e) ? Rgb(0xFF, 0x9B, 0x8A) : Rgb(0xB3, 0x26, 0x1E);
    public static Brush AccentText(FrameworkElement e) => HighContrast ? SystemText() : Dark(e) ? Rgb(0xF7, 0xBE, 0x55) : Rgb(0x8A, 0x5A, 0x00);
    public static Brush Offline(FrameworkElement e) => HighContrast ? SystemText() : Dark(e) ? Rgb(0x55, 0x5F, 0x68) : Rgb(0xC5, 0xCC, 0xD2);
}
