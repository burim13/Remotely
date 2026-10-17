using Remotely.Core.Admin;

namespace Remotely.Tests;

/// <summary>A short run of the soak harness (<see cref="FakeFleet"/>) to show its hooks work; the long run is the soak job's.</summary>
public class ServerSoakTests
{
    [Fact]
    public async Task A_fleet_of_fake_devices_pairs_runs_encrypted_sessions_through_the_relay_and_survives_churn()
    {
        await using var server = await TestServer.StartAsync(configure: TestServer.NoLimits);
        await using var fleet = await FakeFleet.StartAsync(server, hosts: 8, viewers: 8);
        Assert.Equal(16, fleet.Connected);

        await fleet.RunSessionsAsync(rounds: 2, messagesPerSession: 3);
        Assert.Equal(8 * 2 * 3, fleet.Echoes);
        Assert.Equal(0, fleet.Failures);

        await fleet.ChurnAsync(6);
        await Wait.UntilAsync(async () => (await server.Admin.GetStatusAsync()).DevicesOnline == 16, "all devices to be back online after churn");
        await fleet.RunSessionsAsync(rounds: 1);
        Assert.Equal(0, fleet.Failures);

        var metrics = await server.Admin.GetMetricsAsync();
        Assert.Equal(16, metrics.DevicesTotal);
        Assert.Equal(16, metrics.PairingsSucceeded);
        Assert.Equal(8 * 3, metrics.SessionsOpened);
        Assert.True(metrics.FramesRelayed > 0);
        Assert.Equal(0, metrics.SessionsRefused);
        await Wait.UntilAsync(async () => (await server.Admin.GetStatusAsync()).SessionsActive == 0, "sessions to be closed");
    }
}
