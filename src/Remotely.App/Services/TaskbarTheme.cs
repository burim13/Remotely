using Microsoft.Win32;
using Remotely.App.Shell;
using Windows.UI.ViewManagement;

namespace Remotely.App.Services;

/// <summary>Whether the Windows taskbar is light or dark (HKCU ... Themes\Personalize\SystemUsesLightTheme) and a notification when the colours change.</summary>
internal static class TaskbarTheme
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static UISettings? _settings;   // must stay referenced or the event stops

    public static int? ReadSystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue("SystemUsesLightTheme") is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>Full path of the tray icon for the current taskbar theme; falls back to the app icon if the variant is missing.</summary>
    public static string TrayIconPath()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        var themed = Path.Combine(assets, TrayTheme.IconFile(ReadSystemUsesLightTheme()));
        return File.Exists(themed) ? themed : Path.Combine(assets, "remotely.ico");
    }

    /// <summary>Calls <paramref name="changed"/> (on a worker thread) when Windows reports a colour change. UISettings.ColorValuesChanged is raised for theme switches; whether it also fires when only the taskbar mode changes is not verified.</summary>
    public static void Watch(Action changed)
    {
        try
        {
            _settings = new UISettings();
            _settings.ColorValuesChanged += (_, _) => changed();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            App.LogCrash(ex);
        }
    }
}
