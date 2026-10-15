using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.Host.Helper;
using Remotely.HostCore;

namespace Remotely.App;

/// <summary>
/// "Share this PC" on the local network: runs the token listener and, for the one PC that enters the token, captures this PC's screen and
/// injects its input. The app runs in the signed-in user's session, so the lock screen and Ctrl+Alt+Del are not available in this mode.
/// </summary>
public sealed class ShareService : IAsyncDisposable
{
    private readonly DesktopThread _desktop = new();
    private readonly DeviceIdentity _identity = DeviceIdentity.Generate();
    private readonly CancellationTokenSource _cts = new();
    private LanHost? _host;

    public LanHost? Host => _host;
    public bool IsSharing => _host is not null;

    /// <summary>Starts sharing. Returns an error sentence for the person, or null when sharing.</summary>
    public string? Start(Action<string> log)
    {
        if (_host is not null) return null;
        var received = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Remotely");
        var services = new HostServices(new DesktopCapturer(_desktop, log))
        {
            Input = new InputInjector(_desktop),
            Clipboard = new ClipboardAccess(),
            ReceiveDirectory = received,
            IsTrusted = _ => true,           // the token already proved who is connecting
            CanControl = _ => true,
        };
        var agent = new HostAgent(_identity, services, log);
        var host = new LanHost(new LanHostOptions(), (channel, name, ct) => agent.ServeChannelAsync(channel, name, control: true, ct), log);
        try { host.Start(); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _ = host.DisposeAsync();
            return "Remotely could not start listening on this network: " + ex.Message;
        }
        _host = host;
        return null;
    }

    public async Task StopAsync()
    {
        var host = _host;
        _host = null;
        if (host is not null) await host.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Cancel();
        _desktop.Dispose();
        _identity.Dispose();
    }
}
