using Microsoft.Extensions.Hosting;
using Remotely.Host.Unattended;
using Remotely.HostCore;

namespace Remotely.Host.Service;

/// <summary>The Windows service: keeps the capture helper alive, stays connected to the coordinator, and answers the signed-in user's app on the local pipe.</summary>
internal sealed class HostService(FileLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var identity = HostPaths.LoadOrCreateIdentity();
            log.Write($"Remotely.Host (service) starting. Device id: {identity.DeviceId}");
            Directory.CreateDirectory(HostPaths.ReceiveDir);
            Directory.CreateDirectory(HostPaths.ShareDir);

            var link = new HelperLink();
            var supervisor = new HelperSupervisor(link, log.Write);

            // Unattended access (docs/unattended.md): keep-awake while somebody views the PC, and same-network token sharing for the app of the signed-in user.
            var settings = new CachedSettings(new UnattendedSettingsStore(HostPaths.UnattendedFile));
            using var power = new PowerRequest(log.Write);
            var activity = new SessionActivity(power, () => settings.Current.KeepAwake);
            await using var lan = new ServiceLan(new ServiceLanOptions
            {
                Services = () => HostRuntime.ForService(link, activity, _ => true, _ => true),   // the token (and the question) already decided who connects
                Identity = identity,
                Settings = settings,
                Relay = new ConsentRelay(),
                Activity = activity,
                HelperAttached = () => link.IsAttached,
                Log = log.Write,
            });
            var server = new UnattendedServer(lan, new ConsoleUser().Current, WindowsPipe.Create, new WindowsPeerCheck(), log.Write);

            await Task.WhenAll(
                supervisor.RunAsync(stoppingToken),
                HostConnection.RunForeverAsync(HostPaths.ConfigFile, identity,
                    (trusted, control) => HostRuntime.ForService(link, activity, trusted, control), log.Write, stoppingToken),
                Guarded("The unattended-access pipe", () => server.RunAsync(stoppingToken)),
                Guarded("The unattended-access upkeep", () => UpkeepAsync(activity, lan, stoppingToken))).ConfigureAwait(false);
            activity.Shutdown();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Write("Fatal: " + ex);
            throw;
        }
        log.Write("Remotely.Host stopped.");
    }

    /// <summary>The unattended-access features are additions: whatever goes wrong in them is logged and never takes the service, the helper or the server connection down.</summary>
    private async Task Guarded(string what, Func<Task> run)
    {
        try { await run().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.Write($"{what} stopped working and will not run until the service restarts: {ex}"); }
    }

    private static async Task UpkeepAsync(SessionActivity activity, ServiceLan lan, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                activity.Tick();
                await lan.EnforceSettingsAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
}
