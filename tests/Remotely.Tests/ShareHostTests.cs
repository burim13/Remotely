using System.Net;
using System.Net.Sockets;
using System.Text;
using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.Core.Lan.Consent;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.Tests;

public class ShareStateMachineTests
{
    [Fact]
    public void The_normal_path_goes_Off_Ready_Connecting_AwaitingConsent_InSession_Ready_Off()
    {
        var sm = new ShareStateMachine();
        var seen = new List<ShareState>();
        sm.Changed += s => seen.Add(s.State);
        Assert.Equal(ShareState.Off, sm.Current.State);
        Assert.True(sm.Start());
        Assert.True(sm.BeginHandshake("192.168.1.9"));
        Assert.True(sm.RequestConsent("Anna", "192.168.1.9"));
        Assert.True(sm.BeginSession("Anna", "192.168.1.9"));
        Assert.Equal("Anna", sm.Current.Controller);
        Assert.NotNull(sm.Current.SinceUtc);
        Assert.True(sm.EndSession());
        Assert.True(sm.Stop());
        Assert.Equal([ShareState.Ready, ShareState.Connecting, ShareState.AwaitingConsent, ShareState.InSession, ShareState.Ready, ShareState.Off], seen);
    }

    [Fact]
    public void Moves_that_make_no_sense_are_refused_and_change_nothing()
    {
        var sm = new ShareStateMachine();
        var changes = 0;
        sm.Changed += _ => changes++;
        Assert.False(sm.BeginHandshake("x"));      // not sharing
        Assert.False(sm.EndSession());
        Assert.False(sm.Stop());                   // already off
        Assert.False(sm.SetControlPaused(true));
        sm.Start();
        Assert.False(sm.Start());                  // already ready
        Assert.False(sm.SetControlPaused(true));   // nobody in a session
        Assert.False(sm.ConsentDenied());
        Assert.False(sm.HandshakeFailed());
        sm.BeginHandshake("a");
        Assert.False(sm.BeginHandshake("b"));      // one at a time
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Denied_or_failed_attempts_return_to_Ready_and_a_late_event_after_Stop_is_ignored()
    {
        var sm = new ShareStateMachine();
        sm.Start();
        sm.BeginHandshake("a"); sm.RequestConsent("Anna", "a");
        Assert.True(sm.ConsentDenied());
        Assert.Equal(ShareState.Ready, sm.Current.State);
        sm.BeginHandshake("a");
        Assert.True(sm.HandshakeFailed());
        sm.BeginHandshake("a");
        sm.Stop();
        Assert.False(sm.HandshakeFailed());
        Assert.Equal(ShareState.Off, sm.Current.State);
    }

    [Fact]
    public void Pause_only_exists_inside_a_session_and_is_cleared_when_it_ends()
    {
        var sm = new ShareStateMachine();
        sm.Start(); sm.BeginSession("Anna", "a");
        Assert.True(sm.SetControlPaused(true));
        Assert.Equal("Share_State_InSessionPaused", sm.Current.ResourceKey);
        Assert.False(sm.SetControlPaused(true));
        sm.EndSession(); sm.BeginSession("Ben", "b");
        Assert.False(sm.Current.ControlPaused);
    }

    [Fact]
    public void Error_is_reachable_from_anywhere_and_Start_leaves_it()
    {
        var sm = new ShareStateMachine();
        Assert.True(sm.Fail("port busy"));
        Assert.Equal("port busy", sm.Current.ErrorText);
        Assert.Equal("Share_State_Error", sm.Current.ResourceKey);
        Assert.True(sm.Start());
        Assert.Null(sm.Current.ErrorText);
        sm.BeginSession("Anna", "a");
        Assert.True(sm.Fail("network went away"));
        Assert.True(sm.Stop());
    }

    [Fact]
    public void Every_state_has_a_resource_key()
    {
        foreach (var state in Enum.GetValues<ShareState>())
            Assert.StartsWith("Share_State_", new ShareSnapshot(state, null, null, null, null, false).ResourceKey);
    }
}

public class ConsentRuleTests
{
    private static readonly DeviceIdentity Anna = DeviceIdentity.Generate();

    private static ConsentGate Gate(Func<ConsentRequest, CancellationToken, Task<ConsentDecision>>? prompt, out RememberedDevices store, ConsentOptions? options = null)
    {
        store = new RememberedDevices(null);
        return new ConsentGate(options ?? new ConsentOptions { PromptTimeout = TimeSpan.FromMilliseconds(300) }, store, prompt);
    }

    [Fact]
    public async Task A_new_pc_is_asked_and_deny_means_no()
    {
        var asked = 0;
        var gate = Gate((_, _) => { asked++; return Task.FromResult(ConsentDecision.Deny); }, out _);
        var r = await gate.DecideAsync("Anna", "10.0.0.2", Anna.PublicKey, Anna.DeviceId, default);
        Assert.False(r.Allowed);
        Assert.Equal(ConsentOutcome.Denied, r.Outcome);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task Allow_once_lets_in_but_does_not_remember()
    {
        var gate = Gate((_, _) => Task.FromResult(ConsentDecision.AllowOnce), out var store);
        var r = await gate.DecideAsync("Anna", "10.0.0.2", Anna.PublicKey, Anna.DeviceId, default);
        Assert.Equal(ConsentOutcome.AllowedOnce, r.Outcome);
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Always_allow_remembers_the_key_and_the_next_connection_is_not_asked()
    {
        var asked = 0;
        var gate = Gate((_, _) => { asked++; return Task.FromResult(ConsentDecision.AlwaysAllow); }, out var store);
        Assert.Equal(ConsentOutcome.AllowedAlways, (await gate.DecideAsync("Anna", "10.0.0.2", Anna.PublicKey, Anna.DeviceId, default)).Outcome);
        Assert.True(store.Contains(Anna.DeviceId));
        Assert.Equal(ConsentOutcome.AllowedRemembered, (await gate.DecideAsync("Anna", "10.0.0.2", Anna.PublicKey, Anna.DeviceId, default)).Outcome);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task Always_allow_without_a_proven_key_is_only_allow_once()
    {
        var gate = Gate((req, _) => { Assert.False(req.CanRemember); return Task.FromResult(ConsentDecision.AlwaysAllow); }, out var store);
        var r = await gate.DecideAsync("Old app", "10.0.0.2", null, null, default);
        Assert.Equal(ConsentOutcome.AllowedOnce, r.Outcome);
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task A_different_key_with_the_same_name_is_not_recognised()
    {
        var gate = Gate((_, _) => Task.FromResult(ConsentDecision.Deny), out var store);
        store.Add(Anna.PublicKey, "Anna", DateTimeOffset.UtcNow);
        using var other = DeviceIdentity.Generate();
        var r = await gate.DecideAsync("Anna", "10.0.0.3", other.PublicKey, other.DeviceId, default);
        Assert.Equal(ConsentOutcome.Denied, r.Outcome);
    }

    [Fact]
    public async Task Nobody_answering_in_time_means_no()
    {
        var gate = Gate((_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => ConsentDecision.AllowOnce, TaskScheduler.Default), out _);
        Assert.Equal(ConsentOutcome.TimedOut, (await gate.DecideAsync("Anna", "a", Anna.PublicKey, Anna.DeviceId, default)).Outcome);
    }

    [Fact]
    public async Task With_no_way_to_ask_an_unknown_pc_is_refused_and_a_failing_prompt_is_a_refusal()
    {
        Assert.Equal(ConsentOutcome.Denied, (await Gate(null, out _).DecideAsync("Anna", "a", Anna.PublicKey, Anna.DeviceId, default)).Outcome);
        var broken = Gate((_, _) => throw new InvalidOperationException("window failed"), out _);
        Assert.Equal(ConsentOutcome.Denied, (await broken.DecideAsync("Anna", "a", Anna.PublicKey, Anna.DeviceId, default)).Outcome);
    }

    [Fact]
    public async Task Switching_asking_off_lets_in_without_a_question_but_never_remembers()
    {
        var gate = Gate(null, out var store, new ConsentOptions { AskBeforeConnect = false });
        var r = await gate.DecideAsync("Anna", "a", Anna.PublicKey, Anna.DeviceId, default);
        Assert.Equal(ConsentOutcome.AllowedWithoutAsking, r.Outcome);
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Switching_remembered_pcs_off_asks_again_even_for_a_remembered_pc()
    {
        var asked = 0;
        var gate = Gate((_, _) => { asked++; return Task.FromResult(ConsentDecision.AllowOnce); }, out var store);
        store.Add(Anna.PublicKey, "Anna", DateTimeOffset.UtcNow);
        gate.Options.AllowRemembered = false;
        await gate.DecideAsync("Anna", "a", Anna.PublicKey, Anna.DeviceId, default);
        Assert.Equal(1, asked);
    }
}

public class RememberedAndHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remotely-share-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Remembered_pcs_survive_a_restart_and_revoking_removes_them_for_good()
    {
        using var anna = DeviceIdentity.Generate();
        var path = Path.Combine(_dir, "r.json");
        var a = new RememberedDevices(path);
        Assert.True(a.Add(anna.PublicKey, "  Anna\u0007's laptop ", DateTimeOffset.UtcNow));
        Assert.Equal("Anna's laptop", new RememberedDevices(path).All.Single().Name);
        Assert.True(new RememberedDevices(path).Contains(anna.DeviceId));
        Assert.True(a.Remove(anna.DeviceId));
        Assert.False(a.Remove(anna.DeviceId));
        Assert.False(new RememberedDevices(path).Contains(anna.DeviceId));
    }

    [Fact]
    public void An_edited_entry_whose_id_does_not_match_its_key_and_a_damaged_file_are_ignored()
    {
        using var anna = DeviceIdentity.Generate();
        using var mallory = DeviceIdentity.Generate();
        var path = Path.Combine(_dir, "r.json");
        Directory.CreateDirectory(_dir);
        var forged = $"{{\"Version\":1,\"Devices\":[{{\"DeviceId\":\"{anna.DeviceId}\",\"PublicKey\":\"{Convert.ToBase64String(mallory.PublicKey)}\",\"Name\":\"Anna\",\"AddedUtc\":\"2026-01-01T00:00:00+00:00\"}}]}}";
        File.WriteAllText(path, forged);
        Assert.Empty(new RememberedDevices(path).All);
        File.WriteAllText(path, "{ not json");
        Assert.Empty(new RememberedDevices(path).All);
    }

    [Fact]
    public void The_list_is_capped()
    {
        var store = new RememberedDevices(null);
        for (var i = 0; i < RememberedDevices.MaxEntries; i++) { using var d = DeviceIdentity.Generate(); Assert.True(store.Add(d.PublicKey, "pc" + i, DateTimeOffset.UtcNow)); }
        using var extra = DeviceIdentity.Generate();
        Assert.False(store.Add(extra.PublicKey, "one too many", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void History_records_sessions_refusals_and_marks_an_unfinished_one_as_app_closed_after_a_restart()
    {
        var path = Path.Combine(_dir, "h.json");
        var t0 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var h = new SessionHistory(path);
        var done = h.Begin("Anna", "id1", "10.0.0.2", "token", "AllowedOnce", t0);
        h.End(done, SessionEndReasons.ControllerLeft, t0.AddMinutes(5));
        h.AddRefused("Mallory", null, "10.0.0.9", "token", "Denied", t0.AddMinutes(6));
        h.Begin("Ben", "id2", "10.0.0.3", "remembered", "AllowedRemembered", t0.AddMinutes(7));
        var reloaded = new SessionHistory(path).All;
        Assert.Equal(["Ben", "Mallory", "Anna"], reloaded.Select(r => r.ControllerName));
        Assert.Equal(SessionEndReasons.AppClosed, reloaded[0].EndReason);
        Assert.Equal(SessionEndReasons.ControllerLeft, reloaded[2].EndReason);
        Assert.Equal(t0.AddMinutes(5), reloaded[2].EndedUtc);
        h.Clear();
        Assert.Empty(new SessionHistory(path).All);
    }

    [Fact]
    public void History_is_capped_and_a_missing_directory_is_created()
    {
        var h = new SessionHistory(Path.Combine(_dir, "deep", "h.json"));
        for (var i = 0; i < SessionHistory.MaxEntries + 10; i++) h.AddRefused("pc", null, "a", "token", "Denied", DateTimeOffset.UtcNow);
        Assert.Equal(SessionHistory.MaxEntries, h.All.Count);
        Assert.Equal(SessionHistory.MaxEntries, new SessionHistory(Path.Combine(_dir, "deep", "h.json")).All.Count);
    }

    [Fact]
    public void Settings_persist_between_runs()
    {
        var a = new ShareCoordinator(_dir, null);
        Assert.True(a.Options.AskBeforeConnect);
        a.SetAskBeforeConnect(false);
        a.SetAllowRemembered(false);
        var b = new ShareCoordinator(_dir, null);
        Assert.False(b.Options.AskBeforeConnect);
        Assert.False(b.Options.AllowRemembered);
    }
}

public class ControllerHelloAndPauseTests
{
    [Fact]
    public void The_marker_round_trips_fits_the_name_limit_and_an_old_name_means_no_proof()
    {
        var (name, proof) = ControllerHello.Parse(ControllerHello.Encode("Anna's PC"));
        Assert.Equal("Anna's PC", name);
        Assert.True(proof);
        Assert.True(ControllerHello.Encode(new string('x', 200)).Length <= ControllerHello.MaxNameLength);
        Assert.Equal(("Old PC", false), ControllerHello.Parse("Old PC"));
        Assert.Equal(("Unnamed PC", true), ControllerHello.Parse(ControllerHello.Encode("  ")));
    }

    [Fact]
    public void The_pause_filter_uses_the_same_message_kinds_as_the_host_protocol()
    {
        Assert.Equal(HostMessages.Input, HostSessionChannel.KindInput);
        Assert.Equal(HostMessages.Clipboard, HostSessionChannel.KindClipboard);
        Assert.Equal(HostMessages.SecureAttention, HostSessionChannel.KindSecureAttention);
        Assert.Equal(HostMessages.FileStart, HostSessionChannel.KindFileFirst);
        Assert.Equal(HostMessages.ShareGet, HostSessionChannel.KindFileLast);
        Assert.Equal(HostMessages.Error, HostSessionChannel.KindError);
        foreach (var kind in new[] { HostMessages.Input, HostMessages.Clipboard, HostMessages.SecureAttention, HostMessages.FileStart, HostMessages.FileChunk, HostMessages.FileEnd, HostMessages.ShareList, HostMessages.ShareGet })
            Assert.True(HostSessionChannel.ActsOnThisPc(kind), kind.ToString());
        Assert.False(HostSessionChannel.ActsOnThisPc(HostMessages.FrameRequest));
    }
}

/// <summary>The real host over real sockets (loopback), with a recording screen and input, driven the way the app drives it.</summary>
public class ShareHostIntegrationTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public required LanHost Host;
        public required ShareCoordinator Share;
        public required HostCoreTests.RecordingInput Input;
        public required HostCoreTests.FakeScreen Screen;
        public required DeviceIdentity HostIdentity;
        public Func<ConsentRequest, CancellationToken, Task<ConsentDecision>> Prompt = (_, _) => Task.FromResult(ConsentDecision.Deny);
        public List<ConsentRequest> Asked = [];
        public CancellationTokenSource Cts = new(TimeSpan.FromSeconds(40));

        public static async Task<Rig> StartAsync(bool ask = true, bool allowResume = true, TimeSpan? promptTimeout = null)
        {
            var input = new HostCoreTests.RecordingInput();
            var screen = new HostCoreTests.FakeScreen(() => new FrameInfo(800, 600, "Default", Encoding.ASCII.GetBytes("jpeg")));
            var services = new HostServices(screen) { Input = input, IsTrusted = _ => true, CanControl = _ => true };
            var agent = new HostAgent(DeviceIdentity.Generate(), services, _ => { });
            var probe = new UdpClient(0);
            var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            probe.Dispose();
            Rig? rig = null;
            var share = new ShareCoordinator(null, (req, ct) => { lock (rig!.Asked) rig.Asked.Add(req); return rig.Prompt(req, ct); });
            share.Options.AskBeforeConnect = ask;
            if (promptTimeout is { } t) share.Options.PromptTimeout = t;
            var identity = DeviceIdentity.Generate();
            var host = new LanHost(new LanHostOptions { TcpPort = 0, DiscoveryPort = port, AllowLoopback = true, HostName = "host-pc", Identity = identity, Consent = share.Gate, AllowResume = allowResume },
                (channel, name, ct) => agent.ServeChannelAsync(channel, name, control: true, ct), _ => { });
            host.Start();
            share.Attach(host);
            rig = new Rig { Host = host, Share = share, Input = input, Screen = screen, HostIdentity = identity };
            await Task.Delay(50);
            return rig;
        }

        public string Address => "127.0.0.1:" + Host.TcpPort;

        public async Task<(IMessageChannel Channel, HostAnnouncement Host, bool Remembered)> ConnectWithTokenAsync(DeviceIdentity self, string name = "viewer-pc")
        {
            var result = await LanConnector.ConnectAsync(Host.CurrentToken.Display, ControllerHello.Encode(name), Address, Cts.Token);
            try
            {
                var (host, remembered) = await DeviceProof.RespondAsync(result.Channel, self, ControllerHello.Encode(name), Cts.Token);
                return (result.Channel, host, remembered);
            }
            catch { result.Channel.Dispose(); throw; }
        }

        public Task<IMessageChannel> ResumeAsync(DeviceIdentity self, string expectedHostId) =>
            LanResume.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Host.TcpPort), self, expectedHostId, Cts.Token);

        public async Task WaitAsync(Func<bool> condition, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException(what); await Task.Delay(20); }
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Share.Detach();
            HostIdentity.Dispose();
            Cts.Dispose();
        }
    }

    /// <summary>Waits until the host has served a screen request for the session just opened (the viewer's frame event can fire before the test has subscribed, so the host's capture counter is the stable evidence).</summary>
    private static async Task FirstFrameAsync(Rig rig, ViewerSession session)
    {
        var before = rig.Screen.Captures;
        await rig.WaitAsync(() => rig.Screen.Captures > before, "the host captured a frame for this session");
        Assert.NotNull(session.Hello);
    }

    [Fact]
    public async Task Denied_means_the_other_pc_is_told_no_and_no_frame_is_ever_captured()
    {
        await using var rig = await Rig.StartAsync();
        using var anna = DeviceIdentity.Generate();
        var ex = await Assert.ThrowsAsync<ConsentDeniedException>(() => rig.ConnectWithTokenAsync(anna));
        Assert.Contains("did not allow", ex.Message);
        await rig.WaitAsync(() => rig.Share.State.Current.State == ShareState.Ready && rig.Share.History.All.Count == 1, "refusal recorded");
        Assert.Equal(0, rig.Screen.Captures);
        Assert.Empty(rig.Share.Remembered.All);
        var row = rig.Share.History.All.Single();
        Assert.Equal("Denied", row.Outcome);
        Assert.Equal(anna.DeviceId, row.DeviceId);
        await rig.WaitAsync(() => !rig.Host.IsConnected, "host free again");
    }

    [Fact]
    public async Task Nobody_answering_the_question_is_a_refusal()
    {
        await using var rig = await Rig.StartAsync(promptTimeout: TimeSpan.FromMilliseconds(400));
        rig.Prompt = (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => ConsentDecision.AllowOnce, TaskScheduler.Default);
        using var anna = DeviceIdentity.Generate();
        await Assert.ThrowsAsync<ConsentDeniedException>(() => rig.ConnectWithTokenAsync(anna));
        await rig.WaitAsync(() => rig.Share.History.All.Count == 1, "timeout recorded");
        Assert.Equal("TimedOut", rig.Share.History.All.Single().Outcome);
        Assert.Equal(0, rig.Screen.Captures);
    }

    [Fact]
    public async Task Allow_once_opens_a_session_with_the_state_machine_and_history_following_it()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AllowOnce);
        using var anna = DeviceIdentity.Generate();
        var (channel, host, remembered) = await rig.ConnectWithTokenAsync(anna, "Anna's laptop");
        Assert.False(remembered);
        Assert.Equal(rig.HostIdentity.DeviceId, host.DeviceId);
        await using (var session = await ViewerSession.OpenAsync(channel, "host", rig.Cts.Token))
        {
            await FirstFrameAsync(rig, session);
            await rig.WaitAsync(() => rig.Share.State.Current.State == ShareState.InSession, "in session");
            Assert.Equal("Anna's laptop", rig.Share.State.Current.Controller);
            Assert.Equal("Anna's laptop", rig.Asked.Single().ControllerName);
            Assert.True(rig.Asked.Single().CanRemember);
        }
        await rig.WaitAsync(() => rig.Share.State.Current.State == ShareState.Ready, "ready again");
        var row = rig.Share.History.All.Single();
        Assert.Equal(SessionEndReasons.ControllerLeft, row.EndReason);
        Assert.Equal("token", row.Via);
        Assert.Empty(rig.Share.Remembered.All);
    }

    [Fact]
    public async Task Always_allow_lets_the_same_key_reconnect_without_a_token_and_nobody_else()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AlwaysAllow);
        using var anna = DeviceIdentity.Generate();
        var (channel, host, remembered) = await rig.ConnectWithTokenAsync(anna, "Anna");
        Assert.True(remembered);
        await using (var first = await ViewerSession.OpenAsync(channel, "host", rig.Cts.Token)) await FirstFrameAsync(rig, first);
        await rig.WaitAsync(() => !rig.Host.IsConnected, "first session over");
        Assert.True(rig.Share.Remembered.Contains(anna.DeviceId));

        // Same device key, no token, no question.
        var asked = rig.Asked.Count;
        await using (var second = await ViewerSession.OpenAsync(await rig.ResumeAsync(anna, host.DeviceId), "host", rig.Cts.Token))
        {
            await FirstFrameAsync(rig, second);
            Assert.Equal(asked, rig.Asked.Count);
            Assert.Equal("remembered", rig.Share.History.All[0].Via);
            Assert.Equal("Anna", rig.Share.State.Current.Controller);
        }
        await rig.WaitAsync(() => !rig.Host.IsConnected, "second session over");

        // A different key (even with the same name) is turned away.
        using var mallory = DeviceIdentity.Generate();
        await Assert.ThrowsAsync<LanConnectException>(() => rig.ResumeAsync(mallory, host.DeviceId));
        // The right key, but the caller expects another host: refused by the caller.
        using var elsewhere = DeviceIdentity.Generate();
        await Assert.ThrowsAsync<LanConnectException>(() => rig.ResumeAsync(anna, elsewhere.DeviceId));
        Assert.False(rig.Host.IsConnected);
    }

    [Fact]
    public async Task Revoking_a_remembered_pc_ends_token_free_access_at_once()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AlwaysAllow);
        using var anna = DeviceIdentity.Generate();
        var (channel, host, _) = await rig.ConnectWithTokenAsync(anna);
        await using (var first = await ViewerSession.OpenAsync(channel, "host", rig.Cts.Token)) await FirstFrameAsync(rig, first);
        await rig.WaitAsync(() => !rig.Host.IsConnected, "first session over");
        Assert.True(rig.Share.Remembered.Remove(anna.DeviceId));
        await Assert.ThrowsAsync<LanConnectException>(() => rig.ResumeAsync(anna, host.DeviceId));
    }

    [Fact]
    public async Task Turning_remembered_pcs_off_blocks_token_free_reconnects_without_forgetting_them()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AlwaysAllow);
        using var anna = DeviceIdentity.Generate();
        var (channel, host, _) = await rig.ConnectWithTokenAsync(anna);
        await using (var first = await ViewerSession.OpenAsync(channel, "host", rig.Cts.Token)) await FirstFrameAsync(rig, first);
        await rig.WaitAsync(() => !rig.Host.IsConnected, "first session over");
        rig.Share.SetAllowRemembered(false);
        await Assert.ThrowsAsync<LanConnectException>(() => rig.ResumeAsync(anna, host.DeviceId));
        rig.Share.SetAllowRemembered(true);
        await using var again = await ViewerSession.OpenAsync(await rig.ResumeAsync(anna, host.DeviceId), "host", rig.Cts.Token);
        await FirstFrameAsync(rig, again);
    }

    [Fact]
    public async Task A_remembered_pc_that_also_types_a_token_is_not_asked_again()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AlwaysAllow);
        using var anna = DeviceIdentity.Generate();
        var (c1, _, _) = await rig.ConnectWithTokenAsync(anna);
        await using (var first = await ViewerSession.OpenAsync(c1, "host", rig.Cts.Token)) await FirstFrameAsync(rig, first);
        await rig.WaitAsync(() => !rig.Host.IsConnected, "first session over");
        rig.Prompt = (_, _) => throw new InvalidOperationException("must not be asked");
        var (c2, _, _) = await rig.ConnectWithTokenAsync(anna);
        await using var second = await ViewerSession.OpenAsync(c2, "host", rig.Cts.Token);
        await FirstFrameAsync(rig, second);
    }

    [Fact]
    public async Task An_older_controller_without_a_device_proof_can_be_allowed_once_but_never_remembered()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AlwaysAllow);
        var result = await LanConnector.ConnectAsync(rig.Host.CurrentToken.Display, "Old viewer", rig.Address, rig.Cts.Token);
        await using var session = await ViewerSession.OpenAsync(result.Channel, "host", rig.Cts.Token);
        await FirstFrameAsync(rig, session);
        Assert.False(rig.Asked.Single().CanRemember);
        Assert.Equal("Old viewer", rig.Asked.Single().ControllerName);
        Assert.Empty(rig.Share.Remembered.All);
    }

    [Fact]
    public async Task An_older_controller_is_refused_when_the_person_says_no()
    {
        await using var rig = await Rig.StartAsync();
        var result = await LanConnector.ConnectAsync(rig.Host.CurrentToken.Display, "Old viewer", rig.Address, rig.Cts.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => ViewerSession.OpenAsync(result.Channel, "host", rig.Cts.Token));
        Assert.Equal(0, rig.Screen.Captures);
    }

    [Fact]
    public async Task With_asking_switched_off_a_token_holder_gets_in_without_a_question()
    {
        await using var rig = await Rig.StartAsync(ask: false);
        using var anna = DeviceIdentity.Generate();
        var (channel, _, remembered) = await rig.ConnectWithTokenAsync(anna);
        Assert.False(remembered);
        await using var session = await ViewerSession.OpenAsync(channel, "host", rig.Cts.Token);
        await FirstFrameAsync(rig, session);
        Assert.Empty(rig.Asked);
        Assert.Equal("AllowedWithoutAsking", rig.Share.History.All.Single().Outcome);
    }

    [Fact]
    public async Task Pause_control_makes_the_session_view_only_until_resumed()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AllowOnce);
        using var anna = DeviceIdentity.Generate();
        var (channel, _, _) = await rig.ConnectWithTokenAsync(anna);
        await using var session = await ViewerSession.OpenAsync(channel, "host", rig.Cts.Token);
        await FirstFrameAsync(rig, session);
        var notice = new TaskCompletionSource<string>();
        session.HostError += e => notice.TrySetResult(e);

        await rig.Share.SetControlPausedAsync(true, "Control is paused.");
        Assert.True(rig.Share.State.Current.ControlPaused);
        Assert.Equal("Control is paused.", await notice.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        session.SendInput(new InputEvent(InputKind.KeyDown, 0, 0, 65));
        await Task.Delay(400);
        Assert.Empty(rig.Input.Snapshot());
        Assert.True(rig.Host.ControlPaused);

        await rig.Share.SetControlPausedAsync(false, "");
        session.SendInput(new InputEvent(InputKind.KeyDown, 0, 0, 66));
        await rig.WaitAsync(() => rig.Input.Snapshot().Any(e => e.Kind == InputKind.KeyDown), "input after resume");
        Assert.DoesNotContain(rig.Input.Snapshot(), e => e.Arg1 == 65);
    }

    [Fact]
    public async Task Stop_ends_the_session_for_the_other_pc_and_records_why()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AllowOnce);
        using var anna = DeviceIdentity.Generate();
        var (channel, _, _) = await rig.ConnectWithTokenAsync(anna);
        await using var session = await ViewerSession.OpenAsync(channel, "host", rig.Cts.Token);
        await FirstFrameAsync(rig, session);
        var ended = new TaskCompletionSource();
        session.Closed += _ => ended.TrySetResult();
        rig.Share.EndSession();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.WaitAsync(() => rig.Share.State.Current.State == ShareState.Ready, "ready again");
        Assert.Equal(SessionEndReasons.StoppedByHost, rig.Share.History.All.Single().EndReason);
    }

    [Fact]
    public async Task Turning_sharing_off_during_a_session_closes_the_history_row()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AllowOnce);
        using var anna = DeviceIdentity.Generate();
        var (channel, _, _) = await rig.ConnectWithTokenAsync(anna);
        await using var session = await ViewerSession.OpenAsync(channel, "host", rig.Cts.Token);
        await FirstFrameAsync(rig, session);
        rig.Share.Detach();
        Assert.Equal(ShareState.Off, rig.Share.State.Current.State);
        Assert.Equal(SessionEndReasons.SharingStopped, rig.Share.History.All.Single().EndReason);
    }

    [Fact]
    public async Task A_stranger_sending_junk_never_reaches_Connecting_and_a_wrong_token_returns_to_Ready()
    {
        await using var rig = await Rig.StartAsync();
        var states = new List<ShareState>();
        rig.Share.State.Changed += s => states.Add(s.State);
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, rig.Host.TcpPort);
            var wire = new FramedStream(raw.GetStream());
            await wire.SendAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1"), rig.Cts.Token);
            Assert.Null(await wire.ReceiveAsync(rig.Cts.Token));
        }
        Assert.DoesNotContain(ShareState.Connecting, states);
        var real = rig.Host.CurrentToken;
        var wrong = new LanToken(real.Id, real.Secret.Select(c => c == 'A' ? 'B' : 'A').Aggregate("", (a, c) => a + c)).Display;
        await Assert.ThrowsAsync<LanConnectException>(() => LanConnector.ConnectAsync(wrong, "x", rig.Address, rig.Cts.Token));
        await rig.WaitAsync(() => states.Contains(ShareState.Connecting) && rig.Share.State.Current.State == ShareState.Ready, "back to ready");
        Assert.Equal(0, rig.Screen.Captures);
    }

    [Fact]
    public async Task A_recorded_resume_hello_replayed_by_someone_else_never_starts_a_session()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AlwaysAllow);
        using var anna = DeviceIdentity.Generate();
        var (channel, host, _) = await rig.ConnectWithTokenAsync(anna);
        await using (var first = await ViewerSession.OpenAsync(channel, "host", rig.Cts.Token)) await FirstFrameAsync(rig, first);
        await rig.WaitAsync(() => !rig.Host.IsConnected, "first session over");

        // What an eavesdropper would have recorded: a valid, signed Hello1. Replay it on a new connection and say nothing more.
        using var handshake = new InitiatorHandshake(anna, host.DeviceId);
        var hello1 = handshake.CreateHello1();
        var started = 0;
        rig.Host.SessionStarted += _ => Interlocked.Increment(ref started);
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, rig.Host.TcpPort);
        var wire = new FramedStream(raw.GetStream());
        await wire.SendAsync(hello1, rig.Cts.Token);
        Assert.NotNull(await wire.ReceiveAsync(rig.Cts.Token));   // the host answered with its Hello2 ...
        await Task.Delay(1500);
        Assert.Equal(0, started);                                  // ... but nobody who cannot decrypt it gets a session
        Assert.False(rig.Host.IsConnected);
        Assert.NotEqual(ShareState.InSession, rig.Share.State.Current.State);
    }

    [Fact]
    public async Task A_controller_that_claims_a_proof_but_signs_for_another_name_is_dropped()
    {
        await using var rig = await Rig.StartAsync();
        rig.Prompt = (_, _) => Task.FromResult(ConsentDecision.AllowOnce);
        using var anna = DeviceIdentity.Generate();
        var result = await LanConnector.ConnectAsync(rig.Host.CurrentToken.Display, ControllerHello.Encode("Real name"), rig.Address, rig.Cts.Token);
        // The proof is signed for a different name than the one announced: the host must not accept it.
        await Assert.ThrowsAnyAsync<Exception>(() => DeviceProof.RespondAsync(result.Channel, anna, ControllerHello.Encode("Other name"), rig.Cts.Token));
        Assert.Empty(rig.Asked);
        Assert.Equal(0, rig.Screen.Captures);
    }
}

public class ShareSelfTestOptionTests
{
    [Fact]
    public void The_share_self_test_flag_takes_a_file_and_is_a_self_test_that_is_not_the_token_self_test()
    {
        var o = Remotely.App.Shell.AppOptions.Parse(["--selftest-share", "share.txt"]);
        Assert.Equal("share.txt", o.SelfTestShareResultPath);
        Assert.Null(o.SelfTestResultPath);
        Assert.True(o.IsSelfTest);
        Assert.False(o.UsesSingleInstance);
        Assert.NotEmpty(Remotely.App.Shell.AppOptions.Parse(["--selftest-share"]).Problems);
    }
}
