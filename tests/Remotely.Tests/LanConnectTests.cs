using System.Net;
using System.Net.Sockets;
using System.Text;
using Remotely.Core.Lan;

namespace Remotely.Tests;

public class LanConnectTests
{
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(0);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    /// <summary>A PC that answers discovery: a real responder and the real serve loop, with a fixed label, name and TCP port.</summary>
    private sealed class FakeSharer : IAsyncDisposable
    {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        public int Port { get; }
        public IPEndPoint Target => new(IPAddress.Loopback, Port);

        public FakeSharer(string label, int tcpPort, string name)
        {
            _udp = LanDiscovery.OpenResponder(0);
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _loop = Task.Run(() => LanDiscovery.ServeAsync(_udp, () => label, () => tcpPort, name, allowLoopback: true, _cts.Token));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _udp.Dispose();
            await _loop.WaitAsync(TimeSpan.FromSeconds(3));
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task Several_PCs_answering_are_all_listed_with_names_and_labels_only()
    {
        await using var a = new FakeSharer("K7P4", 47001, "Kitchen");
        await using var b = new FakeSharer("QMH9", 47002, "Study");
        var seen = new List<LanDiscovery.Nearby>();
        var list = await LanDiscovery.ListAsync(TimeSpan.FromSeconds(1.5), CancellationToken.None, [a.Target, b.Target], FreeUdpPort(), seen.Add);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, n => n.Id == "K7P4" && n.HostName == "Kitchen" && n.Endpoint.Port == 47001);
        Assert.Contains(list, n => n.Id == "QMH9" && n.HostName == "Study" && n.Endpoint.Port == 47002);
        Assert.Equal(2, seen.Count);   // each PC is reported once even though the probe is re-sent
    }

    [Fact]
    public async Task The_list_reply_carries_the_label_port_and_name_and_nothing_else()
    {
        await using var sharer = new FakeSharer("K7P4", 47001, "Kitchen");
        using var raw = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var probe = Encoding.ASCII.GetBytes("RMTL").Concat(new byte[] { 0x12, 1 }).ToArray();
        await raw.SendAsync(probe, sharer.Target);
        var reply = (await raw.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer;

        var expected = Encoding.ASCII.GetBytes("RMTL").Concat(new byte[] { 0x13, 1 }).Concat(Encoding.ASCII.GetBytes("K7P4"))
            .Concat(new byte[] { 47001 >> 8, 47001 & 0xff }).Concat(Encoding.UTF8.GetBytes("Kitchen")).ToArray();
        Assert.Equal(expected, reply);
    }

    [Fact]
    public async Task The_old_single_label_probe_still_works_against_the_new_responder()
    {
        await using var sharer = new FakeSharer("K7P4", 47001, "Kitchen");
        var found = await LanDiscovery.FindAsync("K7P4", TimeSpan.FromSeconds(3), CancellationToken.None, [sharer.Target], FreeUdpPort());
        Assert.NotNull(found);
        Assert.Equal(47001, found.Endpoint.Port);
        Assert.Equal("Kitchen", found.HostName);
        Assert.Null(await LanDiscovery.FindAsync("ZZZZ", TimeSpan.FromMilliseconds(600), CancellationToken.None, [sharer.Target], FreeUdpPort()));
    }

    [Fact]
    public async Task A_version_one_PC_that_ignores_the_list_probe_is_simply_not_listed()
    {
        using var old = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = (IPEndPoint)old.Client.LocalEndPoint!;
        using var stop = new CancellationTokenSource();
        var oldLoop = Task.Run(async () =>
        {
            // Behaves like the first release: answers only a 9-byte probe of type 0x10.
            try
            {
                while (true)
                {
                    var r = await old.ReceiveAsync(stop.Token);
                    if (r.Buffer.Length == 9 && r.Buffer[4] == 0x10) await old.SendAsync(Encoding.ASCII.GetBytes("RMTL").Concat(new byte[] { 0x11 }).Concat(Encoding.ASCII.GetBytes("K7P4")).Concat(new byte[] { 0xB7, 0x01 }).ToArray(), r.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) { }
        });
        var list = await LanDiscovery.ListAsync(TimeSpan.FromMilliseconds(900), CancellationToken.None, [target], FreeUdpPort());
        stop.Cancel();
        await oldLoop;
        Assert.Empty(list);
    }

    [Fact]
    public async Task Malformed_list_replies_are_ignored()
    {
        using var bad = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = (IPEndPoint)bad.Client.LocalEndPoint!;
        using var stop = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var r = await bad.ReceiveAsync(stop.Token);
                    var magic = Encoding.ASCII.GetBytes("RMTL");
                    await bad.SendAsync(magic.Concat(new byte[] { 0x13, 1 }).Concat(Encoding.ASCII.GetBytes("K0I4")).Concat(new byte[] { 0xB7, 0x01 }).ToArray(), r.RemoteEndPoint);   // label with characters no token has
                    await bad.SendAsync(magic.Concat(new byte[] { 0x13, 1, 65 }).ToArray(), r.RemoteEndPoint);                                                                          // truncated
                    await bad.SendAsync(magic.Concat(new byte[] { 0x13, 1 }).Concat(Encoding.ASCII.GetBytes("K7P4")).Concat(new byte[] { 0, 0 }).ToArray(), r.RemoteEndPoint);        // port 0
                }
            }
            catch (OperationCanceledException) { }
        });
        var list = await LanDiscovery.ListAsync(TimeSpan.FromMilliseconds(900), CancellationToken.None, [target], FreeUdpPort());
        stop.Cancel();
        await loop;
        Assert.Empty(list);
    }

    [Fact]
    public async Task Cancelling_the_list_returns_quickly_with_what_was_found()
    {
        await using var a = new FakeSharer("K7P4", 47001, "Kitchen");
        using var cts = new CancellationTokenSource();
        var task = LanDiscovery.ListAsync(TimeSpan.FromSeconds(30), cts.Token, [a.Target], FreeUdpPort(), _ => cts.Cancel());
        var list = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(list);
    }

    [Fact]
    public async Task Cancelling_a_connection_attempt_stays_a_cancel_and_is_not_reported_as_nobody_found()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(400);
        var closed = new IPEndPoint(IPAddress.Loopback, FreeUdpPort());
        var attempt = LanConnector.ConnectAsync("K7P4-4NX2-QMH9", "viewer", null, cts.Token, [closed], FreeUdpPort(), TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Nobody_answering_gives_the_friendly_message()
    {
        var closed = new IPEndPoint(IPAddress.Loopback, FreeUdpPort());
        var ex = await Assert.ThrowsAsync<LanConnectException>(() =>
            LanConnector.ConnectAsync("K7P4-4NX2-QMH9", "viewer", null, CancellationToken.None, [closed], FreeUdpPort(), TimeSpan.FromMilliseconds(700)));
        Assert.Contains("K7P4", ex.Message);
    }

    [Fact]
    public async Task A_nearby_PC_completes_the_connection_when_the_matching_token_is_typed_and_progress_is_reported()
    {
        var discovery = FreeUdpPort();
        await using var host = new LanHost(new LanHostOptions { TcpPort = 0, DiscoveryPort = discovery, AllowLoopback = true, HostName = "test-pc" },
            async (_, _, ct) => { try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { } }, _ => { });
        host.Start();
        var nearby = await LanDiscovery.ListAsync(TimeSpan.FromSeconds(2), CancellationToken.None, [new IPEndPoint(IPAddress.Loopback, discovery)], FreeUdpPort());
        var entry = Assert.Single(nearby);
        Assert.Equal(host.CurrentToken.Id, entry.Id);
        Assert.Equal("test-pc", entry.HostName);

        var typed = host.CurrentToken.Display.ToLowerInvariant();
        Assert.Same(entry, TokenHints.MatchNearby(typed, nearby));

        var steps = new List<LanConnectStep>();
        var result = await LanConnector.ConnectAsync(typed, "viewer", null, CancellationToken.None, knownPc: TokenHints.MatchNearby(typed, nearby), progress: new SyncProgress(steps.Add));
        result.Channel.Dispose();
        Assert.Equal("test-pc", result.HostName);
        Assert.Equal([LanConnectStep.Reaching, LanConnectStep.Verifying], steps);
    }

    private sealed class SyncProgress(Action<LanConnectStep> report) : IProgress<LanConnectStep>
    {
        public void Report(LanConnectStep value) => report(value);
    }

    [Fact]
    public void Matching_a_nearby_PC_needs_the_whole_label_and_a_single_candidate()
    {
        var a = new LanDiscovery.Nearby("K7P4", new IPEndPoint(IPAddress.Loopback, 1), "A");
        var b = new LanDiscovery.Nearby("QMH9", new IPEndPoint(IPAddress.Loopback, 2), "B");
        var twin = new LanDiscovery.Nearby("K7P4", new IPEndPoint(IPAddress.Loopback, 3), "C");
        Assert.Same(b, TokenHints.MatchNearby("qmh9-4nx2", [a, b]));
        Assert.Null(TokenHints.MatchNearby("qmh", [a, b]));
        Assert.Null(TokenHints.MatchNearby("ZZZZ-4NX2-QMH9", [a, b]));
        Assert.Null(TokenHints.MatchNearby("K7P4-4NX2-QMH9", [a, twin]));
    }

    // ---- IPv6 ----

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("[::1]:47810")]
    [InlineData("2001:db8::5")]
    public async Task An_IPv6_address_is_refused_with_a_message_that_says_IPv6(string address)
    {
        var ex = await Assert.ThrowsAsync<LanConnectException>(() => LanConnector.ConnectAsync("K7P4-4NX2-QMH9", "viewer", address, CancellationToken.None));
        Assert.Contains("IPv6", ex.Message);
        Assert.Contains("192.168.1.20", ex.Message);
    }

    // ---- typo hints ----

    [Fact]
    public void Typo_hints_name_the_character_and_say_that_tokens_never_contain_it()
    {
        var zero = TokenHints.Check("K70");
        Assert.False(zero.IsValid);
        Assert.Equal([3], zero.BadPositions);
        Assert.Contains("number 0", zero.Message);
        Assert.Contains("letter O", zero.Message);

        var one = TokenHints.Check("k7p4-1");
        Assert.Equal([5], one.BadPositions);
        Assert.Contains("number 1", one.Message);
        Assert.Contains("letter I", one.Message);

        var letterO = TokenHints.Check("O");
        Assert.Contains("letter O", letterO.Message);
        var letterI = TokenHints.Check("I");
        Assert.Contains("letter I", letterI.Message);

        var two = TokenHints.Check("0O");
        Assert.Equal([1, 2], two.BadPositions);
    }

    [Fact]
    public void Typing_progress_and_completion_follow_LanToken()
    {
        Assert.True(TokenHints.Check("").IsValid);
        Assert.Equal("", TokenHints.Check("").Message);
        var partial = TokenHints.Check("K7P4-4N");
        Assert.True(partial.IsValid);
        Assert.False(partial.IsComplete);
        Assert.Equal("6 of 12 characters.", partial.Message);
        var full = TokenHints.Check("k7p4 4nx2 qmh9");
        Assert.True(full.IsComplete);
        var tooLong = TokenHints.Check("K7P4-4NX2-QMH9-K");
        Assert.False(tooLong.IsValid);
        Assert.False(LanToken.TryParse("K7P4-4NX2-QMH9-K", out _, out var expected));
        Assert.Equal(expected, tooLong.Message);
    }

    // ---- recent PCs ----

    [Fact]
    public void Recent_PCs_are_newest_first_without_duplicates_and_survive_a_restart()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "sub", "recent.json");
            var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var store = new JsonRecentPcs(path, () => clock += TimeSpan.FromMinutes(1));
            Assert.Empty(store.List());
            store.Record("Kitchen", "192.168.1.20:47810");
            store.Record("Study", "192.168.1.21:47810");
            store.Record("Kitchen PC", "192.168.1.20:47810");
            Assert.Equal(["Kitchen PC", "Study"], new JsonRecentPcs(path).List().Select(p => p.Name));

            store.Remove("192.168.1.21:47810");
            Assert.Single(store.List());
            store.Clear();
            Assert.Empty(store.List());
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Recent_PCs_keep_at_most_eight_and_never_store_tokens_and_a_damaged_file_reads_as_empty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "recent.json");
            var store = new JsonRecentPcs(path);
            for (var i = 0; i < 12; i++) store.Record("PC" + i, "10.0.0." + i);
            Assert.Equal(JsonRecentPcs.MaxEntries, store.List().Count);
            Assert.Equal("PC11", store.List()[0].Name);
            Assert.DoesNotContain("Secret", File.ReadAllText(path));

            File.WriteAllText(path, "{ not json");
            Assert.Empty(store.List());
            store.Record("Again", "10.0.0.99");
            Assert.Single(store.List());
        }
        finally { dir.Delete(true); }
    }

    // ---- diagnosis ----

    private sealed class FakeEnvironment : IDiagnosisEnvironment
    {
        public List<AdapterInfo> AdapterList = [new("Wi-Fi", IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"), false)];
        public List<LanDiscovery.Nearby> Replies = [];
        public TcpOutcome Tcp = TcpOutcome.Connected;
        public string? AskedLabel = "unset";
        public int DiscoverCalls, TcpCalls;
        public IReadOnlyList<AdapterInfo> Adapters() => AdapterList;
        public Task<IReadOnlyList<LanDiscovery.Nearby>> DiscoverAsync(string? label, CancellationToken ct)
        {
            DiscoverCalls++; AskedLabel = label;
            return Task.FromResult<IReadOnlyList<LanDiscovery.Nearby>>(Replies);
        }
        public Task<TcpOutcome> TryTcpAsync(IPEndPoint endpoint, CancellationToken ct) { TcpCalls++; return Task.FromResult(Tcp); }
    }

    private static LanDiscovery.Nearby Sharer(string ip = "192.168.1.20", int port = 47810) => new("K7P4", new IPEndPoint(IPAddress.Parse(ip), port), "Kitchen");

    [Fact]
    public async Task Diagnosis_passes_when_every_step_works()
    {
        var env = new FakeEnvironment { Replies = [Sharer()] };
        var report = await new LanDiagnosis(env).RunAsync(null, "K7P4", CancellationToken.None);
        Assert.True(report.Succeeded);
        Assert.All(report.Steps.Where(s => s.Step != DiagnosisStep.Subnet), s => Assert.Equal(StepState.Passed, s.State));
        Assert.Equal(StepState.Skipped, report.Steps.Single(s => s.Step == DiagnosisStep.Subnet).State);
        Assert.Equal("K7P4", env.AskedLabel);
    }

    [Fact]
    public async Task Diagnosis_reports_no_network_adapter()
    {
        var env = new FakeEnvironment { AdapterList = [] };
        var report = await new LanDiagnosis(env).RunAsync(null, "K7P4", CancellationToken.None);
        Assert.Equal(DiagnosisStep.Adapter, report.FailedStep);
        Assert.Contains("not connected to a network", report.Summary);
        Assert.Equal(0, env.DiscoverCalls);
        Assert.Equal(0, env.TcpCalls);
        Assert.Equal(3, report.Steps.Count(s => s.State == StepState.Skipped));
    }

    [Fact]
    public async Task Diagnosis_reports_an_IPv6_only_network_clearly()
    {
        var env = new FakeEnvironment { AdapterList = [new("Ethernet", null, null, true)] };
        var report = await new LanDiagnosis(env).RunAsync("192.168.1.20", "K7P4", CancellationToken.None);
        Assert.Equal(DiagnosisStep.Adapter, report.FailedStep);
        Assert.Contains("only an IPv6", report.Summary);
        Assert.Contains("IPv4", report.Summary);
        Assert.Equal(0, env.DiscoverCalls);
    }

    [Fact]
    public async Task Diagnosis_reports_a_self_assigned_address()
    {
        var env = new FakeEnvironment { AdapterList = [new("Ethernet", IPAddress.Parse("169.254.3.4"), IPAddress.Parse("255.255.0.0"), false)] };
        var report = await new LanDiagnosis(env).RunAsync(null, null, CancellationToken.None);
        Assert.Equal(DiagnosisStep.Adapter, report.FailedStep);
        Assert.Contains("169.254", report.Summary);
    }

    [Fact]
    public async Task Diagnosis_reports_a_different_subnet_and_does_not_search()
    {
        var env = new FakeEnvironment { Replies = [Sharer("10.0.0.5")] };
        var report = await new LanDiagnosis(env).RunAsync("10.0.0.5", "K7P4", CancellationToken.None);
        Assert.Equal(DiagnosisStep.Subnet, report.FailedStep);
        Assert.Contains("not on the same network", report.Summary);
        Assert.Equal(0, env.DiscoverCalls);
    }

    [Fact]
    public async Task Diagnosis_reports_an_IPv6_or_invalid_address_typed_by_the_person()
    {
        var env = new FakeEnvironment();
        var v6 = await new LanDiagnosis(env).RunAsync("fe80::1", null, CancellationToken.None);
        Assert.Equal(DiagnosisStep.Subnet, v6.FailedStep);
        Assert.Contains("IPv6", v6.Summary);
        var junk = await new LanDiagnosis(env).RunAsync("banana", null, CancellationToken.None);
        Assert.Equal(DiagnosisStep.Subnet, junk.FailedStep);
        Assert.Contains("not valid", junk.Summary);
    }

    [Fact]
    public async Task Diagnosis_reports_silence_on_the_search()
    {
        var env = new FakeEnvironment { Tcp = TcpOutcome.TimedOut };
        var report = await new LanDiagnosis(env).RunAsync(null, "K7P4", CancellationToken.None);
        Assert.Equal(DiagnosisStep.DiscoveryReply, report.FailedStep);
        Assert.Contains("did not answer the search", report.Summary);
        Assert.Contains("K7P4", report.Summary);
        Assert.Equal(0, env.TcpCalls);   // no PC found, no address: nothing to try
        Assert.Equal(StepState.Skipped, report.Steps.Single(s => s.Step == DiagnosisStep.ConnectionPort).State);
    }

    [Fact]
    public async Task Diagnosis_blames_the_firewall_when_the_PC_answers_the_search_but_not_the_connection()
    {
        var env = new FakeEnvironment { Replies = [Sharer()], Tcp = TcpOutcome.TimedOut };
        var report = await new LanDiagnosis(env).RunAsync(null, "K7P4", CancellationToken.None);
        Assert.Equal(DiagnosisStep.ConnectionPort, report.FailedStep);
        Assert.Contains("Windows Firewall", report.Summary);
        Assert.Contains("47810", report.Summary);
    }

    [Fact]
    public async Task Diagnosis_says_nothing_is_listening_when_the_connection_is_refused()
    {
        var env = new FakeEnvironment { Replies = [Sharer()], Tcp = TcpOutcome.Refused };
        var report = await new LanDiagnosis(env).RunAsync(null, "K7P4", CancellationToken.None);
        Assert.Equal(DiagnosisStep.ConnectionPort, report.FailedStep);
        Assert.Contains("nothing is accepting connections", report.Summary);
    }

    [Fact]
    public async Task Diagnosis_suggests_the_address_when_only_the_search_is_blocked()
    {
        var env = new FakeEnvironment { Tcp = TcpOutcome.Connected };
        var report = await new LanDiagnosis(env).RunAsync("192.168.1.20", "K7P4", CancellationToken.None);
        Assert.Equal(DiagnosisStep.DiscoveryReply, report.FailedStep);
        Assert.Contains("Enter its address", report.Summary);
        Assert.Equal(1, env.TcpCalls);
    }

    [Fact]
    public async Task Diagnosis_with_an_address_ignores_answers_from_other_PCs_and_uses_the_port_the_PC_announced()
    {
        var env = new FakeEnvironment { Replies = [Sharer("192.168.1.99", 1111), Sharer("192.168.1.20", 50000)] };
        var report = await new LanDiagnosis(env).RunAsync("192.168.1.20", null, CancellationToken.None);
        Assert.True(report.Succeeded);
        Assert.Contains("192.168.1.20:50000", report.Steps.Single(s => s.Step == DiagnosisStep.ConnectionPort).Message);
        Assert.Null(env.AskedLabel);
    }

    [Fact]
    public async Task Diagnosis_lists_several_PCs_that_answer()
    {
        var env = new FakeEnvironment { Replies = [Sharer("192.168.1.20"), new LanDiscovery.Nearby("QMH9", new IPEndPoint(IPAddress.Parse("192.168.1.21"), 47810), "Study")] };
        var report = await new LanDiagnosis(env).RunAsync(null, null, CancellationToken.None);
        Assert.Contains("2 PCs answered", report.Steps.Single(s => s.Step == DiagnosisStep.DiscoveryReply).Message);
    }

    [Fact]
    public async Task Diagnosis_can_be_cancelled()
    {
        var env = new FakeEnvironment { Replies = [Sharer()] };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LanDiagnosis(env).RunAsync(null, "K7P4", cts.Token));
    }

    [Fact]
    public async Task The_real_environment_tells_refused_from_connected_on_this_machine()
    {
        var env = new SystemDiagnosisEnvironment();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Equal(TcpOutcome.Connected, await env.TryTcpAsync(new IPEndPoint(IPAddress.Loopback, port), CancellationToken.None));
        listener.Stop();
        Assert.Equal(TcpOutcome.Refused, await env.TryTcpAsync(new IPEndPoint(IPAddress.Loopback, port), CancellationToken.None));
    }
}
