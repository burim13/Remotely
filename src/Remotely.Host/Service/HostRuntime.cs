using Remotely.Host.Helper;
using Remotely.Host.Unattended;
using Remotely.HostCore;

namespace Remotely.Host.Service;

/// <summary>Builds what a host offers, for each way of running.</summary>
internal static class HostRuntime
{
    /// <summary>Service mode: everything goes through the helper that runs in the user's session; Ctrl+Alt+Del is available.</summary>
    public static HostServices ForService(HelperLink link, SessionActivity activity, Func<string, bool> trusted, Func<string, bool> canControl) => new(new ActivityFrameSource(link, activity))
    {
        Input = link,
        Clipboard = link,
        SecureAttention = new SasSender(),
        ReceiveDirectory = HostPaths.ReceiveDir,
        ShareDirectory = HostPaths.ShareDir,
        IsTrusted = trusted,
        CanControl = canControl,
    };

    /// <summary>User-only mode: the host itself runs in the user's session, so it captures and injects directly. No lock screen, no Ctrl+Alt+Del.</summary>
    public static HostServices ForUser(DesktopThread desktop, SessionActivity activity, Func<string, bool> trusted, Func<string, bool> canControl, Action<string> log) => new(new ActivityFrameSource(new DesktopCapturer(desktop, log), activity))
    {
        Input = new InputInjector(desktop),
        Clipboard = new ClipboardAccess(),
        ReceiveDirectory = HostPaths.ReceiveDir,
        ShareDirectory = HostPaths.ShareDir,
        IsTrusted = trusted,
        CanControl = canControl,
    };
}
