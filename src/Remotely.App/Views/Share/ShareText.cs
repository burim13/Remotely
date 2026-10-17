using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Remotely.Core.Lan.Consent;
using Remotely.UI.Localization;
using Windows.Graphics;

namespace Remotely.App.Views.Share;

/// <summary>Turns state, codes and times into sentences from the string table (English, Albanian). Nothing here shows a literal.</summary>
internal static class ShareText
{
    public static string Format(string key, params object[] args) => string.Format(Loc.Get(key), args);

    public static string State(ShareSnapshot s) => s.State switch
    {
        ShareState.AwaitingConsent or ShareState.InSession => Format(s.ResourceKey, s.Controller ?? ""),
        ShareState.Error => Format(s.ResourceKey, s.ErrorText ?? ""),
        _ => Loc.Get(s.ResourceKey),
    };

    public static string Outcome(string outcome) => Loc.Get("History_Outcome_" + outcome);

    public static string Via(string via) => Loc.Get(via == "remembered" ? "History_Via_Remembered" : "History_Via_Token");

    public static string Reason(string? reason) => reason switch
    {
        SessionEndReasons.StoppedByHost => Loc.Get("History_Reason_StoppedByHost"),
        SessionEndReasons.SharingStopped => Loc.Get("History_Reason_SharingStopped"),
        SessionEndReasons.ControllerLeft => Loc.Get("History_Reason_ControllerLeft"),
        SessionEndReasons.AppClosed => Loc.Get("History_Reason_AppClosed"),
        _ => "",
    };

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1 ? Format("Duration_HoursMinutes", (int)span.TotalHours, span.Minutes)
            : span.TotalMinutes >= 1 ? Format("Duration_Minutes", (int)span.TotalMinutes)
            : Format("Duration_Seconds", (int)span.TotalSeconds);
    }

    public static string When(DateTimeOffset utc) => utc.ToLocalTime().ToString("g");
}

/// <summary>Size, position and always-on-top for the small windows of the sharing PC (the banner and the question).</summary>
internal static class ShareWindows
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    public static void PlaceTop(Window window, int widthDip, int heightDip, bool center)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(widthDip * scale), area.Width);
        var height = Math.Min((int)(heightDip * scale), area.Height);
        window.AppWindow.Resize(new SizeInt32(width, height));
        window.AppWindow.Move(new PointInt32(area.X + (area.Width - width) / 2, center ? area.Y + (area.Height - height) / 2 : area.Y + (int)(12 * scale)));
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }
}
