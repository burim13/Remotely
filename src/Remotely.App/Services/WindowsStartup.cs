using Microsoft.Win32;
using Remotely.App.Shell;

namespace Remotely.App.Services;

/// <summary>Start with Windows: one value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run (per user, no administrator rights needed).</summary>
internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Remotely";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string { Length: > 0 };
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }

    /// <summary>Returns false when Windows (policy, for example) refused the change.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, StartupCommand.Build(exe));
            }
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            App.LogCrash(ex);
            return false;
        }
    }
}
