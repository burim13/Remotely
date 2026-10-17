using System.Net;
using System.Net.Sockets;
using System.Text;
using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.Core.Lan.Consent;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.Tests;

/// <summary>The controller side of consent and remembered PCs, against the real LanHost, ShareCoordinator and consent classes over real sockets.</summary>
public class LanControllerFlowTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public required LanHost Host;
        public required ShareCoordinator Share;
        public required DeviceIdentity HostIdentity;
        public required HostCoreTests.FakeScreen Screen;
        public Func<ConsentRequest, CancellationToken, Task<ConsentDecision>> Prompt = (_, _) => Task.FromResult(ConsentDecision.Deny);
        public int Asked;

        public static async Task<Rig> StartAsync(TimeSpan? promptTimeout = null)
        {
            var input = new HostCoreTests.RecordingInput();
            var screen = new HostCoreTests.FakeScreen(() => new FrameInfo(800, 600, "Default", Encoding.ASCII.GetBytes("jpeg")));
            var services = new HostServices(screen) { Input = input, IsTrusted = _ => true, CanControl = _ => true };
            var agent = new HostAgent(DeviceIdentity.Generate(), services, _ => { });
            var probe = new UdpClient(0);
            var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            probe.Dispose();
            Rig? rig = null;
            var share = new ShareCoordinator(null, (req, ct) => { Interlocked.Increment(ref rig!.Asked); return rig!.Prompt(req, ct); });
            if (promptTimeout is { } t) share.Options.PromptTimeout = t;
            var identity = DeviceIdentity.Generate();
            var host = new LanHost(new LanHostOptions { TcpPort = 0, DiscoveryPort = port, AllowLoopback = true, HostName = "host-pc", Identity = identity, Consent = share.Gate },
                (channel, name, ct) => agent.ServeChannelAsync(channel, name, control: true, ct), _ => { });
            host.Start();
            share.Attach(host);
            rig = new Rig { Host = host, Share = share, HostIdentity = identity, Screen = screen };
            await Task.Delay(50);
            return rig;
        }

        public string Address => "127.0.0.1:" + Host.TcpPort;

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Share.Detach();
            HostIdentity.Dispose();
        }
    }

    private static CancellationToken Limit => new CancellationTokenSource(TimeSpan.FromSeconds(40)).Token;

    private sealed class Steps(List<LanConnectStep> list) : IProgress<LanConnectStep>
    {
        public void Report(LanConnectStep value) { lock (list) list.Add(value); }
    }

    [Fact]
    public async Task Always_allow_gives_the_host_key_and_the_PC_then_reconnects_without_a_token_and_gets_frames()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AlwaysAllow);
        using var me = DeviceIdentity.Generate();

        var first = await LanControllerFlow.ConnectWithTokenAsync(rig.Host.CurrentToken.Display, "my-pc", rig.Address, me, Limit);
        Assert.True(first.Remembered);
        Assert.NotNull(first.Host);
        Assert.Equal(rig.HostIdentity.DeviceId, first.Host!.DeviceId);
        Assert.Equal("host-pc", first.Host.Name);
        first.Channel.Dispose();

        var store = new JsonRecentPcs(Path.Combine(Directory.CreateTempSubdirectory().FullName, "r.json"));
        store.Record(first.HostName, first.Endpoint.ToString(), first.Remembered ? first.Host.DeviceId : null);
        var saved = Assert.Single(store.List());
        Assert.Equal(rig.HostIdentity.DeviceId, saved.HostDeviceId);

        await Task.Delay(300);   // the host frees its slot when the first session ends
        var asked = rig.Asked;
        var again = await LanControllerFlow.ResumeAsync(saved, me, Limit);
        Assert.True(again.Resumed);
        await using var session = await ViewerSession.OpenAsync(again.Channel, again.HostName, Limit);
        var before = rig.Screen.Captures;
        var until = DateTime.UtcNow.AddSeconds(15);
        while (rig.Screen.Captures <= before && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(rig.Screen.Captures > before);
        Assert.Equal(asked, rig.Asked);   // no question the second time
    }

    [Fact]
    public async Task Allow_once_is_not_remembered_and_leaves_no_device_id_in_the_recent_list()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AllowOnce);
        using var me = DeviceIdentity.Generate();
        var c = await LanControllerFlow.ConnectWithTokenAsync(rig.Host.CurrentToken.Display, "my-pc", rig.Address, me, Limit);
        c.Channel.Dispose();
        Assert.False(c.Remembered);
        Assert.NotNull(c.Host);
        var store = new JsonRecentPcs(Path.Combine(Directory.CreateTempSubdirectory().FullName, "r.json"));
        store.Record("host-pc", c.Endpoint.ToString(), "stale-id");
        store.Record("host-pc", c.Endpoint.ToString(), c.Remembered ? c.Host!.DeviceId : null);
        Assert.Null(Assert.Single(store.List()).HostDeviceId);
        await Assert.ThrowsAsync<LanConnectException>(() => LanControllerFlow.ResumeAsync(store.List()[0], me, Limit));
    }

    [Fact]
    public async Task Deny_is_reported_in_plain_language_and_no_frame_is_captured()
    {
        await using var rig = await Rig.StartAsync();
        using var me = DeviceIdentity.Generate();
        var ex = await Assert.ThrowsAsync<ConsentDeniedException>(() => LanControllerFlow.ConnectWithTokenAsync(rig.Host.CurrentToken.Display, "my-pc", rig.Address, me, Limit));
        Assert.Equal(LanControllerFlow.DeniedMessage, ex.Message);
        Assert.Contains("Deny", ex.Message);
        Assert.Equal(0, rig.Screen.Captures);
    }

    [Fact]
    public async Task Waiting_for_the_other_person_may_take_longer_than_the_connection_timeout_and_says_so()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = async (_, ct) => { await Task.Delay(2500, ct); return ConsentDecision.AllowOnce; };
        using var me = DeviceIdentity.Generate();
        var steps = new List<LanConnectStep>();
        var c = await LanControllerFlow.ConnectWithTokenAsync(rig.Host.CurrentToken.Display, "my-pc", rig.Address, me, Limit, new Steps(steps),
            connectTimeout: TimeSpan.FromSeconds(1.5), consentWait: TimeSpan.FromSeconds(20));
        c.Channel.Dispose();
        Assert.Contains(LanConnectStep.WaitingForApproval, steps);
        Assert.Equal(LanConnectStep.Verifying, steps[^2]);
    }

    [Fact]
    public async Task Nobody_answering_within_the_wait_is_a_clear_message_not_a_hang()
    {
        await using var rig = await Rig.StartAsync(promptTimeout: TimeSpan.FromSeconds(30));
        rig.Prompt = (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => ConsentDecision.AllowOnce, TaskScheduler.Default);
        using var me = DeviceIdentity.Generate();
        var ex = await Assert.ThrowsAsync<LanConnectException>(() => LanControllerFlow.ConnectWithTokenAsync(rig.Host.CurrentToken.Display, "my-pc", rig.Address, me, Limit,
            consentWait: TimeSpan.FromMilliseconds(600)));
        Assert.Contains("did not answer the question in time", ex.Message);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_approval_stays_a_cancel()
    {
        await using var rig = await Rig.StartAsync(promptTimeout: TimeSpan.FromSeconds(30));
        rig.Prompt = (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => ConsentDecision.AllowOnce, TaskScheduler.Default);
        using var me = DeviceIdentity.Generate();
        using var cts = new CancellationTokenSource();
        var steps = new List<LanConnectStep>();
        var attempt = LanControllerFlow.ConnectWithTokenAsync(rig.Host.CurrentToken.Display, "my-pc", rig.Address, me, cts.Token, new Steps(steps));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!steps.Contains(LanConnectStep.WaitingForApproval) && DateTime.UtcNow < until) await Task.Delay(20);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_PC_that_was_removed_from_the_remembered_list_is_refused_with_a_sentence()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AlwaysAllow);
        using var me = DeviceIdentity.Generate();
        var c = await LanControllerFlow.ConnectWithTokenAsync(rig.Host.CurrentToken.Display, "my-pc", rig.Address, me, Limit);
        c.Channel.Dispose();
        foreach (var d in rig.Share.Remembered.All.ToList()) rig.Share.Remembered.Remove(d.DeviceId);
        await Task.Delay(300);
        var pc = new RecentPc("host-pc", c.Endpoint.ToString(), DateTimeOffset.UtcNow, c.Host!.DeviceId);
        await Assert.ThrowsAsync<LanConnectException>(() => LanControllerFlow.ResumeAsync(pc, me, Limit));
    }

    [Fact]
    public async Task A_different_PC_answering_at_the_saved_address_is_refused()
    {
        await using var rig = await Rig.StartAsync();
        using var me = DeviceIdentity.Generate();
        var pc = new RecentPc("host-pc", rig.Address, DateTimeOffset.UtcNow, DeviceIdentity.Generate().DeviceId);
        await Assert.ThrowsAsync<LanConnectException>(() => LanControllerFlow.ResumeAsync(pc, me, Limit));
    }

    [Fact]
    public async Task Without_a_device_key_the_controller_connects_like_the_first_release_and_the_host_can_only_allow_once()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (req, _) => Task.FromResult(req.CanRemember ? ConsentDecision.AlwaysAllow : ConsentDecision.AllowOnce);
        var c = await LanControllerFlow.ConnectWithTokenAsync(rig.Host.CurrentToken.Display, "old-pc", rig.Address, null, Limit);
        await using var session = await ViewerSession.OpenAsync(c.Channel, "host", Limit);
        Assert.Null(c.Host);
        Assert.False(c.Remembered);
        Assert.Empty(rig.Share.Remembered.All);
    }

    [Fact]
    public async Task Resume_needs_a_saved_host_id_and_a_valid_address()
    {
        using var me = DeviceIdentity.Generate();
        await Assert.ThrowsAsync<LanConnectException>(() => LanControllerFlow.ResumeAsync(new RecentPc("x", "10.0.0.2:47810", DateTimeOffset.UtcNow), me, CancellationToken.None));
        await Assert.ThrowsAsync<LanConnectException>(() => LanControllerFlow.ResumeAsync(new RecentPc("x", "banana", DateTimeOffset.UtcNow, "id"), me, CancellationToken.None));
    }

    [Fact]
    public void A_recent_list_written_before_host_ids_existed_still_loads()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "r.json");
            File.WriteAllText(path, "[{\"Name\":\"Kitchen\",\"Address\":\"192.168.1.20:47810\",\"LastUsedUtc\":\"2026-01-01T00:00:00+00:00\"}]");
            var pc = Assert.Single(new JsonRecentPcs(path).List());
            Assert.Equal("Kitchen", pc.Name);
            Assert.Null(pc.HostDeviceId);
        }
        finally { dir.Delete(true); }
    }
}
