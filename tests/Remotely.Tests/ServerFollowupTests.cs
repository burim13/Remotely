using System.Net;
using System.Net.Sockets;
using Remotely.Client;
using Remotely.Core.Admin;
using Remotely.Core.Crypto;
using Remotely.Core.NetworkSetup;
using Remotely.Core.Protocol;
using Remotely.Server;

namespace Remotely.Tests;

public class ServerFollowupTests
{
    private static async Task<TestServer> StartAsync(bool tls = false, Action<ServerOptions>? configure = null)
    {
        var server = await TestServer.StartAsync(tls, configure);
        await server.Admin.SetSettingsAsync((await server.Admin.GetSettingsAsync()) with { Address = "127.0.0.1" });
        return server;
    }

    // ---- revoke and block drop live connections and refuse the next ones ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoking_or_blocking_closes_the_live_connection_and_its_sessions_and_the_old_client_can_do_nothing(bool block)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = await StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;
        var relay = await viewer.ConnectToAsync(hostId.DeviceId, cts.Token);
        var hostSession = await host.IncomingSessions.ReadAsync(cts.Token);

        if (block) await server.Admin.SetDeviceBlockedAsync(viewerId.DeviceId, true);
        else await server.Admin.RevokeDeviceAsync(viewerId.DeviceId);

        await Wait.UntilAsync(() => !viewer.IsConnected, "the connection to be closed");
        Assert.Null(await hostSession.ReceiveAsync(cts.Token));   // the host sees the session end
        await Assert.ThrowsAnyAsync<Exception>(() => viewer.ListDevicesAsync(cts.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => viewer.ConnectToAsync(hostId.DeviceId, cts.Token));
        Assert.False((await server.Admin.ListDevicesAsync()).Single(d => d.DeviceId == hostId.DeviceId).Blocked);

        // The next connection is not let in as a paired device.
        await using var again = await CoordinatorClient.ConnectAsync(server.Options(), viewerId, cts.Token);
        Assert.False(again.IsPaired);
        await Assert.ThrowsAsync<CoordinatorException>(() => again.ListDevicesAsync(cts.Token));
        await Assert.ThrowsAsync<CoordinatorException>(() => again.ConnectToAsync(hostId.DeviceId, cts.Token));
        _ = relay;
    }

    [Fact]
    public async Task A_revoked_device_can_pair_again_only_with_a_new_invite_a_blocked_one_cannot_at_all()
    {
        await using var server = await StartAsync();
        var used = await server.NewPairCodeAsync();
        var identity = DeviceIdentity.Generate();
        await using (var first = await CoordinatorClient.ConnectAsync(server.Options(), identity))
            await first.PairAsync(used, "Revoked", true);
        await server.Admin.RevokeDeviceAsync(identity.DeviceId);

        await using (var old = await CoordinatorClient.ConnectAsync(server.Options(), identity))
        {
            await Assert.ThrowsAsync<CoordinatorException>(() => old.PairAsync(used, "Revoked", true));   // the old code was used up
            var fresh = await server.Admin.CreateInviteAsync(TimeSpan.FromMinutes(5), 1);
            Invite.TryParse(fresh.InviteText, out var invite, out _);
            await old.PairAsync(invite!.Code, "Revoked", true);
            Assert.True(old.IsPaired);
        }

        await server.Admin.SetDeviceBlockedAsync(identity.DeviceId, true);
        await using var blocked = await CoordinatorClient.ConnectAsync(server.Options(), identity);
        var ex = await Assert.ThrowsAsync<CoordinatorException>(async () => await blocked.PairAsync(await server.NewPairCodeAsync(), "Blocked", true));
        Assert.Contains("blocked", ex.Message);
    }

    // ---- outside check ----

    [Fact]
    public async Task The_check_page_records_the_first_requester_and_the_manager_side_reads_it()
    {
        await using var server = await StartAsync();
        var check = new ServerOutsideCheck(server.Admin);
        var ticket = await check.StartAsync();
        Assert.Matches("^/check/[A-Z2-9]{10}$", ticket.Path);
        Assert.Null(await check.GetHitAsync(ticket.Id));

        using var http = new HttpClient();
        var first = await http.GetAsync($"http://127.0.0.1:{server.PublicPort}{ticket.Path}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.DoesNotContain(ticket.Id, await first.Content.ReadAsStringAsync());

        var hit = await check.GetHitAsync(ticket.Id);
        Assert.Equal(IPAddress.Loopback, hit!.Source);
        Assert.Equal(OutsideVerdict.SeenFromInside, OutsideCheckEvaluator.Evaluate(hit));

        // A later request from another address does not replace the first.
        using var other = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.PublicPort), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        });
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"http://127.0.0.1:{server.PublicPort}{ticket.Path}")).StatusCode);
        Assert.Equal(IPAddress.Loopback, (await check.GetHitAsync(ticket.Id))!.Source);
        Assert.Contains(await server.Admin.GetAuditAsync(), e => e.Kind == "outside-check.create");
    }

    [Fact]
    public async Task The_check_page_reveals_nothing_for_unknown_tickets_and_only_the_page_is_unauthenticated()
    {
        await using var server = await StartAsync();
        using var http = new HttpClient();
        var unknown = await http.GetAsync($"http://127.0.0.1:{server.PublicPort}/check/NOSUCHTICKET");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("", await unknown.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.RawAdminAsync(HttpMethod.Post, "/admin/v1/outside-check")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.RawAdminAsync(HttpMethod.Get, "/admin/v1/outside-check/ABC")).StatusCode);
        var ticket = await new ServerOutsideCheck(server.Admin).StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, await server.GetStatusAsync($"http://127.0.0.1:{server.AdminPort}{ticket.Path}"));   // not served on the admin port
        var missing = await Assert.ThrowsAsync<ServerAdminException>(() => server.Admin.GetOutsideCheckAsync("NOSUCHTICKET"));
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task The_check_page_is_rate_limited_and_the_number_of_tickets_is_bounded()
    {
        await using var server = await StartAsync();
        var ticket = await new ServerOutsideCheck(server.Admin).StartAsync();
        using var http = new HttpClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 40; i++) statuses.Add((await http.GetAsync($"http://127.0.0.1:{server.PublicPort}/check/GUESS{i:D5}")).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.GetAsync($"http://127.0.0.1:{server.PublicPort}{ticket.Path}")).StatusCode);

        var first = await server.Admin.StartOutsideCheckAsync();
        for (var i = 0; i < OutsideChecks.MaxActive + 2; i++) await server.Admin.StartOutsideCheckAsync();
        Assert.Equal(404, (await Assert.ThrowsAsync<ServerAdminException>(() => server.Admin.GetOutsideCheckAsync(first.Id))).StatusCode);   // oldest dropped
        var long1 = await server.Admin.StartOutsideCheckAsync(TimeSpan.FromDays(3));
        Assert.True(long1.ExpiresUtc <= DateTimeOffset.UtcNow.AddHours(1).AddMinutes(1));
    }

    // ---- what needs a restart ----

    [Fact]
    public async Task A_replaced_server_pfx_is_used_for_new_connections_without_a_restart()
    {
        await using var server = await StartAsync(tls: true);
        var other = Path.Combine(Path.GetTempPath(), "remotely-cert-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var replacement = ServerCertificate.LoadOrCreate(other);   // as the Server Manager's helper does: put a new server.pfx in place
            var newPin = ServerCertificate.Pin(replacement);
            Assert.NotEqual(server.Pin, newPin);
            File.Copy(Path.Combine(other, "server.pfx"), Path.Combine(server.DataDir, "server.pfx"), overwrite: true);

            await Wait.UntilAsync(async () => (await server.Admin.GetStatusAsync()).CertificatePin == newPin, "the new certificate to be noticed", seconds: 20);
            await using var c = await CoordinatorClient.ConnectAsync(server.Options(pinOverride: newPin), DeviceIdentity.Generate());
            await Assert.ThrowsAnyAsync<Exception>(() => CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate()));   // the old pin is refused now
        }
        finally
        {
            if (Directory.Exists(other)) Directory.Delete(other, true);
        }
    }

    [Fact]
    public async Task A_changed_address_file_is_used_at_once_by_status_settings_and_new_invites()
    {
        await using var server = await StartAsync();
        File.WriteAllText(Path.Combine(server.DataDir, "address.txt"), "changed.example.com");   // what 'Remotely.Server.exe set-address' does, from another process

        Assert.Equal("changed.example.com", (await server.Admin.GetStatusAsync()).Address);
        Assert.Equal("changed.example.com", (await server.Admin.GetSettingsAsync()).Address);
        var invite = await server.Admin.CreateInviteAsync(TimeSpan.FromMinutes(5), 1);
        Invite.TryParse(invite.InviteText, out var parsed, out _);
        Assert.Contains("changed.example.com", parsed!.ServerUri);
    }
}
