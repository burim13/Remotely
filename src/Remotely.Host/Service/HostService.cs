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
            log.Write($"Remotely.Host starting. Device id: {identity.DeviceId}");

            var source = new PipeFrameSource();
            var supervisor = new HelperSupervisor(source, log.Write);
            await Task.WhenAll(
                supervisor.RunAsync(stoppingToken),
                HostConnection.RunForeverAsync(HostPaths.ConfigFile, identity, source, log.Write, stoppingToken)).ConfigureAwait(false);
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
