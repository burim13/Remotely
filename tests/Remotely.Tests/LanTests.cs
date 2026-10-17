using System.Net;
using System.Net.Sockets;
using System.Text;
using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.Tests;

public class LanTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public required LanHost Host;
        public required HostCoreTests.RecordingInput Input;
        public int DiscoveryPort;
        public CancellationTokenSource Cts = new(TimeSpan.FromSeconds(30));
        public List<string> Log = [];

        public static async Task<Rig> StartAsync(TimeSpan? tokenLifetime = null, int maxFailures = 3)
        {
            var input = new HostCoreTests.RecordingInput();
            var screen = new HostCoreTests.FakeScreen(() => new FrameInfo(800, 600, "Default", Encoding.ASCII.GetBytes("jpeg")));
            var services = new HostServices(screen) { Input = input, IsTrusted = _ => true, CanControl = _ => true };
            var agent = new HostAgent(DeviceIdentity.Generate(), services, _ => { });
            var probe = new UdpClient(0);
            var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            probe.Dispose();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var log = new List<string>();
            var host = new LanHost(new LanHostOptions { TcpPort = 0, DiscoveryPort = port, AllowLoopback = true, MaxFailures = maxFailures, TokenLifetime = tokenLifetime ?? TimeSpan.FromMinutes(10), HostName = "test-pc" },
                (channel, name, ct) => agent.ServeChannelAsync(channel, name, control: true, ct), log.Add);
            host.Start();
            await Task.Delay(50);
            return new Rig { Host = host, Input = input, DiscoveryPort = port, Cts = cts, Log = log };
        }

        public string Address => "127.0.0.1:" + Host.TcpPort;
        public Task<LanConnector.Result> ConnectAsync(string token, string? address = null) =>
            LanConnector.ConnectAsync(token, "viewer-pc", address ?? Address, Cts.Token);

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Cts.Dispose();
        }
    }

    [Fact]
    public void A_token_is_twelve_characters_and_reads_back_however_it_is_typed()
    {
        var token = LanToken.Create();
        Assert.Equal(12, token.Id.Length + token.Secret.Length);
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", token.Display);
        Assert.True(LanToken.TryParse(token.Display.ToLowerInvariant().Replace("-", " "), out var back, out var error), error);
        Assert.Equal(token, back);
        Assert.False(LanToken.TryParse("K7P4-4NX2", out _, out var tooShort)); Assert.Contains("12", tooShort);
        Assert.False(LanToken.TryParse("K7P4-4NX2-QMH0", out _, out var badChar)); Assert.Contains("0, 1, I or O", badChar);
        Assert.False(LanToken.TryParse("", out _, out _));
    }

    [Fact]
    public async Task Typing_the_token_connects_and_the_screen_and_input_work()
    {
        await using var rig = await Rig.StartAsync();
        var token = rig.Host.CurrentToken.Display;
        var result = await rig.ConnectAsync(token);
        await using var session = await ViewerSession.OpenAsync(result.Channel, "host", rig.Cts.Token);
        var frame = new TaskCompletionSource<FrameInfo>();
        session.FrameReceived += f => frame.TrySetResult(f);
        // The first frame can arrive before the handler above is attached; LatestFrame holds the picture such a late subscriber missed.
        var first = session.LatestFrame ?? await frame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Default", first.Desktop);
        Assert.True(session.CanControl);
        session.SendInput(new InputEvent(InputKind.KeyDown, 0, 0, 65));
        var sw = DateTime.UtcNow;
        while (rig.Input.Snapshot().All(e => e.Kind != InputKind.KeyDown) && DateTime.UtcNow - sw < TimeSpan.FromSeconds(10)) await Task.Delay(25);
        Assert.Contains(rig.Input.Snapshot(), e => e.Kind == InputKind.KeyDown);
    }

    [Fact]
    public async Task A_token_works_once_then_a_new_one_is_shown()
    {
        await using var rig = await Rig.StartAsync();
        var changed = new TaskCompletionSource();
        rig.Host.TokenChanged += () => changed.TrySetResult();
        var first = rig.Host.CurrentToken.Display;
        var result = await rig.ConnectAsync(first);
        await using var session = await ViewerSession.OpenAsync(result.Channel, "host", rig.Cts.Token);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(first, rig.Host.CurrentToken.Display);
        // The used token is dead: a second PC trying it gets a plain message.
        var ex = await Assert.ThrowsAsync<LanConnectException>(() => rig.ConnectAsync(first));
        Assert.Contains("did not accept the token", ex.Message);
    }

    [Fact]
    public async Task A_wrong_token_is_refused_and_three_wrong_tries_replace_the_token()
    {
        await using var rig = await Rig.StartAsync(maxFailures: 3);
        var real = rig.Host.CurrentToken;
        var wrong = new LanToken(real.Id, real.Secret.Length > 0 ? new string(real.Secret.Select(c => c == 'A' ? 'B' : 'A').ToArray()) : "AAAAAAAA").Display;
        var replaced = new TaskCompletionSource<string>();
        rig.Host.Notice += n => replaced.TrySetResult(n);
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<LanConnectException>(() => rig.ConnectAsync(wrong));
        }
        Assert.Contains("too many", await replaced.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(real.Display, rig.Host.CurrentToken.Display);
        // The real token of the old set no longer works either.
        await Assert.ThrowsAsync<LanConnectException>(() => rig.ConnectAsync(real.Display));
    }

    [Fact]
    public async Task The_host_says_nothing_to_someone_who_does_not_know_the_token()
    {
        await using var rig = await Rig.StartAsync();
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, rig.Host.TcpPort);
        var wire = new FramedStream(raw.GetStream());
        // A well-formed Hello1 for the right label, but the attacker then sends junk where the proof should be.
        using var hs = new LanInitiatorHandshake(new LanToken(rig.Host.CurrentToken.Id, "AAAAAAAA"));
        await wire.SendAsync(hs.CreateHello1(), rig.Cts.Token);
        Assert.NotNull(await wire.ReceiveAsync(rig.Cts.Token));            // Hello2 only
        await wire.SendAsync(new byte[40], rig.Cts.Token);                  // not a valid proof
        Assert.Null(await wire.ReceiveAsync(rig.Cts.Token));                // closed, and nothing was sent in between
    }

    [Fact]
    public async Task Discovery_finds_the_pc_by_the_label_and_a_different_label_finds_nothing()
    {
        await using var rig = await Rig.StartAsync();
        var target = new[] { new IPEndPoint(IPAddress.Loopback, rig.DiscoveryPort) };
        var found = await LanDiscovery.FindAsync(rig.Host.CurrentToken.Id, TimeSpan.FromSeconds(5), rig.Cts.Token, target, rig.DiscoveryPort);
        Assert.NotNull(found);
        Assert.Equal(rig.Host.TcpPort, found!.Endpoint.Port);
        Assert.Equal("test-pc", found.HostName);
        var other = rig.Host.CurrentToken.Id == "ZZZZ" ? "YYYY" : "ZZZZ";
        Assert.Null(await LanDiscovery.FindAsync(other, TimeSpan.FromMilliseconds(800), rig.Cts.Token, target, rig.DiscoveryPort));
    }

    [Fact]
    public async Task Typing_only_the_token_finds_the_pc_and_connects()
    {
        await using var rig = await Rig.StartAsync();
        var result = await LanConnector.ConnectAsync(rig.Host.CurrentToken.Display, "viewer-pc", hostAddress: null, rig.Cts.Token,
            extraDiscoveryTargets: [new IPEndPoint(IPAddress.Loopback, rig.DiscoveryPort)], discoveryPort: rig.DiscoveryPort);
        Assert.Equal("test-pc", result.HostName);
        await using var session = await ViewerSession.OpenAsync(result.Channel, "host", rig.Cts.Token);
        Assert.True(session.CanControl);
    }

    [Fact]
    public async Task A_missing_pc_gives_a_plain_message()
    {
        var ex = await Assert.ThrowsAsync<LanConnectException>(() => LanConnector.ConnectAsync("ZZZZ-AAAA-BBBB", "me", null, CancellationToken.None,
            extraDiscoveryTargets: [], discoveryPort: 1, discoveryTimeout: TimeSpan.FromMilliseconds(600)));
        Assert.Contains("same network", ex.Message);
    }

    [Fact]
    public void Only_addresses_on_the_same_subnet_count_as_this_network()
    {
        Assert.False(LanNetwork.IsOnLink(IPAddress.Parse("8.8.8.8"), allowLoopback: false));
        Assert.False(LanNetwork.IsOnLink(IPAddress.Loopback, allowLoopback: false));
        Assert.True(LanNetwork.IsOnLink(IPAddress.Loopback, allowLoopback: true));
        Assert.False(LanNetwork.IsOnLink(IPAddress.IPv6Loopback, allowLoopback: false));
    }

    [Fact]
    public async Task An_unused_token_expires_and_is_replaced()
    {
        await using var rig = await Rig.StartAsync(tokenLifetime: TimeSpan.FromSeconds(1.5));
        var first = rig.Host.CurrentToken.Display;
        var changed = new TaskCompletionSource();
        rig.Host.TokenChanged += () => changed.TrySetResult();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(first, rig.Host.CurrentToken.Display);
    }
}
