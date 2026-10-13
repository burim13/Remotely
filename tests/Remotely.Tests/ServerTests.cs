using System.Net;
using System.Security.Cryptography;
using System.Text;
using Remotely.Client;
using Remotely.Core.Crypto;

namespace Remotely.Tests;

public class ServerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_new_device_pairs_with_a_one_time_code_and_is_remembered()
    {
        await using var server = await TestServer.StartAsync();
        var (client, identity) = await server.PairedDeviceAsync("Dad's PC", canHost: true);
        await using var _ = client;
        Assert.True(client.IsPaired);

        // Reconnecting with the same key needs no code.
        await using var again = await CoordinatorClient.ConnectAsync(server.Options(), identity);
        Assert.True(again.IsPaired);
        var devices = await again.ListDevicesAsync();
        var me = Assert.Single(devices);
        Assert.Equal(identity.DeviceId, me.DeviceId);
        Assert.Equal("Dad's PC", me.Name);
        Assert.True(me.Online);
    }

    [Fact]
    public async Task A_pair_code_works_once_only()
    {
        await using var server = await TestServer.StartAsync();
        var code = await server.NewPairCodeAsync();
        await using var first = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
        await first.PairAsync(code, "One", false);

        await using var second = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
        await Assert.ThrowsAsync<CoordinatorException>(() => second.PairAsync(code, "Two", false));
    }

    [Fact]
    public async Task Wrong_codes_are_refused_and_repeated_guessing_locks_the_address_out()
    {
        await using var server = await TestServer.StartAsync();
        var good = await server.NewPairCodeAsync();

        for (var i = 0; i < 5; i++)
        {
            await using var guess = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
            await Assert.ThrowsAsync<CoordinatorException>(() => guess.PairAsync("AAAA-AAAA", "Guess", false));
        }
        // After five misses, even a valid code is refused from this address.
        await using var late = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
        await Assert.ThrowsAsync<CoordinatorException>(() => late.PairAsync(good, "Late", false));
    }

    [Fact]
    public async Task An_unpaired_device_cannot_list_or_connect()
    {
        await using var server = await TestServer.StartAsync();
        await using var stranger = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
        Assert.False(stranger.IsPaired);
        await Assert.ThrowsAsync<CoordinatorException>(() => stranger.ListDevicesAsync());
        await Assert.ThrowsAsync<CoordinatorException>(() => stranger.ConnectToAsync(new string('a', 64)));
    }

    [Fact]
    public async Task The_admin_api_is_not_reachable_through_the_public_port()
    {
        await using var server = await TestServer.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, await server.GetStatusAsync($"http://127.0.0.1:{server.PublicPort}/admin/devices"));
        Assert.Equal(HttpStatusCode.OK, await server.GetStatusAsync($"http://127.0.0.1:{server.AdminPort}/admin/devices"));
    }

    [Fact]
    public async Task Connecting_to_an_offline_or_unknown_host_fails_cleanly()
    {
        await using var server = await TestServer.StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        var (viewer, _) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;
        await host.DisposeAsync();
        await Task.Delay(300); // let the server notice the disconnect

        var offline = await Assert.ThrowsAsync<CoordinatorException>(() => viewer.ConnectToAsync(hostId.DeviceId));
        Assert.Contains("offline", offline.Message);
        await Assert.ThrowsAsync<CoordinatorException>(() => viewer.ConnectToAsync(new string('b', 64)));
        // A device that never accepts connections can't be a target either.
        var viewerId = (await viewer.ListDevicesAsync()).Single(d => d.Name == "Viewer").DeviceId;
        var (other, _) = await server.PairedDeviceAsync("Other", canHost: false);
        await using var _o = other;
        var notHost = await Assert.ThrowsAsync<CoordinatorException>(() => other.ConnectToAsync(viewerId));
        Assert.Contains("does not accept", notHost.Message);
    }

    [Fact]
    public async Task Viewer_and_host_talk_end_to_end_through_the_relay()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = await TestServer.StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        var hostSide = Task.Run(async () =>
        {
            var relay = await host.IncomingSessions.ReadAsync(cts.Token);
            using var secure = await SecureSession.AcceptAsync(relay, hostId, id => id == viewerId.DeviceId, cts.Token);
            var request = await secure.ReceiveAsync(cts.Token);
            await secure.SendAsync(Encoding.UTF8.GetBytes("echo:" + Encoding.UTF8.GetString(request!)), cts.Token);
            return secure.PeerDeviceId;
        }, cts.Token);

        var relay = await viewer.ConnectToAsync(hostId.DeviceId, cts.Token);
        Assert.True(relay.IsInitiator);
        using var viewerSecure = await SecureSession.InitiateAsync(relay, viewerId, cts.Token);
        await viewerSecure.SendAsync("hello host"u8.ToArray(), cts.Token);
        var reply = await viewerSecure.ReceiveAsync(cts.Token);

        Assert.Equal("echo:hello host", Encoding.UTF8.GetString(reply!));
        Assert.Equal(viewerId.DeviceId, await hostSide);
    }

    [Fact]
    public async Task The_host_refuses_a_viewer_it_does_not_trust()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = await TestServer.StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        var hostSide = Task.Run(async () =>
        {
            var relay = await host.IncomingSessions.ReadAsync(cts.Token);
            return await Assert.ThrowsAsync<HandshakeException>(() => SecureSession.AcceptAsync(relay, hostId, _ => false, cts.Token));
        }, cts.Token);

        var relay = await viewer.ConnectToAsync(hostId.DeviceId, cts.Token);
        var viewerAttempt = SecureSession.InitiateAsync(relay, viewerId, cts.Token);
        await hostSide;
        await relay.CloseAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => viewerAttempt);
    }

    [Fact]
    public async Task A_relay_that_tampers_with_traffic_is_detected()
    {
        // The coordinator forwards bytes it cannot read; this simulates it (or anyone on the path) flipping a bit.
        using var cts = new CancellationTokenSource(Limit);
        await using var server = await TestServer.StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        var received = Task.Run(async () =>
        {
            var relay = await host.IncomingSessions.ReadAsync(cts.Token);
            using var secure = await SecureSession.AcceptAsync(relay, hostId, _ => true, cts.Token);
            return await Assert.ThrowsAnyAsync<CryptographicException>(() => secure.ReceiveAsync(cts.Token));
        }, cts.Token);

        var relay = await viewer.ConnectToAsync(hostId.DeviceId, cts.Token);
        using var secure = await SecureSession.InitiateAsync(relay, viewerId, cts.Token);
        // Bypass SecureSession: send a sealed-looking frame with one bit wrong.
        var forged = new byte[8 + 5 + 16];
        RandomNumberGenerator.Fill(forged.AsSpan(8));
        await relay.SendAsync(forged, cts.Token);
        await received;
    }

    [Fact]
    public async Task When_one_side_disconnects_the_other_side_sees_the_session_end()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = await TestServer.StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        var (viewer, _) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        var relay = await viewer.ConnectToAsync(hostId.DeviceId, cts.Token);
        await host.DisposeAsync();
        Assert.Null(await relay.ReceiveAsync(cts.Token));
    }

    [Fact]
    public async Task Over_tls_the_client_connects_only_to_the_pinned_server_key()
    {
        await using var server = await TestServer.StartAsync(tls: true);
        var identity = DeviceIdentity.Generate();

        await using var ok = await CoordinatorClient.ConnectAsync(server.Options(), identity);
        await ok.PairAsync(await server.NewPairCodeAsync(), "Pinned", true);
        Assert.True(ok.IsPaired);

        var wrongPin = new string('0', 64);
        await Assert.ThrowsAnyAsync<Exception>(() => CoordinatorClient.ConnectAsync(server.Options(pinOverride: wrongPin), identity));
        var noPin = server.Options();
        await Assert.ThrowsAsync<CoordinatorException>(() => CoordinatorClient.ConnectAsync(
            new CoordinatorOptions { ServerUri = noPin.ServerUri }, identity));
    }

    [Fact]
    public async Task Plain_ws_is_refused_unless_explicitly_allowed()
    {
        await using var server = await TestServer.StartAsync();
        var opts = server.Options(insecure: false);
        await Assert.ThrowsAsync<CoordinatorException>(() => CoordinatorClient.ConnectAsync(opts, DeviceIdentity.Generate()));
    }

    [Fact]
    public async Task Paired_devices_survive_a_server_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remotely-restart-" + Guid.NewGuid().ToString("N"));
        var identity = DeviceIdentity.Generate();
        try
        {
            var store = new Remotely.Server.DeviceStore(dir);
            store.Add(new Remotely.Server.DeviceRecord(identity.DeviceId, "Kept", identity.PublicKey, true, DateTimeOffset.UtcNow));
            var reloaded = new Remotely.Server.DeviceStore(dir);
            Assert.Equal("Kept", reloaded.Find(identity.DeviceId)!.Name);
            Assert.True(reloaded.Remove(identity.DeviceId));
            Assert.Null(new Remotely.Server.DeviceStore(dir).Find(identity.DeviceId));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
