using Microsoft.Extensions.Hosting;
using Remotely.HostCore;

namespace Remotely.Host.Service;

/// <summary>The Windows service: keeps the capture helper alive and stays connected to the coordinator.</summary>
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
            await Task.WhenAll(
                supervisor.RunAsync(stoppingToken),
                HostConnection.RunForeverAsync(HostPaths.ConfigFile, identity,
                    (trusted, control) => HostRuntime.ForService(link, trusted, control), log.Write, stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Write("Fatal: " + ex);
            throw;
        }
        log.Write("Remotely.Host stopped.");
    }
}
