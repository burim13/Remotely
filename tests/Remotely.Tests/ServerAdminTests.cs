using System.Net;
using System.Text;
using Remotely.Client;
using Remotely.Core.Admin;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;
using Remotely.Server;

namespace Remotely.Tests;

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Gave up waiting for: " + what);
            await Task.Delay(50);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Gave up waiting for: " + what);
            await Task.Delay(50);
        }
    }
}

public class ServerAdminTests
{
    private static async Task<TestServer> StartAsync(bool tls = false, Action<Remotely.Server.ServerOptions>? configure = null)
    {
        var server = await TestServer.StartAsync(tls, configure);
        // Invites carry the server's address; tests run on loopback.
        var settings = await server.Admin.GetSettingsAsync();
        await server.Admin.SetSettingsAsync(settings with { Address = "127.0.0.1" });
        return server;
    }

    private static string CodeOf(string inviteText)
    {
        Assert.True(Invite.TryParse(inviteText, out var invite, out var error), error);
        return invite!.Code;
    }

    // ---- authentication ----

    [Fact]
    public async Task Admin_v1_calls_without_the_token_or_with_a_wrong_one_are_refused()
    {
        await using var server = await TestServer.StartAsync();
        foreach (var path in new[] { "/admin/v1/status", "/admin/v1/devices", "/admin/v1/invites", "/admin/v1/audit", "/admin/v1/settings", "/admin/v1/backup", "/admin/v1/metrics" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await server.RawAdminAsync(HttpMethod.Get, path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await server.RawAdminAsync(HttpMethod.Get, path, token: "not-the-token")).StatusCode);
        }
        // Calls that change things are refused too, including ones with a malformed body (the check comes before the body is read).
        foreach (var path in new[] { "/admin/v1/invites", "/admin/v1/restore", "/admin/v1/certificate/renew", "/admin/v1/token/rotate" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await server.RawAdminAsync(HttpMethod.Post, path, content: new StringContent("{ not json", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.RawAdminAsync(HttpMethod.Delete, "/admin/v1/devices/" + new string('a', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.RawAdminAsync(HttpMethod.Get, "/admin/v1/status", token: server.AdminToken)).StatusCode);

        // The refusals are counted and written to the audit log.
        var metrics = await server.Admin.GetMetricsAsync();
        Assert.True(metrics.AdminUnauthorized >= 10);
        Assert.Contains(await server.Admin.GetAuditAsync(), e => e.Kind == "admin.unauthorized");
    }

    [Fact]
    public async Task The_client_without_the_right_token_gets_a_clear_error()
    {
        await using var server = await TestServer.StartAsync();
        using var wrong = ServerAdminClient.ForToken("wrong-token", server.AdminPort);
        var ex = await Assert.ThrowsAsync<ServerAdminException>(() => wrong.GetStatusAsync());
        Assert.Equal(401, ex.StatusCode);
        using var nowhere = ServerAdminClient.ForToken(server.AdminToken, adminPort: 1);
        var down = await Assert.ThrowsAsync<ServerAdminException>(() => nowhere.GetStatusAsync());
        Assert.Equal(0, down.StatusCode);
        using var noFile = ServerAdminClient.ForDataDirectory(Path.Combine(server.DataDir, "missing"), server.AdminPort);
        var unreadable = await Assert.ThrowsAsync<ServerAdminException>(() => noFile.GetStatusAsync());
        Assert.Equal(0, unreadable.StatusCode);
    }

    [Fact]
    public async Task Admin_v1_is_not_reachable_through_the_public_port_even_with_the_token()
    {
        await using var server = await TestServer.StartAsync();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.PublicPort}/admin/v1/status");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.AdminToken);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(request)).StatusCode);

        // A request that claims to have been forwarded is not treated as local.
        using var forwarded = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.AdminPort}/admin/v1/status");
        forwarded.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.AdminToken);
        forwarded.Headers.Add("X-Forwarded-For", "203.0.113.9");
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(forwarded)).StatusCode);
    }

    [Fact]
    public async Task The_token_can_be_rotated_and_the_old_one_stops_working()
    {
        await using var server = await TestServer.StartAsync();
        var old = server.AdminToken;
        var fresh = await server.Admin.RotateTokenAsync();

        Assert.NotEqual(old, fresh);
        Assert.Equal(fresh, server.AdminToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.RawAdminAsync(HttpMethod.Get, "/admin/v1/status", token: old)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.RawAdminAsync(HttpMethod.Get, "/admin/v1/status", token: fresh)).StatusCode);
        Assert.Contains(await server.Admin.GetAuditAsync(), e => e.Kind == "token.rotate");

        // A client that reads the file on every call follows the rotation by itself.
        using var other = ServerAdminClient.ForDataDirectory(server.DataDir, server.AdminPort);
        await other.RotateTokenAsync();
        await server.Admin.GetStatusAsync();
    }

    [Fact]
    public async Task The_token_survives_a_restart_and_is_the_same_file_content()
    {
        await using var server = await TestServer.StartAsync();
        var before = server.AdminToken;
        await server.RestartAsync();
        Assert.Equal(before, server.AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await server.RawAdminAsync(HttpMethod.Get, "/admin/v1/status", token: before)).StatusCode);
    }

    [Fact]
    public async Task On_Linux_the_token_file_is_readable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;   // the Windows access list is checked by compiling the code path only; see docs/server-admin-api.md
        await using var server = await TestServer.StartAsync(tls: true);
        var path = Path.Combine(server.DataDir, ServerAdminClient.TokenFileName);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        await server.Admin.RotateTokenAsync();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(server.DataDir, "server.pfx")));
    }

    [Fact]
    public async Task The_older_admin_endpoints_work_without_a_token_unless_the_server_is_told_to_demand_one()
    {
        await using (var open = await TestServer.StartAsync())
        {
            Assert.Equal(HttpStatusCode.OK, (await open.RawAdminAsync(HttpMethod.Post, "/admin/pair-codes")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await open.RawAdminAsync(HttpMethod.Get, "/admin/devices")).StatusCode);
            var body = await (await open.RawAdminAsync(HttpMethod.Get, "/admin/devices")).Content.ReadAsStringAsync();
            Assert.Equal("[]", body);
        }
        await using var strict = await TestServer.StartAsync(configure: o => o.LegacyAdminRequiresToken = true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await strict.RawAdminAsync(HttpMethod.Post, "/admin/pair-codes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await strict.RawAdminAsync(HttpMethod.Post, "/admin/pair-codes", token: strict.AdminToken)).StatusCode);
    }

    [Fact]
    public async Task The_older_devices_endpoint_keeps_its_shape_and_delete_removes_and_disconnects()
    {
        await using var server = await TestServer.StartAsync();
        var (client, identity) = await server.PairedDeviceAsync("Legacy", canHost: true);
        await using var _ = client;
        var json = await (await server.RawAdminAsync(HttpMethod.Get, "/admin/devices")).Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var first = doc.RootElement[0];
        Assert.Equal(identity.DeviceId, first.GetProperty("deviceId").GetString());
        Assert.Equal("Legacy", first.GetProperty("name").GetString());
        Assert.True(first.GetProperty("canHost").GetBoolean());
        Assert.True(first.GetProperty("online").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await server.RawAdminAsync(HttpMethod.Delete, "/admin/devices/" + identity.DeviceId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.RawAdminAsync(HttpMethod.Delete, "/admin/devices/" + identity.DeviceId)).StatusCode);
        await Wait.UntilAsync(() => !client.IsConnected, "the removed device to be disconnected");
    }

    // ---- status and metrics ----

    [Fact]
    public async Task Status_reports_version_address_port_pin_expiry_and_counts()
    {
        await using var server = await StartAsync(tls: true);
        var (host, _) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, _) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        var status = await server.Admin.GetStatusAsync();
        Assert.False(string.IsNullOrEmpty(status.Version));
        Assert.Equal("127.0.0.1", status.Address);
        Assert.Equal(server.PublicPort, status.Port);
        Assert.True(status.TlsEnabled);
        Assert.Equal(server.Pin, status.CertificatePin);
        Assert.True(status.CertificateExpiresUtc > DateTimeOffset.UtcNow.AddYears(9));
        Assert.True(status.StartedUtc <= DateTimeOffset.UtcNow && status.StartedUtc > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(2, status.DevicesTotal);
        Assert.Equal(2, status.DevicesOnline);
        Assert.Equal(0, status.SessionsActive);

        await viewer.ConnectToAsync((await viewer.ListDevicesAsync()).Single(d => d.Name == "Host").DeviceId);
        Assert.Equal(1, (await server.Admin.GetStatusAsync()).SessionsActive);
    }

    [Fact]
    public async Task Without_tls_status_has_no_pin_and_renewal_is_refused()
    {
        await using var server = await StartAsync();
        var status = await server.Admin.GetStatusAsync();
        Assert.False(status.TlsEnabled);
        Assert.Equal("", status.CertificatePin);
        var ex = await Assert.ThrowsAsync<ServerAdminException>(() => server.Admin.RenewCertificateAsync());
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task Metrics_count_connections_pairings_sessions_and_relayed_frames()
    {
        await using var server = await StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var hostSide = Task.Run(async () =>
        {
            var relay = await host.IncomingSessions.ReadAsync(cts.Token);
            using var secure = await SecureSession.AcceptAsync(relay, hostId, _ => true, cts.Token);
            await secure.SendAsync(await secure.ReceiveAsync(cts.Token) ?? [], cts.Token);
        }, cts.Token);
        var relay = await viewer.ConnectToAsync(hostId.DeviceId, cts.Token);
        using var secure = await SecureSession.InitiateAsync(relay, viewerId, cts.Token);
        await secure.SendAsync(new byte[] { 1, 2, 3 }, cts.Token);
        await secure.ReceiveAsync(cts.Token);
        await hostSide;

        var m = await server.Admin.GetMetricsAsync();
        Assert.True(m.ConnectionsAccepted >= 2);
        Assert.Equal(2, m.PairingsSucceeded);
        Assert.Equal(1, m.SessionsOpened);
        Assert.True(m.FramesRelayed > 0 && m.BytesRelayed > 0);
        Assert.True(m.WorkingSetBytes > 0 && m.ThreadCount > 0);
        Assert.Equal(2, m.DevicesOnline);
        Assert.Equal(1, m.SessionsActive);
    }

    [Fact]
    public async Task Healthz_answers_on_the_public_port_and_on_the_admin_port_without_a_token()
    {
        await using var server = await TestServer.StartAsync();
        Assert.Equal(HttpStatusCode.OK, await server.GetStatusAsync($"http://127.0.0.1:{server.PublicPort}/healthz"));
        Assert.Equal(HttpStatusCode.OK, await server.GetStatusAsync($"http://127.0.0.1:{server.AdminPort}/healthz"));
    }

    // ---- invites ----

    [Fact]
    public async Task An_invite_has_the_address_pin_and_a_code_that_works_for_the_allowed_number_of_devices()
    {
        await using var server = await StartAsync(tls: true);
        var invite = await server.Admin.CreateInviteAsync(TimeSpan.FromMinutes(30), uses: 2);
        Assert.Equal(2, invite.UsesAllowed);
        Assert.Equal(2, invite.UsesLeft);
        Assert.True(invite.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(29) && invite.ExpiresUtc <= DateTimeOffset.UtcNow.AddMinutes(31));
        Assert.True(Invite.TryParse(invite.InviteText, out var parsed, out var error), error);
        Assert.Equal($"wss://127.0.0.1:{server.PublicPort}/ws", parsed!.ServerUri);
        Assert.Equal(server.Pin, parsed.PinHex);

        for (var i = 0; i < 2; i++)
        {
            await using var c = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
            await c.PairAsync(parsed.Code, "Joiner " + i, false);
        }
        await using var third = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
        await Assert.ThrowsAsync<CoordinatorException>(() => third.PairAsync(parsed.Code, "Too many", false));
        Assert.Empty(await server.Admin.ListInvitesAsync());
    }

    [Fact]
    public async Task Invites_are_listed_with_remaining_uses_and_can_be_revoked()
    {
        await using var server = await StartAsync();
        var keep = await server.Admin.CreateInviteAsync(TimeSpan.FromHours(1), 3);
        var drop = await server.Admin.CreateInviteAsync(TimeSpan.FromHours(1), 1);
        Assert.NotEqual(keep.Id, drop.Id);

        await using (var c = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate()))
            await c.PairAsync(CodeOf(keep.InviteText), "One use", false);
        var listed = await server.Admin.ListInvitesAsync();
        Assert.Equal(2, listed.Count);
        Assert.Equal(2, listed.Single(i => i.Id == keep.Id).UsesLeft);
        Assert.Equal(CodeOf(drop.InviteText), CodeOf(listed.Single(i => i.Id == drop.Id).InviteText));

        await server.Admin.RevokeInviteAsync(drop.Id);
        Assert.Single(await server.Admin.ListInvitesAsync());
        await using var late = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
        await Assert.ThrowsAsync<CoordinatorException>(() => late.PairAsync(CodeOf(drop.InviteText), "Revoked", false));
        var again = await Assert.ThrowsAsync<ServerAdminException>(() => server.Admin.RevokeInviteAsync(drop.Id));
        Assert.Equal(404, again.StatusCode);
    }

    [Fact]
    public async Task Invites_with_impossible_lifetime_or_uses_are_refused()
    {
        await using var server = await StartAsync();
        foreach (var (life, uses) in new[] { (TimeSpan.Zero, 1), (TimeSpan.FromHours(25), 1), (TimeSpan.FromMinutes(5), 0), (TimeSpan.FromMinutes(5), 21) })
        {
            var ex = await Assert.ThrowsAsync<ServerAdminException>(() => server.Admin.CreateInviteAsync(life, uses));
            Assert.Equal(400, ex.StatusCode);
        }
    }

    [Fact]
    public async Task An_invite_cannot_be_made_while_the_address_is_unknown_and_unguessable_but_a_saved_address_fixes_it()
    {
        // The test machine may or may not have a network address to guess; saving one removes the doubt.
        await using var server = await TestServer.StartAsync();
        await server.Admin.SetSettingsAsync((await server.Admin.GetSettingsAsync()) with { Address = "home.example.com" });
        var invite = await server.Admin.CreateInviteAsync(TimeSpan.FromMinutes(5), 1);
        Assert.True(Invite.TryParse(invite.InviteText, out var parsed, out _));
        Assert.Equal($"ws://home.example.com:{server.PublicPort}/ws", parsed!.ServerUri);
    }

    // ---- devices ----

    [Fact]
    public async Task Devices_are_listed_renamed_and_the_new_name_reaches_the_other_devices()
    {
        await using var server = await StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, _) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        var list = await server.Admin.ListDevicesAsync();
        var entry = list.Single(d => d.DeviceId == hostId.DeviceId);
        Assert.Equal("Host", entry.Name);
        Assert.True(entry.CanHost && entry.Online && !entry.Blocked);
        Assert.True(entry.PairedUtc > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.NotNull(entry.LastSeenUtc);

        await server.Admin.RenameDeviceAsync(hostId.DeviceId, "  Dad's PC\t");
        Assert.Equal("Dad's PC", (await viewer.ListDevicesAsync()).Single(d => d.DeviceId == hostId.DeviceId).Name);
        Assert.Equal(400, (await Assert.ThrowsAsync<ServerAdminException>(() => server.Admin.RenameDeviceAsync(hostId.DeviceId, "   "))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ServerAdminException>(() => server.Admin.RenameDeviceAsync(new string('f', 64), "x"))).StatusCode);
        Assert.Contains(await server.Admin.GetAuditAsync(), e => e.Kind == "device.rename" && e.DeviceId == hostId.DeviceId);
    }

    [Fact]
    public async Task A_revoked_device_is_disconnected_and_must_join_again()
    {
        await using var server = await StartAsync();
        var (client, identity) = await server.PairedDeviceAsync("Gone", canHost: true);
        await using var _ = client;

        await server.Admin.RevokeDeviceAsync(identity.DeviceId);

        await Wait.UntilAsync(() => !client.IsConnected, "the revoked device to be disconnected");
        Assert.Empty(await server.Admin.ListDevicesAsync());
        await using var back = await CoordinatorClient.ConnectAsync(server.Options(), identity);
        Assert.False(back.IsPaired);
        Assert.Equal(404, (await Assert.ThrowsAsync<ServerAdminException>(() => server.Admin.RevokeDeviceAsync(identity.DeviceId))).StatusCode);

        // It can join again with a new invite: revoking forgets, blocking keeps out.
        await back.PairAsync(await server.NewPairCodeAsync(), "Gone", true);
        Assert.True(back.IsPaired);
    }

    [Fact]
    public async Task A_blocked_device_is_disconnected_cannot_pair_again_and_is_let_back_in_when_unblocked()
    {
        await using var server = await StartAsync();
        var (client, identity) = await server.PairedDeviceAsync("Troublemaker", canHost: true);
        await using var _ = client;
        var (other, _) = await server.PairedDeviceAsync("Other", canHost: false);
        await using var _o = other;

        await server.Admin.SetDeviceBlockedAsync(identity.DeviceId, true);

        await Wait.UntilAsync(() => !client.IsConnected, "the blocked device to be disconnected");
        Assert.True((await server.Admin.ListDevicesAsync()).Single(d => d.DeviceId == identity.DeviceId).Blocked);
        Assert.DoesNotContain(await other.ListDevicesAsync(), d => d.DeviceId == identity.DeviceId);

        await using var retry = await CoordinatorClient.ConnectAsync(server.Options(), identity);
        Assert.False(retry.IsPaired);
        var refused = await Assert.ThrowsAsync<CoordinatorException>(async () => await retry.PairAsync(await server.NewPairCodeAsync(), "Troublemaker", true));
        Assert.Contains("blocked", refused.Message);
        Assert.Contains(await server.Admin.GetAuditAsync(), e => e.Kind == "auth" && e.DeviceId == identity.DeviceId && e.Result == "refused");

        await server.Admin.SetDeviceBlockedAsync(identity.DeviceId, false);
        await using var welcome = await CoordinatorClient.ConnectAsync(server.Options(), identity);
        Assert.True(welcome.IsPaired);
        Assert.Contains(await other.ListDevicesAsync(), d => d.DeviceId == identity.DeviceId);
    }

    [Fact]
    public async Task Blocking_a_device_in_a_session_ends_the_session_for_the_other_side()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = await StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, _) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;
        var relay = await viewer.ConnectToAsync(hostId.DeviceId, cts.Token);

        await server.Admin.SetDeviceBlockedAsync(hostId.DeviceId, true);

        Assert.Null(await relay.ReceiveAsync(cts.Token));
    }

    // ---- audit ----

    [Fact]
    public async Task The_audit_log_records_pairing_sessions_and_admin_changes_newest_first_and_can_be_filtered()
    {
        await using var server = await StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;
        var relay = await viewer.ConnectToAsync(hostId.DeviceId);
        await relay.CloseAsync();
        await Assert.ThrowsAsync<CoordinatorException>(() => viewer.ConnectToAsync(new string('c', 64)));
        await Wait.UntilAsync(async () => (await server.Admin.GetAuditAsync()).Any(e => e.Kind == "session.end"), "the session end to be logged");

        var log = await server.Admin.GetAuditAsync();
        Assert.Equal(log.OrderByDescending(e => e.TimeUtc), log);
        Assert.Contains(log, e => e.Kind == "pair" && e.Result == "ok" && e.DeviceId == hostId.DeviceId);
        Assert.Contains(log, e => e.Kind == "connect" && e.Result == "ok" && e.DeviceId == viewerId.DeviceId && e.PeerDeviceId == hostId.DeviceId);
        Assert.Contains(log, e => e.Kind == "connect" && e.Result == "failed" && e.DeviceId == viewerId.DeviceId);
        Assert.Contains(log, e => e.Kind == "session.end");
        Assert.Contains(log, e => e.Kind == "settings");

        Assert.Equal(2, (await server.Admin.GetAuditAsync(max: 2)).Count);
        var cut = log[1].TimeUtc;
        Assert.All(await server.Admin.GetAuditAsync(since: cut), e => Assert.True(e.TimeUtc > cut));
        using var badSince = await server.RawAdminAsync(HttpMethod.Get, "/admin/v1/audit?since=yesterday-ish", server.AdminToken);
        Assert.Equal(HttpStatusCode.BadRequest, badSince.StatusCode);
    }

    [Fact]
    public async Task The_audit_log_survives_a_restart()
    {
        await using var server = await StartAsync();
        var (c, identity) = await server.PairedDeviceAsync("Before restart", canHost: true);
        await c.DisposeAsync();
        await server.RestartAsync();
        Assert.Contains(await server.Admin.GetAuditAsync(), e => e.Kind == "pair" && e.DeviceId == identity.DeviceId);
    }

    // ---- settings ----

    [Fact]
    public async Task Settings_are_validated_and_the_limits_apply_at_once()
    {
        await using var server = await StartAsync();
        var settings = await server.Admin.GetSettingsAsync();
        Assert.Equal(server.PublicPort, settings.Port);
        Assert.Equal(1000, settings.MaxDevices);
        Assert.Equal(8, settings.MaxSessionsPerDevice);
        Assert.False(settings.RequireTls);

        foreach (var bad in new[]
                 {
                     settings with { Port = 0 }, settings with { Port = 70000 }, settings with { Port = server.AdminPort },
                     settings with { MaxDevices = 0 }, settings with { MaxDevices = 10001 },
                     settings with { MaxSessionsPerDevice = 0 }, settings with { MaxSessionsPerDevice = 65 },
                     settings with { Address = "has space.example.com" }, settings with { Address = "bad_name!.com" }, settings with { Address = "http://x/ws" },
                 })
            Assert.Equal(400, (await Assert.ThrowsAsync<ServerAdminException>(() => server.Admin.SetSettingsAsync(bad))).StatusCode);
        Assert.Equal(settings, await server.Admin.GetSettingsAsync());

        // Device limit: the next device is turned away with a clear message, the code is not used up.
        var result = await server.Admin.UpdateSettingsAsync(settings with { MaxDevices = 1 });
        Assert.False(result.RestartRequired);
        var (first, _) = await server.PairedDeviceAsync("First", canHost: true);
        await using var _f = first;
        var code = await server.NewPairCodeAsync();
        await using var second = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
        var refused = await Assert.ThrowsAsync<CoordinatorException>(() => second.PairAsync(code, "Second", false));
        Assert.Contains("limit", refused.Message);
        await server.Admin.SetSettingsAsync(settings with { MaxDevices = 5 });
        await second.PairAsync(code, "Second", false);
        Assert.True(second.IsPaired);
    }

    [Fact]
    public async Task The_sessions_per_device_limit_applies_without_a_restart()
    {
        await using var server = await StartAsync();
        await server.Admin.SetSettingsAsync((await server.Admin.GetSettingsAsync()) with { MaxSessionsPerDevice = 1 });
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, _) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        await viewer.ConnectToAsync(hostId.DeviceId);
        var ex = await Assert.ThrowsAsync<CoordinatorException>(() => viewer.ConnectToAsync(hostId.DeviceId));
        Assert.Contains("Too many sessions", ex.Message);
    }

    [Fact]
    public async Task A_new_port_is_saved_and_used_after_a_restart_while_the_address_applies_at_once()
    {
        await using var server = await StartAsync();
        var newPort = FreeTcpPort();
        var result = await server.Admin.UpdateSettingsAsync((await server.Admin.GetSettingsAsync()) with { Port = newPort, Address = "myhome.example.com" });
        Assert.True(result.RestartRequired);
        Assert.Contains("port", result.RestartRequiredFor);
        Assert.Equal(server.PublicPort, (await server.Admin.GetStatusAsync()).Port);   // still the running one
        Assert.Equal("myhome.example.com", (await server.Admin.GetStatusAsync()).Address);
        Assert.Equal(newPort, (await server.Admin.GetSettingsAsync()).Port);
        Assert.Equal("myhome.example.com", File.ReadAllText(Path.Combine(server.DataDir, "address.txt")));   // the file set-address writes

        await server.RestartAsync();
        Assert.Equal(newPort, (await server.Admin.GetStatusAsync()).Port);
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{newPort}/healthz")).StatusCode);
    }

    [Fact]
    public async Task A_port_that_is_in_use_is_refused()
    {
        await using var server = await StartAsync();
        using var busy = new System.Net.Sockets.TcpListener(IPAddress.Any, 0);
        busy.Start();
        var port = ((IPEndPoint)busy.LocalEndpoint).Port;
        var ex = await Assert.ThrowsAsync<ServerAdminException>(async () => await server.Admin.SetSettingsAsync((await server.Admin.GetSettingsAsync()) with { Port = port }));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("in use", ex.Message);
    }

    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ---- backup and restore ----

    [Fact]
    public async Task A_backup_restores_the_device_list_after_a_device_was_removed()
    {
        await using var server = await StartAsync();
        var (client, identity) = await server.PairedDeviceAsync("Precious", canHost: true);
        await client.DisposeAsync();
        var folder = Path.Combine(Path.GetTempPath(), "remotely-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = await server.Admin.CreateBackupAsync(folder);
            Assert.StartsWith(folder, file);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(file))
            {
                var names = zip.Entries.Select(e => e.FullName).ToList();
                Assert.Contains("devices.json", names);
                Assert.Contains("manifest.json", names);
                Assert.DoesNotContain(ServerAdminClient.TokenFileName, names);   // the admin token never leaves the data directory
            }

            await server.Admin.RevokeDeviceAsync(identity.DeviceId);
            Assert.Empty(await server.Admin.ListDevicesAsync());

            var result = await server.Admin.RestoreBackupAsync(file);
            Assert.Equal(1, result.DevicesRestored);
            Assert.False(result.PinChanged);
            Assert.False(result.RestartRequired);
            Assert.Equal("Precious", (await server.Admin.ListDevicesAsync()).Single().Name);
            await using var again = await CoordinatorClient.ConnectAsync(server.Options(), identity);
            Assert.True(again.IsPaired);
            Assert.True(Directory.GetFiles(Path.Combine(server.DataDir, "backups"), "before-restore-*.zip").Length == 1);
            Assert.Contains(await server.Admin.GetAuditAsync(), e => e.Kind == "restore" && e.Result == "ok");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Restoring_removes_online_devices_that_are_not_in_the_backup()
    {
        await using var server = await StartAsync();
        var folder = Path.Combine(Path.GetTempPath(), "remotely-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = await server.Admin.CreateBackupAsync(folder);   // an empty server
            var (late, _) = await server.PairedDeviceAsync("Joined after the backup", canHost: true);
            await using var _l = late;
            await server.Admin.RestoreBackupAsync(file);
            await Wait.UntilAsync(() => !late.IsConnected, "the device that is not in the backup to be disconnected");
            Assert.Empty(await server.Admin.ListDevicesAsync());
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Restoring_a_backup_with_another_server_key_warns_about_the_pin_and_takes_effect_for_new_connections()
    {
        await using var a = await StartAsync(tls: true);
        await using var b = await StartAsync(tls: true);
        Assert.NotEqual(a.Pin, b.Pin);
        var folder = Path.Combine(Path.GetTempPath(), "remotely-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fromB = await b.Admin.CreateBackupAsync(folder);
            var result = await a.Admin.RestoreBackupAsync(fromB);

            Assert.True(result.PinChanged);
            Assert.Equal(a.Pin, result.OldPin);
            Assert.Equal(b.Pin, result.NewPin);
            Assert.NotNull(result.Warning);
            Assert.Equal(b.Pin, (await a.Admin.GetStatusAsync()).CertificatePin);
            await using var withNew = await CoordinatorClient.ConnectAsync(a.Options(pinOverride: b.Pin), DeviceIdentity.Generate());
            await Assert.ThrowsAnyAsync<Exception>(() => CoordinatorClient.ConnectAsync(a.Options(pinOverride: a.Pin), DeviceIdentity.Generate()));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Things_that_are_not_server_backups_are_refused_and_change_nothing()
    {
        await using var server = await StartAsync();
        var (c, identity) = await server.PairedDeviceAsync("Safe", canHost: true);
        await using var _c = c;
        var folder = Path.Combine(Path.GetTempPath(), "remotely-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string Zip(params (string Name, string Text)[] entries)
            {
                var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip");
                using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
                foreach (var (name, text) in entries)
                    using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
                return path;
            }

            var garbage = Path.Combine(folder, "garbage.zip");
            File.WriteAllText(garbage, "this is not a zip file");
            var manifest = """{"format":1,"createdUtc":"2026-01-01T00:00:00+00:00","serverVersion":"x","pin":""}""";
            var cases = new[]
            {
                garbage,
                Zip(("manifest.json", manifest)),                                                                     // no device list
                Zip(("manifest.json", manifest), ("devices.json", "[1,2,3]")),                                        // not a device list
                Zip(("manifest.json", manifest), ("devices.json", """{"schemaVersion":99,"devices":[]}""")),          // from the future
                Zip(("manifest.json", manifest), ("devices.json", "[]"), ("../evil.txt", "x")),                       // path trick
                Zip(("manifest.json", """{"format":99}"""), ("devices.json", "[]")),                                  // newer backup format
                Zip(("manifest.json", manifest), ("devices.json", "[]"), ("settings.json", """{"schemaVersion":99}""")),
                Zip(("manifest.json", manifest), ("devices.json", "[]"), ("server.pfx", "not a certificate")),
            };
            foreach (var bad in cases)
                Assert.Equal(400, (await Assert.ThrowsAsync<ServerAdminException>(() => server.Admin.RestoreBackupAsync(bad))).StatusCode);

            Assert.Equal(identity.DeviceId, (await server.Admin.ListDevicesAsync()).Single().DeviceId);
            Assert.True(c.IsConnected);
            Assert.False(File.Exists(Path.Combine(folder, "..", "evil.txt")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // ---- certificate ----

    [Fact]
    public async Task Renewing_with_the_same_key_keeps_the_pin_and_renewing_with_a_new_key_needs_confirmation()
    {
        await using var server = await StartAsync(tls: true);
        var before = await server.Admin.GetStatusAsync();
        var identity = DeviceIdentity.Generate();
        await using (var c = await CoordinatorClient.ConnectAsync(server.Options(), identity))
            await c.PairAsync(await server.NewPairCodeAsync(), "Pinned", true);

        var same = await server.Admin.RenewCertificateAsync();
        Assert.False(same.PinChanged);
        Assert.Null(same.Warning);
        Assert.Equal(server.Pin, same.NewPin);
        Assert.True(same.ExpiresUtc >= before.CertificateExpiresUtc);
        await using (var still = await CoordinatorClient.ConnectAsync(server.Options(), identity))
            Assert.True(still.IsPaired);

        var ex = await Assert.ThrowsAsync<ServerAdminException>(() => server.Admin.RenewCertificateAsync(newKey: true));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("pin", ex.Message);
        Assert.Equal(server.Pin, (await server.Admin.GetStatusAsync()).CertificatePin);

        var changed = await server.Admin.RenewCertificateAsync(newKey: true, confirmPinChange: true);
        Assert.True(changed.PinChanged);
        Assert.NotNull(changed.Warning);
        Assert.NotEqual(server.Pin, changed.NewPin);
        Assert.Equal(changed.NewPin, (await server.Admin.GetStatusAsync()).CertificatePin);
        await Assert.ThrowsAnyAsync<Exception>(() => CoordinatorClient.ConnectAsync(server.Options(), identity));    // the old pin is refused
        await using var fresh = await CoordinatorClient.ConnectAsync(server.Options(pinOverride: changed.NewPin), identity);
        Assert.True(fresh.IsPaired);
        Assert.Equal(2, (await server.Admin.GetAuditAsync()).Count(e => e.Kind == "cert.renew"));
        Assert.True(File.Exists(Path.Combine(server.DataDir, "server.pfx.prev")));
    }

    // ---- shutdown ----

    [Fact]
    public async Task Stopping_the_server_tells_devices_and_finishes_quickly_and_keeps_last_seen_times()
    {
        var server = await StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = host;
        var (viewer, _) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;
        var relay = await viewer.ConnectToAsync(hostId.DeviceId);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await server.StopAsync();
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"stopping took {watch.Elapsed}");
        await Wait.UntilAsync(() => !host.IsConnected && !viewer.IsConnected, "devices to see the server leave");
        Assert.Null(await relay.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
        var onDisk = DeviceStore.Parse(File.ReadAllBytes(Path.Combine(server.DataDir, "devices.json")), "devices.json").Devices;
        Assert.All(onDisk, d => Assert.NotNull(d.LastSeenUtc));
        await server.DisposeAsync();
    }

    // ---- IPv6 ----

    [Fact]
    public async Task The_server_answers_on_IPv6_where_the_machine_has_it()
    {
        if (!Ipv6Loopback())
        {
            // Nothing to test here: without a usable ::1 this machine cannot show IPv6 listening (the CI run says whether the runner could).
            return;
        }
        await using var server = await StartAsync();
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://[::1]:{server.PublicPort}/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{server.PublicPort}/healthz")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://[::1]:{server.AdminPort}/admin/v1/status");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(request)).StatusCode);

        var identity = DeviceIdentity.Generate();
        var options = new CoordinatorOptions
        {
            ServerUri = new Uri($"ws://[::1]:{server.PublicPort}/ws"), AllowInsecureTransport = true, RequestTimeout = TimeSpan.FromSeconds(10),
        };
        await using var client = await CoordinatorClient.ConnectAsync(options, identity);
        await client.PairAsync(await server.NewPairCodeAsync(), "Over IPv6", false);
        Assert.True(client.IsPaired);
    }

    private static bool Ipv6Loopback()
    {
        if (!System.Net.Sockets.Socket.OSSupportsIPv6) return false;
        try
        {
            using var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetworkV6, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return true;
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    [Theory]
    [InlineData("home.example.com", true, 8443, "wss://home.example.com:8443/ws")]
    [InlineData("home.example.com:9000", true, 8443, "wss://home.example.com:9000/ws")]
    [InlineData("192.168.1.5", false, 8443, "ws://192.168.1.5:8443/ws")]
    [InlineData("2001:db8::1", true, 8443, "wss://[2001:db8::1]:8443/ws")]
    [InlineData("[2001:db8::1]", true, 8443, "wss://[2001:db8::1]:8443/ws")]
    [InlineData("[2001:db8::1]:9000", true, 8443, "wss://[2001:db8::1]:9000/ws")]
    [InlineData("wss://x.example.com/ws", true, 8443, "wss://x.example.com/ws")]
    public void Addresses_become_the_address_in_the_invite_including_IPv6_literals(string given, bool tls, int port, string expected)
    {
        var uri = ServerAddress.BuildUri(tls, port, given);
        Assert.Equal(expected, uri);
        Assert.True(Uri.TryCreate(uri, UriKind.Absolute, out _));
        Assert.Null(ServerAddress.Validate(given, tls, port));
    }
}
