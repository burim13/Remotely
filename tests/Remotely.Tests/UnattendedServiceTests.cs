using System.Net;
using System.Net.Sockets;
using System.Text;
using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.Core.Lan.Consent;
using Remotely.Host.Unattended;
using Remotely.HostCore;
using Remotely.Unattended;
using Remotely.ViewerCore;

namespace Remotely.Tests;

/// <summary>The logic of the host service for unattended access (agent A13): the question relay, the keep-awake request, the settings file, and sharing by token through the service, end to end over real sockets.</summary>
public class UnattendedServiceTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class FakePower : IPowerRequest
    {
        public int Acquired, Released;
        public bool Succeeds = true;
        public bool Acquire() { Acquired++; return Succeeds; }
        public void Release() => Released++;
    }

    // ---- the question relay ----

    private static ConsentRequest Request(string name = "Dad's laptop") => new(name, "192.168.1.20", null, false, DateTimeOffset.UtcNow);

    [Fact]
    public async Task With_no_app_watching_a_question_is_refused_at_once_instead_of_waiting_for_nobody()
    {
        var relay = new ConsentRelay();
        Assert.Equal(ConsentDecision.Deny, await relay.AskAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(relay.Current);
    }

    [Fact]
    public async Task A_question_is_shown_to_the_app_and_only_the_exact_answer_with_the_right_id_lets_anyone_in()
    {
        var clock = new Clock();
        var relay = new ConsentRelay(clock.Read);
        relay.NotePoll();
        var asking = relay.AskAsync(Request(), CancellationToken.None);
        var question = relay.Current!;
        Assert.Equal("Dad's laptop", question.ControllerName);
        Assert.False(string.IsNullOrEmpty(question.Id));

        Assert.False(relay.Answer("not-the-id", PipeDecisions.AllowOnce));
        Assert.False(relay.Answer(null, PipeDecisions.AllowOnce));
        Assert.False(asking.IsCompleted);
        Assert.True(relay.Answer(question.Id, PipeDecisions.AllowOnce));
        Assert.Equal(ConsentDecision.AllowOnce, await asking);
        Assert.Null(relay.Current);
        Assert.False(relay.Answer(question.Id, PipeDecisions.AllowOnce));   // answered once
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("always")]
    [InlineData("ALLOW")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Anything_but_an_explicit_once_is_a_refusal(string? decision)
    {
        var relay = new ConsentRelay();
        relay.NotePoll();
        var asking = relay.AskAsync(Request(), CancellationToken.None);
        Assert.True(relay.Answer(relay.Current!.Id, decision));
        Assert.Equal(ConsentDecision.Deny, await asking);
    }

    [Fact]
    public async Task A_second_question_while_one_is_open_is_refused_and_a_timeout_clears_the_first()
    {
        var relay = new ConsentRelay();
        relay.NotePoll();
        using var cts = new CancellationTokenSource();
        var first = relay.AskAsync(Request("first"), cts.Token);
        Assert.Equal(ConsentDecision.Deny, await relay.AskAsync(Request("second"), CancellationToken.None));
        Assert.Equal("first", relay.Current!.ControllerName);

        cts.Cancel();   // what the consent gate does when nobody answers in time
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Null(relay.Current);
    }

    [Fact]
    public async Task The_app_counts_as_watching_for_six_seconds_after_it_last_asked()
    {
        var clock = new Clock();
        var relay = new ConsentRelay(clock.Read);
        Assert.False(relay.AppIsWatching);
        relay.NotePoll();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(relay.AppIsWatching);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(relay.AppIsWatching);
        Assert.Equal(ConsentDecision.Deny, await relay.AskAsync(Request(), CancellationToken.None));
    }

    // ---- staying awake ----

    [Fact]
    public void Windows_is_kept_awake_from_the_first_frame_until_thirty_seconds_after_the_last()
    {
        var clock = new Clock();
        var power = new FakePower();
        var activity = new SessionActivity(power, () => true, clock.Read);
        activity.Tick();
        Assert.Equal(0, power.Acquired);

        activity.Touch("Default");
        activity.Touch("Default");
        Assert.Equal(1, power.Acquired);   // one request, however many frames
        Assert.True(activity.AwakeHeld);

        clock.Advance(TimeSpan.FromSeconds(29));
        activity.Tick();
        Assert.True(activity.AwakeHeld);
        clock.Advance(TimeSpan.FromSeconds(2));
        activity.Tick();
        Assert.False(activity.AwakeHeld);
        Assert.Equal(1, power.Released);

        activity.Touch("Default");   // somebody looks again
        Assert.Equal(2, power.Acquired);
    }

    [Fact]
    public void Nothing_is_requested_while_the_switch_is_off_and_switching_it_off_lets_go_at_the_next_tick()
    {
        var clock = new Clock();
        var power = new FakePower();
        var on = false;
        var activity = new SessionActivity(power, () => on, clock.Read);
        activity.Touch("Default");
        Assert.Equal(0, power.Acquired);

        on = true;
        activity.Touch("Default");
        Assert.True(activity.AwakeHeld);
        on = false;
        activity.Tick();
        Assert.False(activity.AwakeHeld);
        Assert.Equal(1, power.Released);
    }

    [Fact]
    public void A_refused_request_is_tried_again_with_the_next_frame_and_shutdown_lets_go()
    {
        var power = new FakePower { Succeeds = false };
        var activity = new SessionActivity(power, () => true);
        activity.Touch("Default");
        Assert.False(activity.AwakeHeld);
        power.Succeeds = true;
        activity.Touch("Default");
        Assert.True(activity.AwakeHeld);
        Assert.Equal(2, power.Acquired);
        activity.Shutdown();
        Assert.False(activity.AwakeHeld);
        Assert.Equal(1, power.Released);
        activity.Shutdown();
        Assert.Equal(1, power.Released);
    }

    [Fact]
    public void The_desktop_being_looked_at_is_known_for_fifteen_seconds()
    {
        var clock = new Clock();
        var activity = new SessionActivity(new FakePower(), () => true, clock.Read);
        Assert.Null(activity.CaptureDesktop);
        activity.Touch("Winlogon");
        Assert.Equal("Winlogon", activity.CaptureDesktop);
        activity.Touch(null);   // a request that carried no picture does not forget it
        Assert.Equal("Winlogon", activity.CaptureDesktop);
        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Null(activity.CaptureDesktop);
    }

    // ---- the settings file ----

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rmt-unattended-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Missing_empty_or_damaged_settings_mean_the_safe_defaults()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "unattended.json");
            var store = new UnattendedSettingsStore(path);
            void AssertSafe()
            {
                var s = store.Load();
                Assert.False(s.LanAllowed);
                Assert.True(s.AskBeforeConnect);
                Assert.True(s.KeepAwake);
            }
            AssertSafe();
            File.WriteAllText(path, "");
            AssertSafe();
            File.WriteAllText(path, "{ this is not json");
            AssertSafe();
            File.WriteAllText(path, "null");
            AssertSafe();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Settings_survive_a_save_and_unknown_fields_are_ignored()
    {
        var dir = TempDir();
        try
        {
            var store = new UnattendedSettingsStore(Path.Combine(dir, "sub", "unattended.json"));
            store.Update(s => { s.LanAllowed = true; s.AskBeforeConnect = false; });
            var back = store.Load();
            Assert.True(back.LanAllowed);
            Assert.False(back.AskBeforeConnect);
            Assert.True(back.KeepAwake);
            Assert.False(File.Exists(store.FilePath + ".tmp"));
            File.WriteAllText(store.FilePath, """{"LanAllowed":true,"FromTheFuture":[1,2]}""");
            Assert.True(store.Load().LanAllowed);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_cached_settings_read_the_file_again_after_two_seconds_and_not_before()
    {
        var dir = TempDir();
        try
        {
            var clock = new Clock();
            var store = new UnattendedSettingsStore(Path.Combine(dir, "unattended.json"));
            var cached = new CachedSettings(store, clock.Read);
            Assert.False(cached.Current.LanAllowed);
            store.Update(s => s.LanAllowed = true);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(cached.Current.LanAllowed);
            clock.Advance(TimeSpan.FromSeconds(1.5));
            Assert.True(cached.Current.LanAllowed);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- sharing by token through the service, end to end ----

    private sealed class TiledFake(string desktop) : ITiledFrameSource
    {
        public int Captures;
        public Task<FrameInfo> CaptureAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Captures);
            return Task.FromResult(new FrameInfo(800, 600, desktop, Encoding.ASCII.GetBytes("jpeg")));
        }

        public Task<TileCapture> CaptureTilesAsync(TileRequest request, CancellationToken ct) => Task.FromResult(new TileCapture([], null, null, null));
    }

    private sealed class LanRig : IAsyncDisposable
    {
        public readonly string Dir = TempDir();
        public readonly Clock Clock = new();
        public readonly UnattendedSettingsStore Store;
        public readonly CachedSettings Settings;
        public readonly FakePower Power = new();
        public readonly ConsentRelay Relay;
        public readonly SessionActivity Activity;
        public readonly TiledFake Screen;
        public readonly HostCoreTests.RecordingInput Input = new();
        public readonly List<string> Log = [];
        public readonly ServiceLan Lan;
        public readonly CancellationTokenSource Cts = new(TimeSpan.FromSeconds(40));

        public LanRig(bool allowed = true, bool ask = false, string desktop = "Winlogon")
        {
            Relay = new ConsentRelay(Clock.Read);
            Store = new UnattendedSettingsStore(Path.Combine(Dir, "unattended.json"));
            Store.Save(new UnattendedSettings { LanAllowed = allowed, AskBeforeConnect = ask });
            Settings = new CachedSettings(Store, Clock.Read);
            Activity = new SessionActivity(Power, () => Settings.Current.KeepAwake);
            Screen = new TiledFake(desktop);
            using var probe = new UdpClient(0);
            var udp = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            Lan = new ServiceLan(new ServiceLanOptions
            {
                Services = () => new HostServices(new ActivityFrameSource(Screen, Activity)) { Input = Input, IsTrusted = _ => true, CanControl = _ => true },
                Identity = DeviceIdentity.Generate(),
                Settings = Settings,
                Relay = Relay,
                Activity = Activity,
                HelperAttached = () => true,
                Log = line => { lock (Log) Log.Add(line); },
                TcpPort = 0,
                DiscoveryPort = udp,
                AllowLoopback = true,
            });
        }

        public async Task<PipeReply> CallAsync(string cmd, string? id = null, string? decision = null) =>
            await Lan.HandleAsync(new PipeRequest { Cmd = cmd, QuestionId = id, Decision = decision }, Cts.Token);

        public async Task<LanConnector.Result> ConnectAsync(string token, int port) =>
            await LanConnector.ConnectAsync(token, "viewer-pc", "127.0.0.1:" + port, Cts.Token);

        public async ValueTask DisposeAsync()
        {
            await Lan.DisposeAsync();
            Cts.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Until_an_administrator_allows_it_the_service_does_not_listen_and_shows_no_token()
    {
        await using var rig = new LanRig(allowed: false);
        var reply = await rig.CallAsync(PipeCommands.Start);
        Assert.False(reply.Ok);
        Assert.Equal(PipeErrors.NotAllowed, reply.Error);
        Assert.False(reply.Status!.Listening);
        Assert.Null(reply.Status.Token);
        Assert.False(reply.Status.LanAllowed);
        Assert.False(rig.Lan.IsListening);
    }

    [Fact]
    public async Task A_PC_with_the_token_reaches_the_screen_the_service_captures_including_the_lock_screen_and_the_service_says_so()
    {
        await using var rig = new LanRig(ask: false, desktop: "Winlogon");
        var started = await rig.CallAsync(PipeCommands.Start);
        Assert.True(started.Ok, started.Error);
        var status = started.Status!;
        Assert.True(status.Listening);
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", status.Token!);
        Assert.True(status.Port > 0);

        var result = await rig.ConnectAsync(status.Token!, status.Port);
        await using var session = await ViewerSession.OpenAsync(result.Channel, "host", rig.Cts.Token);
        var frame = session.LatestFrame ?? await Task.Run(async () =>
        {
            var got = new TaskCompletionSource<FrameInfo>();
            session.FrameReceived += f => got.TrySetResult(f);
            return session.LatestFrame ?? await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        Assert.Equal("Winlogon", frame.Desktop);
        Assert.True(session.CanControl);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        UnattendedStatus now;
        do { now = (await rig.CallAsync(PipeCommands.Status)).Status!; if (now.Controller is not null) break; await Task.Delay(25); } while (DateTime.UtcNow < deadline);
        Assert.Equal("viewer-pc", now.Controller);
        Assert.Equal("Winlogon", now.CaptureDesktop);
        Assert.True(now.AwakeHeld);
        Assert.Equal(1, rig.Power.Acquired);
        Assert.NotNull(now.SessionSince);
        Assert.NotEqual(status.Token, now.Token);   // single use: a new token is already on offer
        await Assert.ThrowsAsync<LanConnectException>(() => rig.ConnectAsync(status.Token!, status.Port));
    }

    [Fact]
    public async Task Ending_the_session_disconnects_the_other_PC_and_stopping_closes_the_door()
    {
        await using var rig = new LanRig(ask: false);
        var status = (await rig.CallAsync(PipeCommands.Start)).Status!;
        var result = await rig.ConnectAsync(status.Token!, status.Port);
        await using var session = await ViewerSession.OpenAsync(result.Channel, "host", rig.Cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await rig.CallAsync(PipeCommands.Status)).Status!.Controller is null && DateTime.UtcNow < deadline) await Task.Delay(25);

        Assert.True((await rig.CallAsync(PipeCommands.EndSession)).Ok);
        deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await rig.CallAsync(PipeCommands.Status)).Status!.Controller is not null && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.Null((await rig.CallAsync(PipeCommands.Status)).Status!.Controller);

        var token = (await rig.CallAsync(PipeCommands.Status)).Status!;
        var stopped = await rig.CallAsync(PipeCommands.Stop);
        Assert.True(stopped.Ok);
        Assert.False(stopped.Status!.Listening);
        Assert.Null(stopped.Status.Token);
        await Assert.ThrowsAsync<LanConnectException>(() => rig.ConnectAsync(token.Token!, token.Port));
    }

    [Fact]
    public async Task With_the_question_on_the_app_is_asked_and_a_yes_lets_the_PC_in()
    {
        await using var rig = new LanRig(ask: true);
        var status = (await rig.CallAsync(PipeCommands.Start)).Status!;
        var connect = rig.ConnectAsync(status.Token!, status.Port).ContinueWith(async t =>
        {
            var r = await t;
            return await ViewerSession.OpenAsync(r.Channel, "host", rig.Cts.Token);
        }, TaskScheduler.Default).Unwrap();

        ConsentQuestion? question = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (question is null && DateTime.UtcNow < deadline) { question = (await rig.CallAsync(PipeCommands.Status)).Status!.Question; if (question is null) await Task.Delay(25); }
        Assert.NotNull(question);
        Assert.Equal("viewer-pc", question!.ControllerName);
        Assert.False((await rig.CallAsync(PipeCommands.Answer, "wrong-id", PipeDecisions.AllowOnce)).Ok);
        Assert.True((await rig.CallAsync(PipeCommands.Answer, question.Id, PipeDecisions.AllowOnce)).Ok);

        await using var session = await connect.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(session.CanControl);
    }

    [Fact]
    public async Task With_the_question_on_a_no_means_no_frame_is_ever_captured()
    {
        await using var rig = new LanRig(ask: true);
        var status = (await rig.CallAsync(PipeCommands.Start)).Status!;
        var connect = Task.Run(async () =>
        {
            var r = await rig.ConnectAsync(status.Token!, status.Port);
            await using var s = await ViewerSession.OpenAsync(r.Channel, "host", rig.Cts.Token);
            await Task.Delay(500);
        });

        ConsentQuestion? question = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (question is null && DateTime.UtcNow < deadline) { question = (await rig.CallAsync(PipeCommands.Status)).Status!.Question; if (question is null) await Task.Delay(25); }
        Assert.NotNull(question);
        Assert.True((await rig.CallAsync(PipeCommands.Answer, question!.Id, PipeDecisions.Deny)).Ok);

        await Assert.ThrowsAnyAsync<Exception>(() => connect.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(0, rig.Screen.Captures);
        Assert.Null((await rig.CallAsync(PipeCommands.Status)).Status!.Controller);
    }

    [Fact]
    public async Task With_the_question_on_and_no_app_watching_the_PC_is_turned_away_without_waiting()
    {
        await using var rig = new LanRig(ask: true);
        var status = (await rig.CallAsync(PipeCommands.Start)).Status!;
        rig.Clock.Advance(TimeSpan.FromSeconds(7));   // the app last looked seven seconds ago: nobody can answer
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var attempt = Task.Run(async () =>
        {
            var r = await rig.ConnectAsync(status.Token!, status.Port);
            await using var s = await ViewerSession.OpenAsync(r.Channel, "host", rig.Cts.Token);
            await Task.Delay(500);
        });
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => attempt.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsNotType<TimeoutException>(ex);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "the refusal should not wait for the 25 second question timeout");
        Assert.Equal(0, rig.Screen.Captures);
    }

    [Fact]
    public async Task When_the_administrator_switches_sharing_off_the_service_stops_listening_by_itself()
    {
        await using var rig = new LanRig(ask: false);
        Assert.True((await rig.CallAsync(PipeCommands.Start)).Ok);
        Assert.True(rig.Lan.IsListening);
        rig.Store.Update(s => s.LanAllowed = false);
        rig.Clock.Advance(TimeSpan.FromSeconds(3));   // the settings are re-read every two seconds
        await rig.Lan.EnforceSettingsAsync();
        Assert.False(rig.Lan.IsListening);
        Assert.Contains(rig.Log, l => l.Contains("switched off"));
    }

    [Fact]
    public async Task Commands_that_make_no_sense_get_a_plain_error_and_change_nothing()
    {
        await using var rig = new LanRig();
        var newToken = await rig.CallAsync(PipeCommands.NewToken);
        Assert.False(newToken.Ok);
        Assert.Equal(PipeErrors.NotListening, newToken.Error);
        Assert.Equal(PipeErrors.BadRequest, (await rig.CallAsync("format-c")).Error);
        Assert.Equal(PipeErrors.BadRequest, (await rig.CallAsync("")).Error);
        Assert.Equal(PipeErrors.NoSuchQuestion, (await rig.CallAsync(PipeCommands.Answer, "x", PipeDecisions.AllowOnce)).Error);
        Assert.True((await rig.CallAsync(PipeCommands.Stop)).Ok);   // stopping what is not running is fine
        Assert.False(rig.Lan.IsListening);

        Assert.True((await rig.CallAsync(PipeCommands.Start)).Ok);
        var first = (await rig.CallAsync(PipeCommands.Status)).Status!.Token;
        Assert.True((await rig.CallAsync(PipeCommands.Start)).Ok);   // starting twice keeps the same listener
        Assert.Equal(first, (await rig.CallAsync(PipeCommands.Status)).Status!.Token);
        Assert.True((await rig.CallAsync(PipeCommands.NewToken)).Ok);
        Assert.NotEqual(first, (await rig.CallAsync(PipeCommands.Status)).Status!.Token);
    }

    [Fact]
    public async Task The_service_never_offers_remembered_PCs_so_a_token_is_needed_every_time()
    {
        await using var rig = new LanRig(ask: false);
        var status = (await rig.CallAsync(PipeCommands.Start)).Status!;
        // a resume hello (magic RMT1) from anyone is ignored: the listener is started with AllowResume = false
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, status.Port);
        var stream = raw.GetStream();
        var hello = new byte[4 + 40];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hello, 40);
        "RMT1"u8.CopyTo(hello.AsSpan(4));
        await stream.WriteAsync(hello, rig.Cts.Token);
        using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var read = 0;
        try { read = await stream.ReadAsync(new byte[16], brief.Token); } catch (Exception e) when (e is OperationCanceledException or IOException) { }
        Assert.Equal(0, read);   // closed, nothing said
        Assert.Null((await rig.CallAsync(PipeCommands.Status)).Status!.Controller);
    }
}
