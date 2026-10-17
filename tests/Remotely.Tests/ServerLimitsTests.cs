using System.Net;
using System.Net.Sockets;
using Remotely.Client;
using Remotely.Core.Crypto;

namespace Remotely.Tests;

public class ServerLimitsTests
{
    [Fact]
    public async Task Too_many_connection_attempts_from_one_address_are_turned_away_and_counted()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.Limits.ConnectAttemptsPerMinute = 4);
        for (var i = 0; i < 4; i++)
            await using (await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate())) { }

        await Assert.ThrowsAnyAsync<Exception>(() => CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate()));

        var metrics = await server.Admin.GetMetricsAsync();
        Assert.Equal(4, metrics.ConnectionsAccepted);
        Assert.True(metrics.ConnectionsRateLimited >= 1);
    }

    [Fact]
    public async Task The_connection_limit_is_per_source_so_another_address_is_not_affected()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.Limits.ConnectAttemptsPerMinute = 2);
        for (var i = 0; i < 3; i++)
        {
            try { await using var _ = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate()); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        await Assert.ThrowsAnyAsync<Exception>(() => CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate()));

        // 127.0.0.2 is another address on the loopback network (Linux and Windows both route all of 127.0.0.0/8 to the machine).
        using var raw = new System.Net.WebSockets.ClientWebSocket();
        raw.Options.SetRequestHeader("X-Test", "second-source");
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.PublicPort), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        using var invoker = new HttpMessageInvoker(handler);
        await raw.ConnectAsync(new Uri($"ws://127.0.0.1:{server.PublicPort}/ws"), invoker, CancellationToken.None);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, raw.State);
    }

    [Fact]
    public async Task Pairing_attempts_are_limited_even_when_the_code_is_right()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.Limits.PairAttemptsPerMinute = 3);
        var good = await server.NewPairCodeAsync();
        await using var client = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<CoordinatorException>(() => client.PairAsync("AAAA-AAAA", "Guess", false));

        var refused = await Assert.ThrowsAsync<CoordinatorException>(() => client.PairAsync(good, "Right code, too late", false));
        Assert.Contains("Too many attempts", refused.Message);
        Assert.True((await server.Admin.GetMetricsAsync()).PairingsRateLimited >= 1);
        Assert.Contains(await server.Admin.GetAuditAsync(), e => e.Kind == "pair" && e.Result == "rate-limited");
    }

    [Fact]
    public async Task Opening_sessions_too_fast_is_limited_per_device()
    {
        await using var server = await TestServer.StartAsync(configure: o => o.Limits.SessionRequestsPerMinute = 2);
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, _) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        await viewer.ConnectToAsync(hostId.DeviceId);
        await viewer.ConnectToAsync(hostId.DeviceId);
        var ex = await Assert.ThrowsAsync<CoordinatorException>(() => viewer.ConnectToAsync(hostId.DeviceId));
        Assert.Contains("Too many connection attempts", ex.Message);
    }

    [Fact]
    public async Task The_default_limits_leave_room_for_a_household()
    {
        await using var server = await TestServer.StartAsync();
        var clients = new List<CoordinatorClient>();
        try
        {
            for (var i = 0; i < 12; i++) clients.Add((await server.PairedDeviceAsync("PC " + i, canHost: i % 2 == 0)).Client);
            Assert.All(clients, c => Assert.True(c.IsConnected));
        }
        finally
        {
            foreach (var c in clients) await c.DisposeAsync();
        }
    }
}
