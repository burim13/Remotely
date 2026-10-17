using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Remotely.App.Views.Share;

/// <summary>
/// Windows notifications ("toasts") for connect, disconnect and blocked connections. Showing one can fail (no notification service on a server edition, notifications
/// switched off, a registration problem): then nothing is shown and the app carries on, because the banner and the question window are what protect the person.
/// </summary>
public static class ShareToasts
{
    private static bool _registered;
    private static bool _broken;

    public static void Show(string title, string body)
    {
        if (_broken) return;
        try
        {
            if (!_registered)
            {
                _registered = true;
                AppNotificationManager.Default.Register();
            }
            AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException or FileNotFoundException)
        {
            _broken = true;
            App.LogCrash(ex);
        }
    }
}
