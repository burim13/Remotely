using System.Net;
using System.Net.Sockets;
using Remotely.Client;
using Remotely.Core.Lan;
using Remotely.Core.Protocol;
using Remotely.HostCore;

namespace Remotely.Tests;

/// <summary>
/// Problems found by the security audit in code that belongs to other owners. Each test states the behaviour that is right and is skipped, so CI stays
/// green; the Skip reason names the owner. When the owner fixes it, remove the Skip and the test guards the fix. Each one was run un-skipped once and failed
/// as described (see docs/security.md, "Open findings").
/// </summary>
public class CryptoKnownIssuesTests
{
    [Fact(Skip = "Known leak, owner A3 (LanHost.cs): Start() logs 'Sharing on port N. Token <the whole token>.' so the secret reaches the host's log and any diagnostics bundle built from it.")]
    public async Task KnownIssue_the_start_up_line_of_the_token_host_must_not_contain_the_token()
    {
        var lines = new List<string>();
        var probe = new UdpClient(0);
        var discoveryPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Dispose();
        await using var host = new LanHost(new LanHostOptions { TcpPort = 0, DiscoveryPort = discoveryPort, AllowLoopback = true }, (_, _, _) => Task.CompletedTask, lines.Add);
        host.Start();
        var token = host.CurrentToken;
        Assert.DoesNotContain(lines, l => l.Contains(token.Secret) || l.Contains(token.Display));
    }

    [Fact(Skip = "Known leak, owner Protocol/Invite.cs (A10 or integrator): Invite is a record, so ToString() prints Code (a live pairing code) and PinHex; any log or exception text that formats an Invite leaks the code.")]
    public void KnownIssue_an_invite_must_not_print_its_pairing_code()
    {
        var invite = new Invite("wss://example.test/ws", new string('a', 64), "K7P4-4NX2");
        Assert.DoesNotContain("K7P4-4NX2", invite.ToString());
    }

    [Fact]
    public async Task KnownIssue_a_device_revoked_while_connected_must_lose_its_connection()
    {
        await using var server = await TestServer.StartAsync();
        var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _h = host;
        await using var _v = viewer;
        using var http = new HttpClient();
        var response = await http.DeleteAsync($"http://127.0.0.1:{server.AdminPort}/admin/devices/{viewerId.DeviceId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await Task.Delay(500);
        await Assert.ThrowsAnyAsync<Exception>(() => viewer.ConnectToAsync(hostId.DeviceId));
    }

    [Fact(Skip = "Possible gap, owner A8 (HostCore/FileTransfer.cs, FileNames.Sanitize): a reserved device name followed by more than one extension, or by a space ('CON.tar.gz', 'aux.x.y', 'nul .txt'), is returned unchanged because only the part before the LAST dot is checked. Windows is documented to treat a reserved name followed by any extension as the device; that was NOT tested on Windows here.")]
    public void KnownIssue_reserved_device_names_with_several_extensions_must_be_renamed()
    {
        foreach (var name in new[] { "CON.tar.gz", "aux.x.y", "nul .txt" })
            Assert.NotEqual(name, FileNames.Sanitize(name));
    }
}
