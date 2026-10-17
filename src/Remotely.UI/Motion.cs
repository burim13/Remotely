using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Remotely.UI;

/// <summary>Motion that respects the Windows "Show animations" setting. Durations come from the <c>Portico.Motion.*</c> tokens; when animations are off every duration is zero.</summary>
public static class Motion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    public static bool Enabled
    {
        get
        {
            var on = 1;
            try { return !SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref on, 0) || on != 0; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return true; }
        }
    }

    public static TimeSpan Fast => Get("Portico.Motion.Fast", 120);
    public static TimeSpan Normal => Get("Portico.Motion.Normal", 200);
    public static TimeSpan Slow => Get("Portico.Motion.Slow", 320);

    private static TimeSpan Get(string key, double fallbackMs)
    {
        if (!Enabled) return TimeSpan.Zero;
        var ms = Application.Current.Resources.TryGetValue(key, out var v) && v is double d ? d : fallbackMs;
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>Fades an element in. When animations are off the element is simply shown.</summary>
    public static void FadeIn(UIElement element)
    {
        var duration = Normal;
        if (duration == TimeSpan.Zero) { element.Opacity = 1; return; }
        element.Opacity = 0;
        var animation = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var board = new Storyboard();
        board.Children.Add(animation);
        board.Begin();
    }
}
