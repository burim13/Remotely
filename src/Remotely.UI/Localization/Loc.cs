using Microsoft.Windows.ApplicationModel.Resources;

namespace Remotely.UI.Localization;

/// <summary>Looks up a user-visible string by key from the app's <c>Strings/&lt;language&gt;/Resources.resw</c>. Falls back to the key itself, so a missing string is visible but never a crash.</summary>
public static class Loc
{
    private static ResourceLoader? _loader;

    public static string Get(string key)
    {
        try
        {
            _loader ??= new ResourceLoader();
            var value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or FileNotFoundException)
        {
            return key;
        }
    }
}
