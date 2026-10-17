using Remotely.App.Services;
using Remotely.UI.Localization;

namespace Remotely.App.Shell;

/// <summary>The tray icon's menu: Open, Share on/off, the two window options, Quit.</summary>
internal static class TrayMenu
{
    private const int Open = 1, Share = 2, CloseToTray = 3, Startup = 4, Quit = 5;

    public static TrayIcon Create() => new(
        Path.Combine(AppContext.BaseDirectory, "Assets", "remotely.ico"),
        Loc.Get("App_Name"),
        Items,
        OnCommand,
        () => App.Main?.ShowAndFocus());

    private static IReadOnlyList<TrayMenuItem> Items() =>
    [
        new(Open, Loc.Get("Tray_Open")),
        new(Share, Loc.Get("Tray_Share"), Checked: App.Share.IsSharing),
        new(0, "", Separator: true),
        new(CloseToTray, Loc.Get("Tray_CloseToTray"), Checked: App.Settings.CloseToTray),
        new(Startup, Loc.Get("Tray_StartWithWindows"), Checked: WindowsStartup.IsEnabled),
        new(0, "", Separator: true),
        new(Quit, Loc.Get("Tray_Quit")),
    ];

    private static void OnCommand(int id)
    {
        switch (id)
        {
            case Open: App.Main?.ShowAndFocus(); break;
            case Share: _ = ToggleShareAsync(); break;
            case CloseToTray:
                App.Settings.CloseToTray = !App.Settings.CloseToTray;
                SaveSettings();
                break;
            case Startup:
                var enable = !WindowsStartup.IsEnabled;
                if (WindowsStartup.Set(enable)) { App.Settings.StartWithWindows = enable; SaveSettings(); }
                else App.Services.Toast(Loc.Get("App_Name"), Loc.Get("Startup_Failed"));
                break;
            case Quit: App.Main?.Quit(); break;
        }
    }

    private static async Task ToggleShareAsync()
    {
        try
        {
            if (App.Share.IsSharing) await App.Share.StopAsync();
            else if (App.Share.Start(_ => { }) is { } error) App.Services.Toast(Loc.Get("Tray_ShareFailed"), error);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            App.LogCrash(ex);
        }
        App.Services.RaiseShareStateChanged();
    }

    private static void SaveSettings()
    {
        try { App.Settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.LogCrash(ex); }
    }
}
