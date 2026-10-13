using System.Text;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.Core.Protocol;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.Tests;

public class HostCoreTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    internal sealed class FakeScreen(Func<FrameInfo> next) : IFrameSource
    {
        public int Captures;
        public Task<FrameInfo> CaptureAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Captures);
            return Task.FromResult(next());
        }
    }

    internal sealed class RecordingInput : IInputSink, ISecureAttention
    {
        public readonly List<InputEvent> Events = [];
        public int Sas;
        public Task InjectAsync(IReadOnlyList<InputEvent> events, CancellationToken ct)
        {
            lock (Events) Events.AddRange(events);
            return Task.CompletedTask;
        }
        public Task SendAsync(CancellationToken ct) { Interlocked.Increment(ref Sas); return Task.CompletedTask; }
        public InputEvent[] Snapshot() { lock (Events) return [.. Events]; }
    }

    internal sealed class FakeClipboard : IClipboardSync
    {
        private long _sequence = 1;
        private string? _text = "initial";
        public string? Text { get { lock (this) return _text; } }
        public void LocalCopy(string text) { lock (this) { _text = text; _sequence++; } }
        public Task<(long, string?)> ReadAsync(CancellationToken ct) { lock (this) return Task.FromResult((_sequence, _text)); }
        public Task<long> WriteAsync(string text, CancellationToken ct) { lock (this) { _text = text; return Task.FromResult(++_sequence); } }
    }

    internal sealed class Rig : IAsyncDisposable
    {
        public required TestServer Server;
        public required CoordinatorClient HostClient, ViewerClient;
        public required DeviceIdentity HostId, ViewerId;
        public required Task Hosting;
        public CancellationTokenSource Cts = new(Limit);

        public static async Task<Rig> StartAsync(Func<DeviceIdentity /*viewer*/, HostServices> services, Action<string>? log = null)
        {
            var server = await TestServer.StartAsync();
            var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
            var (viewer, viewerId) = await server.PairedDeviceAsync("Viewer", canHost: false);
            var cts = new CancellationTokenSource(Limit);
            var agent = new HostAgent(hostId, services(viewerId), log ?? (_ => { }));
            return new Rig { Server = server, HostClient = host, ViewerClient = viewer, HostId = hostId, ViewerId = viewerId, Hosting = agent.RunAsync(host, cts.Token), Cts = cts };
        }

        public Task<ViewerSession> OpenAsync() => ViewerSession.OpenAsync(ViewerClient, ViewerId, HostId.DeviceId, Cts.Token);

        public async ValueTask DisposeAsync()
        {
            await ViewerClient.DisposeAsync();
            await HostClient.DisposeAsync();
            try { await Hosting.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            await Server.DisposeAsync();
            Cts.Dispose();
        }
    }

    private static FrameInfo Frame(string tag = "a", string desktop = "Default") => new(800, 600, desktop, Encoding.ASCII.GetBytes("jpeg-" + tag));

    private static async Task<T> Eventually<T>(Func<T?> probe, string what) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } v) return v;
            await Task.Delay(25);
        }
        throw new TimeoutException("Timed out waiting for " + what);
    }

    [Fact]
    public void Frames_roundtrip_and_malformed_input_is_rejected()
    {
        var frame = new FrameInfo(1920, 1080, "Winlogon", [1, 2, 3, 4, 5]);
        var decoded = FrameCodec.Decode(FrameCodec.Encode(frame));
        Assert.Equal((1920, 1080, "Winlogon"), (decoded.Width, decoded.Height, decoded.Desktop));
        Assert.Equal(frame.Jpeg, decoded.Jpeg);

        var good = FrameCodec.Encode(frame);
        Assert.Throws<FormatException>(() => FrameCodec.Decode(good[..^1]));
        Assert.Throws<FormatException>(() => FrameCodec.Decode([]));
        Assert.Throws<FormatException>(() => FrameCodec.Decode(good.Select((b, i) => i == 0 ? (byte)9 : b).ToArray()));
        var zeroWidth = (byte[])good.Clone(); Array.Clear(zeroWidth, 1, 4);
        Assert.Throws<FormatException>(() => FrameCodec.Decode(zeroWidth));
    }

    [Fact]
    public void Config_roundtrips_and_trust_is_deny_by_default()
    {
        var path = Path.Combine(Path.GetTempPath(), "remotely-cfg-" + Guid.NewGuid().ToString("N"), "host.json");
        try
        {
            Assert.False(HostConfig.Load(path).IsTrusted("abc"));
            var c = new HostConfig { ServerUri = "wss://x/ws", ServerPin = "pin", TrustedViewers = ["ABCDEF"], ViewOnlyViewers = ["WATCH"] };
            c.Save(path);
            var back = HostConfig.Load(path);
            Assert.Equal("wss://x/ws", back.ServerUri);
            Assert.True(back.IsTrusted("abcdef") && back.CanControl("abcdef"));
            Assert.True(back.IsTrusted("watch") && !back.CanControl("watch"));
            Assert.False(back.IsTrusted("other"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task A_trusted_viewer_receives_frames_and_unchanged_screens_are_not_resent()
    {
        var screen = new FakeScreen(() => Frame());
        await using var rig = await Rig.StartAsync(v => new HostServices(screen) { IsTrusted = id => id == v.DeviceId });
        await using var session = await rig.OpenAsync();

        var frames = new List<FrameInfo>();
        session.FrameReceived += f => { lock (frames) frames.Add(f); };
        await Eventually(() => { lock (frames) return frames.Count > 0 ? frames : null; }, "first frame");
        Assert.Equal((800, 600, "Default"), (frames[0].Width, frames[0].Height, frames[0].Desktop));
        Assert.Equal("jpeg-a", Encoding.ASCII.GetString(frames[0].Jpeg));

        await Eventually(() => screen.Captures >= 5 ? screen : null, "several polls");
        lock (frames) Assert.Single(frames);     // identical screen: later polls answered "unchanged", no new frame
        Assert.False(session.CanControl);          // no CanControl callback given: view only
    }

    [Fact]
    public async Task A_changed_screen_produces_a_new_frame()
    {
        var tag = "one";
        var screen = new FakeScreen(() => Frame(tag));
        await using var rig = await Rig.StartAsync(v => new HostServices(screen) { IsTrusted = _ => true });
        await using var session = await rig.OpenAsync();
        var frames = new List<string>();
        session.FrameReceived += f => { lock (frames) frames.Add(Encoding.ASCII.GetString(f.Jpeg)); };
        await Eventually(() => { lock (frames) return frames.Count > 0 ? frames : null; }, "first frame");
        tag = "two";
        await Eventually(() => { lock (frames) return frames.Contains("jpeg-two") ? frames : null; }, "changed frame");
    }

    [Fact]
    public async Task An_untrusted_viewer_gets_no_frames_and_the_host_never_captures()
    {
        var screen = new FakeScreen(() => Frame());
        var log = new List<string>();
        await using var rig = await Rig.StartAsync(v => new HostServices(screen) { IsTrusted = _ => false }, log.Add);
        await Assert.ThrowsAnyAsync<Exception>(() => rig.OpenAsync());
        Assert.Equal(0, screen.Captures);
        Assert.Contains(log, l => l.Contains("Refused viewer") && l.Contains(rig.ViewerId.DeviceId));
    }

    [Fact]
    public async Task A_capture_failure_is_reported_to_the_viewer_not_swallowed()
    {
        var failing = new FakeScreen(() => throw new InvalidOperationException("Helper is not running."));
        await using var rig = await Rig.StartAsync(v => new HostServices(failing) { IsTrusted = _ => true });
        await using var session = await rig.OpenAsync();
        var errors = new List<string>();
        session.HostError += e => { lock (errors) errors.Add(e); };
        var first = await Eventually(() => { lock (errors) return errors.FirstOrDefault(); }, "error");
        Assert.Contains("Helper is not running", first);
    }

    [Fact]
    public async Task An_oversized_frame_is_refused_instead_of_breaking_the_connection()
    {
        var huge = new FakeScreen(() => new FrameInfo(100, 100, "Default", new byte[1_100_000]));
        await using var rig = await Rig.StartAsync(v => new HostServices(huge) { IsTrusted = _ => true });
        await using var session = await rig.OpenAsync();
        var errors = new List<string>();
        session.HostError += e => { lock (errors) errors.Add(e); };
        await Eventually(() => { lock (errors) return errors.Count >= 2 ? errors : null; }, "two errors");   // still answering after the first
        Assert.All(errors, e => Assert.Contains("too large", e));
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

            var screen = new FakeScreen(() => Frame("cfg"));
            var log = new List<string>();
            var running = HostConnection.RunForeverAsync(configPath, hostId,
                (trusted, control) => new HostServices(screen) { IsTrusted = trusted, CanControl = control }, log.Add, cts.Token);

            DeviceInfo? host = null;
            while (host is null && !cts.IsCancellationRequested)
            {
                host = (await viewer.ListDevicesAsync(cts.Token)).FirstOrDefault(d => d.Name == "Config Host" && d.Online);
                if (host is null) await Task.Delay(100, cts.Token);
            }
            Assert.Equal(hostId.DeviceId, host!.DeviceId);
            Assert.Equal("", HostConfig.Load(configPath).PairCode);

            await using var session = await ViewerSession.OpenAsync(viewer, viewerId, host.DeviceId, cts.Token);
            var got = new TaskCompletionSource<FrameInfo>();
            session.FrameReceived += f => got.TrySetResult(f);
            Assert.Equal("jpeg-cfg", Encoding.ASCII.GetString((await got.Task.WaitAsync(cts.Token)).Jpeg));

            cts.Cancel();
            await running;
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
