using Remotely.Client;
using Remotely.Core.Crypto;

namespace Remotely.HostCore;

/// <summary>Connect, pair (once), and serve; reconnects with backoff until cancelled. The config is re-read on every attempt and every viewer check, so edits apply without a restart.</summary>
public static class HostConnection
{
    public static async Task RunForeverAsync(string configPath, DeviceIdentity identity, IFrameSource source, Action<string> log, CancellationToken ct)
    {
        var agent = new HostAgent(identity, source, viewerId => HostConfig.Load(configPath).IsTrusted(viewerId), log);
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var config = HostConfig.Load(configPath);
                if (string.IsNullOrWhiteSpace(config.ServerUri))
                {
                    log("Not configured: run 'Remotely.Host setup ...'. Waiting.");
                    delay = TimeSpan.FromSeconds(15);
                }
                else
                {
                    var options = new CoordinatorOptions
                    {
                        ServerUri = new Uri(config.ServerUri),
                        PinnedServerKey = config.ServerPin,
                        AllowInsecureTransport = config.AllowInsecureTransport,
                    };
                    await using var client = await CoordinatorClient.ConnectAsync(options, identity, ct).ConfigureAwait(false);
                    if (!client.IsPaired)
                    {
                        if (string.IsNullOrWhiteSpace(config.PairCode))
                            throw new CoordinatorException("This device is not paired and no pairing code is set.");
                        await client.PairAsync(config.PairCode, config.DeviceName, canHost: true, ct).ConfigureAwait(false);
                        config.PairCode = "";
                        config.Save(configPath);
                        log($"Paired with the coordinator as '{config.DeviceName}'. Device id: {identity.DeviceId}");
                    }
                    log($"Online as {DeviceIds.Short(identity.DeviceId)}.");
                    delay = TimeSpan.FromSeconds(2);
                    await agent.RunAsync(client, ct).ConfigureAwait(false);
                    log("Disconnected from the coordinator.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log($"Coordinator connection failed: {ex.Message}");
            }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }
}
