using System.Text;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;
using Remotely.HostCore;

namespace Remotely.Tests;

public class HostCoreTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private sealed class FakeScreen(Func<FrameInfo> next) : IFrameSource
    {
        public int Captures;
        public Task<FrameInfo> CaptureAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Captures);
            return Task.FromResult(next());
        }
    }

    [Fact]
    public void Frames_roundtrip_and_malformed_input_is_rejected()
    {
        var frame = new FrameInfo(1920, 1080, "Winlogon", [1, 2, 3, 4, 5]);
        var decoded = FrameCodec.Decode(FrameCodec.Encode(frame));
        Assert.Equal((1920, 1080, "Winlogon"), (decoded.Width, decoded.Height, decoded.Desktop));
        Assert.Equal(frame.Jpeg, decoded.Jpeg);

        var good = FrameCodec.Encode(frame);
        Assert.Throws<FormatException>(() => FrameCodec.Decode(good[..^1]));            // length mismatch
        Assert.Throws<FormatException>(() => FrameCodec.Decode([]));
        Assert.Throws<FormatException>(() => FrameCodec.Decode(good.Select((b, i) => i == 0 ? (byte)9 : b).ToArray())); // version
        var zeroWidth = (byte[])good.Clone(); Array.Clear(zeroWidth, 1, 4);
        Assert.Throws<FormatException>(() => FrameCodec.Decode(zeroWidth));
    }

    [Fact]
    public void Config_roundtrips_and_trust_is_deny_by_default()
    {
        var path = Path.Combine(Path.GetTempPath(), "remotely-cfg-" + Guid.NewGuid().ToString("N"), "host.json");
        try
        {
            Assert.False(HostConfig.Load(path).IsTrusted("abc"));         // no file: nobody trusted
            var c = new HostConfig { ServerUri = "wss://x/ws", ServerPin = "pin", TrustedViewers = ["ABCDEF"] };
            c.Save(path);
            var back = HostConfig.Load(path);
            Assert.Equal("wss://x/ws", back.ServerUri);
            Assert.True(back.IsTrusted("abcdef"));
            Assert.False(back.IsTrusted("other"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task A_trusted_viewer_receives_frames_from_the_host_through_the_server()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = await TestServer.StartAsync();
        var (hostClient, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = hostClient;
        var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        var screen = new FakeScreen(() => new FrameInfo(800, 600, "Default", Encoding.ASCII.GetBytes("jpeg-bytes")));
        var log = new List<string>();
        var agent = new HostAgent(hostId, screen, id => id == viewerId.DeviceId, log.Add);
        var hosting = agent.RunAsync(hostClient, cts.Token);

        var relay = await viewer.ConnectToAsync(hostId.DeviceId, cts.Token);
        using var session = await SecureSession.InitiateAsync(relay, viewerId, cts.Token);
        for (var i = 0; i < 3; i++)
        {
            await session.SendAsync(new[] { HostMessages.FrameRequest }, cts.Token);
            var reply = (await session.ReceiveAsync(cts.Token))!;
            Assert.Equal(HostMessages.FrameReply, reply[0]);
            var frame = FrameCodec.Decode(reply.AsSpan(1));
            Assert.Equal((800, 600, "Default"), (frame.Width, frame.Height, frame.Desktop));
            Assert.Equal("jpeg-bytes", Encoding.ASCII.GetString(frame.Jpeg));
        }
        Assert.Equal(3, screen.Captures);

        await session.SendAsync(new byte[] { 0x42 }, cts.Token);              // unknown request
        Assert.Equal(HostMessages.Error, (await session.ReceiveAsync(cts.Token))![0]);
        await relay.CloseAsync();
        await hostClient.DisposeAsync();
        await hosting;
    }

    [Fact]
    public async Task An_untrusted_viewer_gets_no_frames_and_the_host_never_captures()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = await TestServer.StartAsync();
        var (hostClient, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = hostClient;
        var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        var screen = new FakeScreen(() => new FrameInfo(1, 1, "Default", [0]));
        var log = new List<string>();
        var hosting = new HostAgent(hostId, screen, _ => false, log.Add).RunAsync(hostClient, cts.Token);

        var relay = await viewer.ConnectToAsync(hostId.DeviceId, cts.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => SecureSession.InitiateAsync(relay, viewerId, cts.Token));
        Assert.Equal(0, screen.Captures);
        Assert.Contains(log, l => l.Contains("Refused viewer") && l.Contains(viewerId.DeviceId));   // the id to trust is in the log
    }

    [Fact]
    public async Task A_capture_failure_is_reported_to_the_viewer_not_swallowed()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = await TestServer.StartAsync();
        var (hostClient, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = hostClient;
        var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        var failing = new FakeScreen(() => throw new InvalidOperationException("Helper is not running."));
        var hosting = new HostAgent(hostId, failing, _ => true, _ => { }).RunAsync(hostClient, cts.Token);

        var relay = await viewer.ConnectToAsync(hostId.DeviceId, cts.Token);
        using var session = await SecureSession.InitiateAsync(relay, viewerId, cts.Token);
        await session.SendAsync(new[] { HostMessages.FrameRequest }, cts.Token);
        var reply = (await session.ReceiveAsync(cts.Token))!;
        Assert.Equal(HostMessages.Error, reply[0]);
        Assert.Contains("Helper is not running", Encoding.UTF8.GetString(reply[1..]));
    }

    [Fact]
    public async Task An_oversized_frame_is_refused_instead_of_breaking_the_connection()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = await TestServer.StartAsync();
        var (hostClient, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
        await using var _h = hostClient;
        var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
        await using var _v = viewer;

        var huge = new FakeScreen(() => new FrameInfo(100, 100, "Default", new byte[1_100_000]));
        var hosting = new HostAgent(hostId, huge, _ => true, _ => { }).RunAsync(hostClient, cts.Token);

        var relay = await viewer.ConnectToAsync(hostId.DeviceId, cts.Token);
        using var session = await SecureSession.InitiateAsync(relay, viewerId, cts.Token);
        await session.SendAsync(new[] { HostMessages.FrameRequest }, cts.Token);
        Assert.Equal(HostMessages.Error, (await session.ReceiveAsync(cts.Token))![0]);
        await session.SendAsync(new[] { HostMessages.FrameRequest }, cts.Token);          // connection still usable
        Assert.Equal(HostMessages.Error, (await session.ReceiveAsync(cts.Token))![0]);
    }

    [Fact]
    public async Task The_host_pairs_itself_from_its_config_then_forgets_the_one_time_code()
    {
        using var cts = new CancellationTokenSource(Limit);
        await using var server = await TestServer.StartAsync();
        var dir = Path.Combine(Path.GetTempPath(), "remotely-host-" + Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(dir, "host.json");
        try
        {
            var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
            await using var _v = viewer;
            using var hostId = DeviceIdentity.Generate();

            new HostConfig
            {
                ServerUri = server.Options().ServerUri.ToString(),
                AllowInsecureTransport = true,
                DeviceName = "Config Host",
                PairCode = await server.NewPairCodeAsync(),
                TrustedViewers = [viewerId.DeviceId],
            }.Save(configPath);

            var screen = new FakeScreen(() => new FrameInfo(10, 10, "Default", [9, 9]));
            var log = new List<string>();
            var running = HostConnection.RunForeverAsync(configPath, hostId, screen, log.Add, cts.Token);

            // Wait until the host shows up online, then fetch a frame from it.
            DeviceInfo? host = null;
            while (host is null && !cts.IsCancellationRequested)
            {
                host = (await viewer.ListDevicesAsync(cts.Token)).FirstOrDefault(d => d.Name == "Config Host" && d.Online);
                if (host is null) await Task.Delay(100, cts.Token);
            }
            Assert.Equal(hostId.DeviceId, host!.DeviceId);
            Assert.Equal("", HostConfig.Load(configPath).PairCode);       // single-use code is gone

            var relay = await viewer.ConnectToAsync(host.DeviceId, cts.Token);
            using var session = await SecureSession.InitiateAsync(relay, viewerId, cts.Token);
            await session.SendAsync(new[] { HostMessages.FrameRequest }, cts.Token);
            Assert.Equal(HostMessages.FrameReply, (await session.ReceiveAsync(cts.Token))![0]);

            cts.Cancel();
            await running;
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
