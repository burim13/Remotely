using System.Text;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.Server;

namespace Remotely.Tests;

/// <summary>
/// Hooks for the long-running soak test (A20): start N fake devices against a real <see cref="TestServer"/> (or any server, through
/// <see cref="FakeFleet.StartAsync(Func{Task{string}}, Func{CoordinatorOptions}, int, int, CancellationToken)"/>),
/// run encrypted echo sessions through the relay, reconnect some of them, and read the server's counters through the admin client.
/// The server must run with the rate limits off: <c>TestServer.StartAsync(configure: TestServer.NoLimits)</c> or <c>Remotely:Limits:*</c> set to 0.
/// </summary>
public sealed class FakeFleet : IAsyncDisposable
{
    private readonly Func<CoordinatorOptions> _options;
    private readonly Func<Task<string>> _newCode;
    private readonly List<Member> _members = [];
    private readonly CancellationTokenSource _stop = new();
    private long _echoes;
    private long _failures;

    private sealed class Member(DeviceIdentity identity, string name, bool host)
    {
        public DeviceIdentity Identity { get; } = identity;
        public string Name { get; } = name;
        public bool Host { get; } = host;
        public CoordinatorClient? Client { get; set; }
        public Task? Accepting { get; set; }
    }

    private FakeFleet(Func<Task<string>> newCode, Func<CoordinatorOptions> options)
    {
        _newCode = newCode;
        _options = options;
    }

    /// <summary>Devices that are connected right now.</summary>
    public int Connected => _members.Count(m => m.Client is { IsConnected: true });

    public int Hosts => _members.Count(m => m.Host);
    public int Viewers => _members.Count(m => !m.Host);

    /// <summary>Echo round trips that completed, and attempts that failed.</summary>
    public long Echoes => Interlocked.Read(ref _echoes);
    public long Failures => Interlocked.Read(ref _failures);

    public static Task<FakeFleet> StartAsync(TestServer server, int hosts, int viewers, CancellationToken ct = default) =>
        StartAsync(server.NewPairCodeAsync, () => server.Options(), hosts, viewers, ct);

    /// <param name="newCode">Returns a pairing code that is valid for one more device.</param>
    public static async Task<FakeFleet> StartAsync(Func<Task<string>> newCode, Func<CoordinatorOptions> options, int hosts, int viewers, CancellationToken ct = default)
    {
        var fleet = new FakeFleet(newCode, options);
        try
        {
            for (var i = 0; i < hosts + viewers; i++)
            {
                var host = i < hosts;
                var member = new Member(DeviceIdentity.Generate(), host ? $"fake-host-{i}" : $"fake-viewer-{i - hosts}", host);
                fleet._members.Add(member);
                await fleet.ConnectAsync(member, firstTime: true, ct);
            }
            return fleet;
        }
        catch
        {
            await fleet.DisposeAsync();
            throw;
        }
    }

    private async Task ConnectAsync(Member m, bool firstTime, CancellationToken ct)
    {
        var client = await CoordinatorClient.ConnectAsync(_options(), m.Identity, ct);
        if (firstTime) await client.PairAsync(await _newCode(), m.Name, m.Host, ct);
        m.Client = client;
        if (m.Host) m.Accepting = Task.Run(() => AcceptLoopAsync(m, client));
    }

    private async Task AcceptLoopAsync(Member m, CoordinatorClient client)
    {
        try
        {
            while (true)
            {
                var relay = await client.IncomingSessions.ReadAsync(_stop.Token);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var secure = await SecureSession.AcceptAsync(relay, m.Identity, _ => true, _stop.Token);
                        while (await secure.ReceiveAsync(_stop.Token) is { } request)
                            await secure.SendAsync(Encoding.UTF8.GetBytes("echo:" + Encoding.UTF8.GetString(request)), _stop.Token);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { }
                });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException) { }
    }

    /// <summary>Each viewer opens a session to a host, sends <paramref name="messagesPerSession"/> messages and checks the echoes, <paramref name="rounds"/> times.</summary>
    public async Task RunSessionsAsync(int rounds, int messagesPerSession = 3, CancellationToken ct = default)
    {
        var hosts = _members.Where(m => m.Host && m.Client is { IsConnected: true }).ToList();
        var viewers = _members.Where(m => !m.Host && m.Client is { IsConnected: true }).ToList();
        if (hosts.Count == 0 || viewers.Count == 0) return;
        for (var round = 0; round < rounds; round++)
        {
            await Task.WhenAll(viewers.Select((v, i) => OneSessionAsync(v, hosts[(i + round) % hosts.Count], messagesPerSession, ct)));
        }
    }

    private async Task OneSessionAsync(Member viewer, Member host, int messages, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var relay = await viewer.Client!.ConnectToAsync(host.Identity.DeviceId, timeout.Token);
            using var secure = await SecureSession.InitiateAsync(relay, viewer.Identity, timeout.Token);
            for (var i = 0; i < messages; i++)
            {
                await secure.SendAsync(Encoding.UTF8.GetBytes("m" + i), timeout.Token);
                var reply = await secure.ReceiveAsync(timeout.Token);
                if (reply is not null && Encoding.UTF8.GetString(reply) == "echo:m" + i) Interlocked.Increment(ref _echoes);
                else Interlocked.Increment(ref _failures);
            }
            await relay.CloseAsync();
        }
        catch (Exception ex) when (ex is CoordinatorException or HandshakeException or OperationCanceledException or IOException)
        {
            Interlocked.Increment(ref _failures);
        }
    }

    /// <summary>Drops <paramref name="count"/> random devices and connects them again with their saved identity (no new pairing).</summary>
    public async Task ChurnAsync(int count, CancellationToken ct = default)
    {
        foreach (var m in _members.OrderBy(_ => Random.Shared.Next()).Take(count).ToList())
        {
            if (m.Client is { } old) await old.DisposeAsync();
            await ConnectAsync(m, firstTime: false, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var m in _members)
            if (m.Client is { } c) await c.DisposeAsync();
        _stop.Dispose();
    }
}
