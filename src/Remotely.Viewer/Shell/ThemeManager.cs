using System.Windows;
using Microsoft.Win32;

namespace Remotely.Viewer.Shell;

/// <summary>Dark or light, following the Windows "Choose your app mode" setting unless the user overrides it; Windows high contrast replaces both. Same behaviour as Portico's ThemeManager.</summary>
internal sealed class ThemeManager : IDisposable
{
    private readonly ViewerSettings _settings;

    public ThemeManager(ViewerSettings settings)
    {
        _settings = settings;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Apply();
    }

    public void Apply()
    {
        var highContrast = SystemParameters.HighContrast;
        var dark = _settings.Theme switch { "dark" => true, "light" => false, _ => WindowsAppsUseDark() };
        var source = highContrast ? "Themes/HighContrast.xaml" : dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
    }

    /// <summary>HKCU\...\Themes\Personalize\AppsUseLightTheme: 0 = dark, 1 = light.</summary>
    private static bool WindowsAppsUseDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { return true; }
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color)
            Application.Current.Dispatcher.BeginInvoke(Apply);
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
}
