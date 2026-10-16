using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Remotely.Core.Admin;
using Remotely.Core.NetworkSetup;

namespace Remotely.Tests;

public class NetworkSetupTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remotely-netsetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- addresses -------------------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("10.1.2.3", AddressKind.Private)]
    [InlineData("172.16.0.1", AddressKind.Private)]
    [InlineData("172.31.255.1", AddressKind.Private)]
    [InlineData("172.32.0.1", AddressKind.Public)]
    [InlineData("192.168.1.50", AddressKind.Private)]
    [InlineData("100.64.0.1", AddressKind.CarrierGradeNat)]
    [InlineData("100.127.255.254", AddressKind.CarrierGradeNat)]
    [InlineData("100.128.0.1", AddressKind.Public)]
    [InlineData("169.254.10.10", AddressKind.LinkLocal)]
    [InlineData("127.0.0.1", AddressKind.Loopback)]
    [InlineData("8.8.8.8", AddressKind.Public)]
    [InlineData("fe80::1", AddressKind.LinkLocal)]
    [InlineData("fd00::1", AddressKind.Private)]
    [InlineData("2001:db8::1", AddressKind.Public)]
    public void Addresses_are_classified(string text, AddressKind expected) => Assert.Equal(expected, AddressClassifier.Classify(IPAddress.Parse(text)));

    private sealed class FakeAddresses(params LocalAddress[] list) : ILocalAddressSource
    {
        public IReadOnlyList<LocalAddress> Read() => list;
    }

    private static LocalAddress Addr(string ip, string name, bool gateway) => new(IPAddress.Parse(ip), name, AddressClassifier.Classify(IPAddress.Parse(ip)), gateway);

    [Fact]
    public void The_recommended_address_is_the_private_one_on_the_adapter_with_a_router_and_junk_is_left_out()
    {
        var detected = AddressDetector.Detect(new FakeAddresses(
            Addr("169.254.5.5", "Ethernet 2", false),
            Addr("192.168.56.1", "VirtualBox", false),
            Addr("192.168.1.50", "Wi-Fi", true),
            Addr("127.0.0.1", "lo", false),
            Addr("fe80::1", "Wi-Fi", true),
            new LocalAddress(IPAddress.Parse("2001:db8::5"), "Wi-Fi", AddressKind.Public, true)));
        Assert.Equal("192.168.1.50", detected.Recommended!.Address.ToString());
        Assert.Equal(["192.168.1.50", "192.168.56.1"], detected.Candidates.Select(c => c.Address.ToString()).ToArray());
    }

    [Fact]
    public void With_no_usable_address_nothing_is_recommended() =>
        Assert.Null(AddressDetector.Detect(new FakeAddresses(Addr("127.0.0.1", "lo", false))).Recommended);

    [Theory]
    [InlineData("", true, "", null)]
    [InlineData("  myhome.example.com ", true, "myhome.example.com", null)]
    [InlineData("myhome.example.com:9000", true, "myhome.example.com", 9000)]
    [InlineData("203.0.113.7", true, "203.0.113.7", null)]
    [InlineData("[2001:db8::1]:8443", true, "2001:db8::1", 8443)]
    [InlineData("wss://myhome.example.com/ws", false, "", null)]
    [InlineData("my home.example.com", false, "", null)]
    [InlineData("myhome.example.com:99999", false, "", null)]
    [InlineData("-bad.example.com", false, "", null)]
    [InlineData("exa_mple.com", false, "", null)]
    [InlineData("2001:db8::1", false, "", null)]
    public void The_public_name_is_checked(string text, bool valid, string host, int? port)
    {
        var r = PublicNameValidator.Validate(text);
        Assert.Equal(valid, r.IsValid);
        if (valid) { Assert.Equal(host, r.Host); Assert.Equal(port, r.Port); }
        else Assert.StartsWith("Setup_Err_", r.ErrorKey);
    }

    // ---- ports and reachability ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_port_probe_tells_free_from_in_use_from_invalid()
    {
        var probe = new SystemPortProbe();
        Assert.Equal(PortStatus.Invalid, await probe.CheckAsync(0));
        Assert.Equal(PortStatus.Invalid, await probe.CheckAsync(70000));
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try { Assert.Equal(PortStatus.InUse, await probe.CheckAsync(port)); }
        finally { listener.Stop(); }
        Assert.Equal(PortStatus.Free, await probe.CheckAsync(port));
    }

    private static async Task<(TcpListener Listener, CancellationTokenSource Stop, string Pin)> StartTlsServer(X509Certificate2 cert)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    _ = Task.Run(async () =>
                    {
                        using var c = client;
                        try
                        {
                            using var ssl = new SslStream(c.GetStream());
                            await ssl.AuthenticateAsServerAsync(cert);
                        }
                        catch (Exception) { }
                    });
                }
                catch (Exception) { return; }
            }
        });
        await Task.CompletedTask;
        return (listener, stop, CertificateInspector.Pin(cert));
    }

    [Fact]
    public async Task The_reachability_probe_checks_the_servers_key_pin_over_a_real_TLS_handshake()
    {
        using var cert = SelfSignedCertificates.Create(TimeSpan.FromDays(30), Now);
        var (listener, stop, pin) = await StartTlsServer(cert);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var probe = new TlsReachabilityProbe();
            var ok = await probe.ProbeAsync("127.0.0.1", port, tls: true, pin, TimeSpan.FromSeconds(5));
            Assert.Equal(ReachabilityOutcome.Reachable, ok.Outcome);
            Assert.Equal(pin, ok.Certificate!.PinHex);
            Assert.Equal(ReachabilityOutcome.WrongServer, (await probe.ProbeAsync("127.0.0.1", port, true, new string((char)48, 64), TimeSpan.FromSeconds(5))).Outcome);
            Assert.Equal(ReachabilityOutcome.Reachable, (await probe.ProbeAsync("127.0.0.1", port, true, null, TimeSpan.FromSeconds(5))).Outcome);
            Assert.Equal(ReachabilityOutcome.Reachable, (await probe.ProbeAsync("127.0.0.1", port, false, null, TimeSpan.FromSeconds(5))).Outcome);
        }
        finally { stop.Cancel(); listener.Stop(); }
    }

    [Fact]
    public async Task The_reachability_probe_reports_refused_and_not_TLS()
    {
        var probe = new TlsReachabilityProbe();
        Assert.Equal(ReachabilityOutcome.Refused, (await probe.ProbeAsync("127.0.0.1", FreePort(), true, null, TimeSpan.FromSeconds(5))).Outcome);

        var plain = new TcpListener(IPAddress.Loopback, 0);
        plain.Start();
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var c = await plain.AcceptTcpClientAsync(stop.Token);
                    await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\n\r\n"));
                }
            }
            catch (Exception) { }
        });
        try { Assert.Equal(ReachabilityOutcome.NotTls, (await probe.ProbeAsync("127.0.0.1", ((IPEndPoint)plain.LocalEndpoint).Port, true, null, TimeSpan.FromSeconds(5))).Outcome); }
        finally { stop.Cancel(); plain.Stop(); }
    }

    // ---- firewall (elevated side) ----------------------------------------------------------------------------------------------------------------------------

    private sealed class RecordingRunner(Func<IReadOnlyList<string>, ProcessOutput>? reply = null) : IProcessRunner
    {
        public List<(string File, string[] Args)> Calls { get; } = [];

        public Task<ProcessOutput> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add((file, arguments.ToArray()));
            return Task.FromResult(reply?.Invoke(arguments) ?? new ProcessOutput(0, ""));
        }
    }

    [Fact]
    public async Task The_firewall_rule_uses_the_same_name_and_arguments_as_the_server_installer()
    {
        var runner = new RecordingRunner();
        var result = await new NetshElevatedActions(runner, TempDir()).AddFirewallRuleAsync(9443);
        Assert.True(result.Ok);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(["advfirewall", "firewall", "delete", "rule", "name=Remotely Server"], runner.Calls[0].Args);
        Assert.Equal(["advfirewall", "firewall", "add", "rule", "name=Remotely Server", "dir=in", "action=allow", "protocol=TCP", "localport=9443"], runner.Calls[1].Args);
        Assert.All(runner.Calls, c => Assert.Equal("netsh.exe", c.File));
    }

    [Fact]
    public async Task A_failed_netsh_is_reported_with_its_message_and_a_bad_port_is_refused_without_running_anything()
    {
        var runner = new RecordingRunner(a => a.Contains("add") ? new ProcessOutput(1, "The requested operation requires elevation.") : new ProcessOutput(0, ""));
        var failed = await new NetshElevatedActions(runner, TempDir()).AddFirewallRuleAsync(8443);
        Assert.False(failed.Ok);
        Assert.Equal("Setup_Err_FirewallFailed", failed.ErrorKey);
        Assert.Contains("elevation", failed.Detail);
        var quiet = new RecordingRunner();
        Assert.Equal("Setup_Err_PortInvalid", (await new NetshElevatedActions(quiet, TempDir()).AddFirewallRuleAsync(0)).ErrorKey);
        Assert.Empty(quiet.Calls);
    }

    // The sample imitates the English output of "netsh advfirewall firewall show rule"; it was written from memory of that format, not captured from a Windows machine.
    private const string ShowRuleSample = """

        Rule Name:                            Remotely Server
        ----------------------------------------------------------------------
        Enabled:                              Yes
        Direction:                            In
        Profiles:                             Domain,Private,Public
        Grouping:
        LocalIP:                              Any
        RemoteIP:                             Any
        Protocol:                             TCP
        LocalPort:                            8443
        RemotePort:                           Any
        Edge traversal:                       No
        Action:                               Allow
        Ok.
        """;

    [Fact]
    public void The_show_rule_output_is_read()
    {
        Assert.Equal(FirewallRuleState.Present, NetshElevatedActions.ParseShowRule(new(0, ShowRuleSample), 8443));
        Assert.Equal(FirewallRuleState.PresentForOtherPort, NetshElevatedActions.ParseShowRule(new(0, ShowRuleSample), 9000));
        Assert.Equal(FirewallRuleState.Missing, NetshElevatedActions.ParseShowRule(new(1, "No rules match the specified criteria."), 8443));
        Assert.Equal(FirewallRuleState.Unknown, NetshElevatedActions.ParseShowRule(new(0, "Rule Name: Remotely Server\r\nEnabled: Yes"), 8443));
    }

    [Fact]
    public void Elevated_commands_round_trip_and_bad_ones_are_refused()
    {
        var cmd = new ElevatedCommand(ElevatedCommand.FirewallAdd, 8443, null);
        Assert.True(ElevatedCommand.TryParse(cmd.ToArguments(), out var back));
        Assert.Equal(cmd, back);
        Assert.True(ElevatedCommand.TryParse(new ElevatedCommand(ElevatedCommand.CertInstall, null, @"C:\x\my cert.pfx").ToArguments(), out var cert));
        Assert.Equal(@"C:\x\my cert.pfx", cert!.Path);
        Assert.False(ElevatedCommand.TryParse(["--elevated", "firewall-add"], out _));
        Assert.False(ElevatedCommand.TryParse(["--elevated", "format-disk"], out _));
        Assert.False(ElevatedCommand.TryParse(["--elevated", "firewall-add", "--port", "0"], out _));
        Assert.False(ElevatedCommand.TryParse(["--elevated", "firewall-remove", "--bogus"], out _));
        Assert.False(ElevatedCommand.TryParse(["firewall-add", "--port", "80"], out _));
        Assert.DoesNotContain(cmd.ToArguments(), a => a.Contains("pass", StringComparison.OrdinalIgnoreCase));
    }

    // ---- router guide, WAN, outside check --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_router_instructions_contain_the_exact_values()
    {
        var r = RouterGuide.Build(IPAddress.Parse("192.168.1.50"), 8443, "myhome.example.com", IPAddress.Parse("192.168.1.1"));
        Assert.Equal("TCP", r.Protocol);
        Assert.Equal(8443, r.ExternalPort);
        Assert.Equal(8443, r.InternalPort);
        Assert.Equal("192.168.1.50", r.InternalAddress);
        Assert.Contains(r.Steps, s => s.Key == "Setup_Router_Step_Values" && s.Args.SequenceEqual(new[] { "TCP", "8443", "192.168.1.50", "8443" }));
        Assert.Contains(r.Steps, s => s.Key == "Setup_Router_Step_OpenAt" && s.Args[0] == "192.168.1.1");
        Assert.Contains(r.Notes, s => s.Key == "Setup_Router_Note_Name" && s.Args[0] == "myhome.example.com");
        Assert.DoesNotContain(RouterGuide.Build(IPAddress.Parse("10.0.0.2"), 1, null).Steps, s => s.Key == "Setup_Router_Step_OpenAt");
    }

    [Theory]
    [InlineData("203.0.113.9", WanVerdict.PublicAddress)]
    [InlineData("100.72.1.1", WanVerdict.CarrierGradeNat)]
    [InlineData("192.168.0.2", WanVerdict.BehindAnotherRouter)]
    [InlineData("not an address", WanVerdict.NotAnAddress)]
    [InlineData("", WanVerdict.NotAnAddress)]
    public void The_routers_outside_address_shows_whether_forwarding_can_work(string text, WanVerdict expected) => Assert.Equal(expected, WanAssessment.Assess(text));

    private sealed class FakeOutside(params OutsideCheckHit?[] hits) : IOutsideCheck
    {
        private int _i;
        public int Polls => _i;
        public Task<OutsideCheckTicket> StartAsync(CancellationToken ct = default) => Task.FromResult(new OutsideCheckTicket("T1", "/check/7QK2"));
        public Task<OutsideCheckHit?> GetHitAsync(string ticketId, CancellationToken ct = default) => Task.FromResult(hits[Math.Min(_i++, hits.Length - 1)]);
    }

    private static OutsideCheckHit Hit(string ip) => new(IPAddress.Parse(ip), Now);

    [Fact]
    public void A_request_from_a_public_address_proves_the_outside_and_one_from_the_home_network_does_not()
    {
        Assert.Equal(OutsideVerdict.ReachableFromOutside, OutsideCheckEvaluator.Evaluate(Hit("198.51.100.20")));
        Assert.Equal(OutsideVerdict.SeenFromInside, OutsideCheckEvaluator.Evaluate(Hit("192.168.1.77")));
        Assert.Equal(OutsideVerdict.Unclear, OutsideCheckEvaluator.Evaluate(Hit("100.64.3.3")));
        Assert.Equal(OutsideVerdict.NothingArrived, OutsideCheckEvaluator.Evaluate(null));
    }

    [Fact]
    public async Task The_outside_test_keeps_waiting_after_a_request_from_inside_and_succeeds_when_the_phone_is_on_mobile_data()
    {
        var fake = new FakeOutside(null, Hit("192.168.1.77"), Hit("192.168.1.77"), Hit("198.51.100.20"));
        var (verdict, hit) = await OutsideCheckEvaluator.WaitAsync(fake, new("T1", "/check/7QK2"), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(1));
        Assert.Equal(OutsideVerdict.ReachableFromOutside, verdict);
        Assert.Equal("198.51.100.20", hit!.Source.ToString());
    }

    [Fact]
    public async Task The_outside_test_times_out_and_remembers_that_something_from_inside_did_arrive()
    {
        var (nothing, _) = await OutsideCheckEvaluator.WaitAsync(new FakeOutside((OutsideCheckHit?)null), new("T", "/c"), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(5));
        Assert.Equal(OutsideVerdict.NothingArrived, nothing);
        var (inside, _) = await OutsideCheckEvaluator.WaitAsync(new FakeOutside(Hit("10.0.0.9")), new("T", "/c"), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(5));
        Assert.Equal(OutsideVerdict.SeenFromInside, inside);
    }

    [Fact]
    public void The_check_address_is_built_from_the_name_port_and_ticket()
    {
        var t = new OutsideCheckTicket("T", "/check/7QK2");
        Assert.Equal("https://myhome.example.com:8443/check/7QK2", OutsideCheckEvaluator.BuildUrl("myhome.example.com", 8443, t));
        Assert.Equal("https://myhome.example.com/check/7QK2", OutsideCheckEvaluator.BuildUrl("myhome.example.com", 443, t));
        Assert.Equal("https://[2001:db8::1]:8443/check/7QK2", OutsideCheckEvaluator.BuildUrl("2001:db8::1", 8443, t));
    }

    // ---- port mapping (UPnP, NAT-PMP) ------------------------------------------------------------------------------------------------------------------------

    private sealed class FakeMapper(string name, bool succeeds, string errorKey = "Setup_Upnp_NoRouter") : IPortMapper
    {
        public string Name => name;
        public List<PortMappingRequest> Created { get; } = [];
        public List<PortMapping> Removed { get; } = [];
        public bool RemoveWorks { get; set; } = true;

        public Task<PortMappingAttempt> CreateAsync(PortMappingRequest r, CancellationToken ct = default)
        {
            Created.Add(r);
            return Task.FromResult(succeeds
                ? PortMappingAttempt.Success(new PortMapping(name, "TCP", r.ExternalPort, r.InternalPort, r.InternalAddress, IPAddress.Parse("203.0.113.9"), r.Lease, DateTimeOffset.UtcNow, r.Description))
                : PortMappingAttempt.Failure(errorKey));
        }

        public Task<bool> RemoveAsync(PortMapping m, CancellationToken ct = default) { Removed.Add(m); return Task.FromResult(RemoveWorks); }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private static PortMappingRequest Req() => new(IPAddress.Parse("192.168.1.50"), 8443, 8443, "Remotely Server", TimeSpan.FromHours(1));

    [Fact]
    public async Task Automatic_mapping_is_off_by_default_and_then_nothing_is_sent_to_the_router()
    {
        var mapper = new FakeMapper("UPnP", true);
        var session = new PortMappingSession([mapper]);
        Assert.False(session.AutomaticMappingEnabled);
        var attempt = await session.CreateAsync(Req());
        Assert.False(attempt.Ok);
        Assert.Equal("Setup_Upnp_Off", attempt.ErrorKey);
        Assert.Empty(mapper.Created);
        Assert.Empty(session.Mappings);
    }

    [Fact]
    public async Task When_switched_on_the_session_lists_exactly_what_the_router_accepted_and_removes_it_again()
    {
        var upnp = new FakeMapper("UPnP", false);
        var pmp = new FakeMapper("NAT-PMP", true);
        var session = new PortMappingSession([upnp, pmp]) { AutomaticMappingEnabled = true };
        var attempt = await session.CreateAsync(Req());
        Assert.True(attempt.Ok);
        var mapping = Assert.Single(session.Mappings);
        Assert.Equal("NAT-PMP", mapping.Mapper);
        var line = PortMappingSession.Describe(mapping);
        Assert.Equal("Setup_Upnp_Created", line.Key);
        Assert.Equal(["NAT-PMP", "TCP", "8443", "192.168.1.50", "8443", "203.0.113.9"], line.Args);

        Assert.Empty(await session.RemoveAllAsync());
        Assert.Equal(mapping, Assert.Single(pmp.Removed));
        Assert.Empty(session.Mappings);
    }

    [Fact]
    public async Task A_mapping_the_router_will_not_remove_stays_listed_so_the_user_can_see_it()
    {
        var mapper = new FakeMapper("UPnP", true) { RemoveWorks = false };
        var session = new PortMappingSession([mapper]) { AutomaticMappingEnabled = true };
        await session.CreateAsync(Req());
        Assert.Single(await session.RemoveAllAsync());
        Assert.Single(session.Mappings);
    }

    [Fact]
    public async Task The_most_informative_failure_wins_over_no_router_found()
    {
        var session = new PortMappingSession([new FakeMapper("UPnP", false, "Setup_Upnp_Conflict"), new FakeMapper("NAT-PMP", false)]) { AutomaticMappingEnabled = true };
        Assert.Equal("Setup_Upnp_Conflict", (await session.CreateAsync(Req())).ErrorKey);
    }

    [Fact]
    public async Task Leases_are_renewed_after_half_their_time()
    {
        var time = new ManualTime(Now);
        var mapper = new FakeMapper("UPnP", true);
        var session = new PortMappingSession([mapper], time) { AutomaticMappingEnabled = true };
        await session.CreateAsync(Req());
        time.Current = Now.AddMinutes(20);
        await session.RenewDueAsync();
        Assert.Single(mapper.Created);
        time.Current = Now.AddMinutes(31);
        await session.RenewDueAsync();
        Assert.Equal(2, mapper.Created.Count);
        Assert.Single(session.Mappings);
    }

    [Fact]
    public void NAT_PMP_packets_follow_the_RFC_6886_layout()
    {
        Assert.Equal([0, 0], NatPmpPacket.ExternalAddressRequest());
        Assert.Equal([0, 2, 0, 0, 0x20, 0xFB, 0x20, 0xFB, 0, 0, 0x0E, 0x10], NatPmpPacket.MapTcpRequest(8443, 8443, 3600));
        Assert.Equal([0, 2, 0, 0, 0x20, 0xFB, 0, 0, 0, 0, 0, 0], NatPmpPacket.MapTcpRequest(8443, 0, 0));

        Assert.True(NatPmpPacket.TryParseExternalAddress([0, 128, 0, 0, 0, 0, 0, 5, 203, 0, 113, 9], out var code, out var ip));
        Assert.Equal(0, code);
        Assert.Equal("203.0.113.9", ip!.ToString());
        Assert.True(NatPmpPacket.TryParseMapTcp([0, 130, 0, 0, 0, 0, 0, 5, 0x20, 0xFB, 0x22, 0x00, 0, 0, 0x0E, 0x10], out var c2, out var inner, out var outer, out var life));
        Assert.Equal((0, 8443, 8704, 3600u), ((int)c2, inner, outer, life));
        Assert.False(NatPmpPacket.TryParseMapTcp([0, 130, 0], out _, out _, out _, out _));
        Assert.False(NatPmpPacket.TryParseExternalAddress([1, 128, 0, 0, 0, 0, 0, 5, 1, 2, 3, 4], out _, out _));
    }

    private sealed class FakePmpRouter : INatPmpTransport
    {
        public List<byte[]> Requests { get; } = [];
        public ushort MapResult { get; set; }

        public Task<byte[]?> ExchangeAsync(IPAddress gateway, byte[] request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (request[1] == 0) return Task.FromResult<byte[]?>([0, 128, 0, 0, 0, 0, 0, 1, 203, 0, 113, 9]);
            var reply = new byte[16];
            reply[1] = 130;
            reply[2] = (byte)(MapResult >> 8); reply[3] = (byte)MapResult;
            Array.Copy(request, 4, reply, 8, 2);
            var external = request[6] == 0 && request[7] == 0 ? new byte[] { 0, 0 } : new byte[] { 0x22, 0x00 };   // this fake router hands out 8704 instead of the suggestion
            Array.Copy(external, 0, reply, 10, 2);
            Array.Copy(request, 8, reply, 12, 4);
            return Task.FromResult<byte[]?>(reply);
        }
    }

    [Fact]
    public async Task NAT_PMP_uses_the_port_the_router_actually_granted_and_removes_with_lifetime_zero()
    {
        var router = new FakePmpRouter();
        var mapper = new NatPmpPortMapper(() => [IPAddress.Parse("192.168.1.1")], router);
        var attempt = await mapper.CreateAsync(Req());
        Assert.True(attempt.Ok);
        Assert.Equal(8704, attempt.Mapping!.ExternalPort);
        Assert.Equal("203.0.113.9", attempt.Mapping.ExternalAddress!.ToString());
        Assert.True(await mapper.RemoveAsync(attempt.Mapping));
        Assert.Equal(NatPmpPacket.MapTcpRequest(8443, 0, 0), router.Requests[^1]);

        router.MapResult = 2;
        Assert.Equal("Setup_Upnp_Refused", (await mapper.CreateAsync(Req())).ErrorKey);
        Assert.Equal("Setup_Upnp_NoRouter", (await new NatPmpPortMapper(() => [], router).CreateAsync(Req())).ErrorKey);
    }

    // A router imitation: serves an IGD description and answers the three SOAP actions over real HTTP on loopback.
    private sealed class FakeIgd : IAsyncDisposable, IIgdLocator
    {
        private readonly HttpListener _http = new();
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; } = FreePort();
        public List<(string Action, string Body)> Calls { get; } = [];
        public int? FailAddWith { get; set; }
        public int? FailAddOnceWith { get; set; }
        public string ControlPath { get; set; } = "/ctl/IPConn";

        public FakeIgd()
        {
            _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _http.Start();
            _ = Task.Run(Loop);
        }

        public Task<IReadOnlyList<Uri>> FindAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Uri>>([new Uri($"http://127.0.0.1:{Port}/rootDesc.xml")]);

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync(); } catch (Exception) { return; }
                var path = ctx.Request.Url!.AbsolutePath;
                string reply;
                var status = 200;
                if (path == "/rootDesc.xml")
                {
                    reply = $"""
                        <?xml version="1.0"?><root xmlns="urn:schemas-upnp-org:device-1-0"><device><deviceList><device><deviceList><device><serviceList>
                        <service><serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType><controlURL>/ctl/CmnIfCfg</controlURL></service>
                        <service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>{ControlPath}</controlURL></service>
                        </serviceList></device></deviceList></device></deviceList></device></root>
                        """;
                }
                else
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var action = ctx.Request.Headers["SOAPAction"]!.Trim('"').Split('#')[1];
                    lock (Calls) Calls.Add((action, body));
                    if (action == "AddPortMapping" && (FailAddWith is not null || FailAddOnceWith is not null))
                    {
                        var code = FailAddOnceWith ?? FailAddWith;
                        FailAddOnceWith = null;
                        status = 500;
                        reply = $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError><errorCode>{code}</errorCode></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
                    }
                    else if (action == "GetExternalIPAddress")
                        reply = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetExternalIPAddressResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\"><NewExternalIPAddress>203.0.113.9</NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>";
                    else
                        reply = $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:{action}Response xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\"/></s:Body></s:Envelope>";
                }
                var bytes = Encoding.UTF8.GetBytes(reply);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "text/xml";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _http.Close();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task UPnP_creates_a_mapping_through_SOAP_reads_the_outside_address_and_deletes_it()
    {
        await using var igd = new FakeIgd();
        var mapper = new UpnpPortMapper(igd);
        var attempt = await mapper.CreateAsync(Req());
        Assert.True(attempt.Ok, attempt.ErrorKey);
        Assert.Equal("203.0.113.9", attempt.Mapping!.ExternalAddress!.ToString());
        Assert.Equal(TimeSpan.FromHours(1), attempt.Mapping.Lease);
        var add = igd.Calls.Single(c => c.Action == "AddPortMapping").Body;
        Assert.Contains("<NewExternalPort>8443</NewExternalPort>", add);
        Assert.Contains("<NewInternalClient>192.168.1.50</NewInternalClient>", add);
        Assert.Contains("<NewProtocol>TCP</NewProtocol>", add);
        Assert.Contains("<NewLeaseDuration>3600</NewLeaseDuration>", add);
        Assert.Contains("<NewPortMappingDescription>Remotely Server</NewPortMappingDescription>", add);

        Assert.True(await mapper.RemoveAsync(attempt.Mapping));
        var del = igd.Calls.Single(c => c.Action == "DeletePortMapping").Body;
        Assert.Contains("<NewExternalPort>8443</NewExternalPort>", del);
    }

    [Fact]
    public async Task UPnP_retries_without_a_lease_when_the_router_only_allows_permanent_mappings_and_does_not_override_a_conflict()
    {
        await using (var igd = new FakeIgd { FailAddOnceWith = 725 })
        {
            var attempt = await new UpnpPortMapper(igd).CreateAsync(Req());
            Assert.True(attempt.Ok);
            Assert.Equal(TimeSpan.Zero, attempt.Mapping!.Lease);
            Assert.Equal(2, igd.Calls.Count(c => c.Action == "AddPortMapping"));
        }
        await using (var igd = new FakeIgd { FailAddWith = 718 })
        {
            var attempt = await new UpnpPortMapper(igd).CreateAsync(Req());
            Assert.Equal("Setup_Upnp_Conflict", attempt.ErrorKey);
            Assert.Single(igd.Calls, c => c.Action == "AddPortMapping");
        }
    }

    private sealed class FixedLocator(params string[] urls) : IIgdLocator
    {
        public Task<IReadOnlyList<Uri>> FindAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Uri>>(urls.Select(u => new Uri(u)).ToList());
    }

    [Fact]
    public async Task UPnP_ignores_descriptions_that_are_not_on_the_local_network_and_reports_no_router()
    {
        var mapper = new UpnpPortMapper(new FixedLocator("http://8.8.8.8:1900/desc.xml", "http://example.com/desc.xml"));
        Assert.Equal("Setup_Upnp_NoRouter", (await mapper.CreateAsync(Req())).ErrorKey);
        Assert.False(await mapper.RemoveAsync(new PortMapping("UPnP", "TCP", 1, 1, IPAddress.Loopback, null, TimeSpan.Zero, Now, "x")));
    }

    [Fact]
    public void The_device_description_is_read_including_a_relative_control_url_and_a_url_base()
    {
        const string xml = """
            <?xml version="1.0"?><root xmlns="urn:schemas-upnp-org:device-1-0"><URLBase>http://192.168.1.1:5000/</URLBase><device><serviceList>
            <service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType><controlURL>ctl/ppp</controlURL></service></serviceList></device></root>
            """;
        var s = IgdDescription.Parse(xml, new Uri("http://192.168.1.1:49152/rootDesc.xml"));
        Assert.Equal("http://192.168.1.1:5000/ctl/ppp", s!.ControlUrl.ToString());
        Assert.EndsWith("WANPPPConnection:1", s.ServiceType);
        Assert.Null(IgdDescription.Parse("<root/>", new Uri("http://192.168.1.1/d.xml")));
        Assert.Null(IgdDescription.Parse("not xml", new Uri("http://192.168.1.1/d.xml")));
    }

    [Fact]
    public void The_SSDP_search_and_answer_are_formed_and_read_correctly()
    {
        var search = SsdpIgdLocator.BuildSearch("urn:schemas-upnp-org:device:InternetGatewayDevice:1");
        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", search);
        Assert.Contains("MAN: \"ssdp:discover\"", search);
        Assert.EndsWith("\r\n\r\n", search);
        Assert.Equal("http://192.168.1.1:49152/rootDesc.xml", SsdpIgdLocator.ParseLocation("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nlocation: http://192.168.1.1:49152/rootDesc.xml\r\nST: x\r\n\r\n")!.ToString());
        Assert.Null(SsdpIgdLocator.ParseLocation("HTTP/1.1 200 OK\r\nLOCATION: ftp://x/y\r\n\r\n"));
        Assert.Null(SsdpIgdLocator.ParseLocation("garbage"));
    }

    // ---- certificates ----------------------------------------------------------------------------------------------------------------------------------------

    private static byte[] MakePfx(string? password, DateTimeOffset notBefore, DateTimeOffset notAfter, bool withPrivateKey = true, string subject = "CN=Test")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(notBefore, notAfter);
        if (withPrivateKey) return cert.Export(X509ContentType.Pfx, password);
        using var publicOnly = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        return new X509Certificate2Collection(publicOnly).Export(X509ContentType.Pfx, password)!;
    }

    [Fact]
    public void A_good_pfx_is_accepted_and_shows_pin_fingerprint_and_dates()
    {
        var pfx = MakePfx("secret", Now.AddDays(-2), Now.AddYears(1));
        var v = PfxValidator.Validate(pfx, "secret", Now);
        Assert.True(v.IsValid);
        Assert.Null(v.ErrorKey);
        Assert.Matches("^[0-9a-f]{64}$", v.Summary!.PinHex);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", v.Summary.FingerprintSha256);
        Assert.True(v.Summary.IsSelfSigned);
        Assert.Equal(Now.AddYears(1).ToUnixTimeSeconds(), v.Summary.NotAfter.ToUnixTimeSeconds());
        Assert.Empty(v.WarningKeys);
    }

    [Fact]
    public void A_pfx_without_a_password_opens_with_an_empty_or_missing_password()
    {
        var pfx = MakePfx(null, Now.AddDays(-2), Now.AddYears(1));
        Assert.True(PfxValidator.Validate(pfx, null, Now).IsValid);
        Assert.True(PfxValidator.Validate(pfx, "", Now).IsValid);
    }

    [Fact]
    public void An_expired_pfx_is_refused_with_its_own_message()
    {
        var v = PfxValidator.Validate(MakePfx("pw", Now.AddYears(-3), Now.AddDays(-5)), "pw", Now);
        Assert.Equal(PfxProblem.Expired, v.Problem);
        Assert.Equal("Setup_Pfx_Expired", v.ErrorKey);
        Assert.NotNull(v.Summary);
    }

    [Fact]
    public void A_pfx_that_is_not_valid_yet_is_refused()
    {
        var v = PfxValidator.Validate(MakePfx("pw", Now.AddDays(10), Now.AddYears(1)), "pw", Now);
        Assert.Equal(PfxProblem.NotYetValid, v.Problem);
    }

    [Fact]
    public void A_wrong_password_is_reported_as_such_and_not_as_a_broken_file()
    {
        var pfx = MakePfx("right", Now.AddDays(-2), Now.AddYears(1));
        Assert.Equal(PfxProblem.WrongPassword, PfxValidator.Validate(pfx, "wrong", Now).Problem);
        Assert.Equal(PfxProblem.WrongPassword, PfxValidator.Validate(pfx, null, Now).Problem);
    }

    [Fact]
    public void A_file_with_certificates_but_no_private_key_is_refused()
    {
        var v = PfxValidator.Validate(MakePfx("pw", Now.AddDays(-2), Now.AddYears(1), withPrivateKey: false), "pw", Now);
        Assert.Equal(PfxProblem.NoPrivateKey, v.Problem);
        Assert.Equal("Setup_Pfx_NoPrivateKey", v.ErrorKey);
    }

    [Fact]
    public void Something_that_is_not_a_pfx_is_refused_without_throwing()
    {
        Assert.Equal(PfxProblem.NotAPfx, PfxValidator.Validate(Encoding.UTF8.GetBytes("hello, I am a text file"), "pw", Now).Problem);
        Assert.Equal(PfxProblem.NotAPfx, PfxValidator.Validate([], "pw", Now).Problem);
        Assert.Equal(PfxProblem.NotAPfx, PfxValidator.Validate(new byte[200], null, Now).Problem);
    }

    [Fact]
    public void A_pfx_close_to_expiry_is_accepted_with_a_warning()
    {
        var v = PfxValidator.Validate(MakePfx("pw", Now.AddYears(-1), Now.AddDays(20)), "pw", Now);
        Assert.True(v.IsValid);
        Assert.Contains("Setup_Pfx_Warn_ExpiresSoon", v.WarningKeys);
    }

    [Fact]
    public void Expiry_states_follow_the_sixty_day_window()
    {
        Assert.Equal(ExpiryState.Ok, CertificateInspector.Expiry(Now.AddYears(-1), Now.AddDays(61), Now));
        Assert.Equal(ExpiryState.RenewSoon, CertificateInspector.Expiry(Now.AddYears(-1), Now.AddDays(60), Now));
        Assert.Equal(ExpiryState.Expired, CertificateInspector.Expiry(Now.AddYears(-1), Now, Now));
        Assert.Equal(ExpiryState.NotYetValid, CertificateInspector.Expiry(Now.AddDays(1), Now.AddYears(1), Now));
    }

    [Fact]
    public void Renewing_keeps_the_pin_so_no_device_has_to_be_invited_again_but_changes_the_certificate()
    {
        using var old = SelfSignedCertificates.Create(TimeSpan.FromDays(20), Now);
        using var renewed = SelfSignedCertificates.RenewKeepingKey(old, TimeSpan.FromDays(3650), Now);
        Assert.Equal(CertificateInspector.Pin(old), CertificateInspector.Pin(renewed));
        Assert.NotEqual(CertificateInspector.Fingerprint(old), CertificateInspector.Fingerprint(renewed));
        Assert.True(renewed.NotAfter.ToUniversalTime() > old.NotAfter.ToUniversalTime().AddYears(5));
        Assert.True(renewed.HasPrivateKey);
    }

    [Fact]
    public void A_new_self_signed_certificate_has_a_different_pin()
    {
        using var a = SelfSignedCertificates.Create(TimeSpan.FromDays(30), Now);
        using var b = SelfSignedCertificates.Create(TimeSpan.FromDays(30), Now);
        Assert.NotEqual(CertificateInspector.Pin(a), CertificateInspector.Pin(b));
    }

    [Fact]
    public void The_pin_matches_the_value_the_server_prints()
    {
        using var cert = SelfSignedCertificates.Create(TimeSpan.FromDays(30), Now);
        Assert.Equal(Remotely.Server.ServerCertificate.Pin(cert), CertificateInspector.Pin(cert));
    }

    [Fact]
    public async Task Installing_a_pfx_backs_up_the_old_certificate_and_stores_one_the_server_can_open_without_a_password()
    {
        var data = TempDir();
        try
        {
            using var first = Remotely.Server.ServerCertificate.LoadOrCreate(data);
            var oldPin = CertificateInspector.Pin(first);
            var source = Path.Combine(data, "mine.pfx");
            await File.WriteAllBytesAsync(source, MakePfx("pw", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(1)));
            var result = await new NetshElevatedActions(new RecordingRunner(), data).InstallCertificateAsync(source, "pw");
            Assert.True(result.Ok, result.ErrorKey);
            Assert.NotEqual(oldPin, result.Detail);
            Assert.Single(Directory.GetFiles(data, "server.pfx.bak-*"));
            using var loaded = Remotely.Server.ServerCertificate.LoadOrCreate(data);
            Assert.Equal(result.Detail, Remotely.Server.ServerCertificate.Pin(loaded));
        }
        finally { Directory.Delete(data, true); }
    }

    [Fact]
    public async Task Renewing_the_servers_certificate_keeps_its_pin_backs_up_the_old_file_and_the_server_loader_still_opens_it()
    {
        var data = TempDir();
        try
        {
            string oldPin, oldFingerprint;
            using (var first = Remotely.Server.ServerCertificate.LoadOrCreate(data)) { oldPin = Remotely.Server.ServerCertificate.Pin(first); oldFingerprint = CertificateInspector.Fingerprint(first); }
            var result = await new NetshElevatedActions(new RecordingRunner(), data).RenewCertificateAsync(TimeSpan.FromDays(3650));
            Assert.True(result.Ok, result.ErrorKey);
            Assert.Equal(oldPin, result.Detail);
            Assert.Single(Directory.GetFiles(data, "server.pfx.bak-*"));
            using var loaded = Remotely.Server.ServerCertificate.LoadOrCreate(data);
            Assert.Equal(oldPin, Remotely.Server.ServerCertificate.Pin(loaded));
            Assert.NotEqual(oldFingerprint, CertificateInspector.Fingerprint(loaded));
        }
        finally { Directory.Delete(data, true); }
    }

    [Fact]
    public async Task Renewing_without_a_current_certificate_says_so()
    {
        var data = TempDir();
        try { Assert.Equal("Setup_Pfx_NoCurrent", (await new NetshElevatedActions(new RecordingRunner(), data).RenewCertificateAsync(TimeSpan.FromDays(30))).ErrorKey); }
        finally { Directory.Delete(data, true); }
    }

    [Fact]
    public async Task Installing_a_bad_pfx_changes_nothing()
    {
        var data = TempDir();
        try
        {
            using var first = Remotely.Server.ServerCertificate.LoadOrCreate(data);
            var before = await File.ReadAllBytesAsync(Path.Combine(data, "server.pfx"));
            var source = Path.Combine(data, "bad.pfx");
            await File.WriteAllBytesAsync(source, MakePfx("right", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(1)));
            var actions = new NetshElevatedActions(new RecordingRunner(), data);
            Assert.Equal("Setup_Pfx_WrongPassword", (await actions.InstallCertificateAsync(source, "wrong")).ErrorKey);
            Assert.Equal("Setup_Pfx_CannotRead", (await actions.InstallCertificateAsync(Path.Combine(data, "missing.pfx"), null)).ErrorKey);
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(data, "server.pfx")));
            Assert.Empty(Directory.GetFiles(data, "server.pfx.bak-*"));
        }
        finally { Directory.Delete(data, true); }
    }

    // ---- the wizard with fake probes -------------------------------------------------------------------------------------------------------------------------

    private sealed class FakeAdmin(ServerStatus status) : IServerAdmin
    {
        public ServerSettings Settings { get; set; } = new("", 8443, 100, 4, true);
        public Task<ServerStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(status);
        public Task<ServerSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(Settings);
        public Task SetSettingsAsync(ServerSettings settings, CancellationToken ct = default) { Settings = settings; return Task.CompletedTask; }
        public Task<InviteInfo> CreateInviteAsync(TimeSpan lifetime, int uses, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeInviteAsync(string inviteId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdminDevice>> ListDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeDeviceAsync(string deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetDeviceBlockedAsync(string deviceId, bool blocked, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(DateTimeOffset? since = null, int max = 500, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> CreateBackupAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakePorts(PortStatus status) : IPortProbe
    {
        public Task<PortStatus> CheckAsync(int port, CancellationToken ct = default) => Task.FromResult(status);
    }

    private sealed class FakeReach(Func<string, ReachabilityOutcome> outcome, string? pin = null) : IReachabilityProbe
    {
        public List<string> Hosts { get; } = [];

        public Task<ReachabilityResult> ProbeAsync(string host, int port, bool tls, string? expectedPinHex, TimeSpan timeout, CancellationToken ct = default)
        {
            Hosts.Add(host);
            var o = outcome(host);
            var summary = pin is null ? null : new CertificateSummary("CN=Remotely", "CN=Remotely", Now.AddDays(-1), Now.AddYears(10), pin, "AA:BB", true, "ECC");
            return Task.FromResult(new ReachabilityResult(o, host, port, pin, summary));
        }
    }

    private sealed class FakeElevated : IElevatedActions
    {
        public FirewallRuleState State { get; set; } = FirewallRuleState.Missing;
        public List<int> Added { get; } = [];
        public Task<FirewallRuleState> GetFirewallRuleAsync(int port, CancellationToken ct = default) => Task.FromResult(State);
        public Task<ActionResult> AddFirewallRuleAsync(int port, CancellationToken ct = default) { Added.Add(port); return Task.FromResult(ActionResult.Success()); }
        public Task<ActionResult> RemoveFirewallRuleAsync(CancellationToken ct = default) => Task.FromResult(ActionResult.Success());
        public Task<ActionResult> InstallCertificateAsync(string pfxPath, string? password, CancellationToken ct = default) => Task.FromResult(ActionResult.Success());
        public Task<ActionResult> RenewCertificateAsync(TimeSpan validity, CancellationToken ct = default) => Task.FromResult(ActionResult.Success());
    }

    private static ServerStatus Status(int port = 8443, string pin = "ab", bool tls = true, string address = "wss://192.168.1.50:8443/ws") =>
        new("0.4.0", Now, address, port, tls, pin, Now.AddYears(5), 1, 0, 0);

    private static (SetupWizard Wizard, FakeElevated Elevated, FakeAdmin Admin, FakeOutside Outside) Make(PortStatus port, Func<string, ReachabilityOutcome> reach, ServerStatus? status = null, FakeOutside? outside = null)
    {
        var elevated = new FakeElevated();
        var admin = new FakeAdmin(status ?? Status());
        outside ??= new FakeOutside(Hit("198.51.100.20"));
        var services = new SetupWizardServices(
            new FakeAddresses(Addr("192.168.1.50", "Wi-Fi", true), Addr("169.254.1.1", "x", false)),
            new FakePorts(port), new FakeReach(reach, "ab"), elevated, outside,
            new PortMappingSession([]), admin, () => [IPAddress.Parse("192.168.1.1")]);
        return (new SetupWizard(services), elevated, admin, outside);
    }

    [Fact]
    public async Task The_wizard_loads_port_and_pin_from_the_server_and_picks_the_address()
    {
        var (wizard, _, _, _) = Make(PortStatus.Free, _ => ReachabilityOutcome.Reachable, Status(port: 9000, pin: "cd"));
        var snapshot = await wizard.LoadServerAsync();
        Assert.Equal(9000, wizard.Port);
        Assert.Equal("cd", wizard.ServerPin);
        Assert.Equal("192.168.1.50", snapshot!.Address.Contains("192.168.1.50") ? "192.168.1.50" : "");
        Assert.Equal("", wizard.PublicName);   // a home-network address is not offered as a "public name"
        Assert.Equal("192.168.1.50", wizard.DetectAddresses().Recommended!.Address.ToString());
        Assert.Equal("192.168.1.50", wizard.ChosenLocalAddress!.ToString());
    }

    [Fact]
    public async Task A_public_name_already_set_on_the_server_is_picked_up()
    {
        var (wizard, _, _, _) = Make(PortStatus.Free, _ => ReachabilityOutcome.Reachable, Status(address: "wss://myhome.example.com:8443/ws"));
        await wizard.LoadServerAsync();
        Assert.Equal("myhome.example.com", wizard.PublicName);
    }

    [Fact]
    public async Task Without_an_admin_connection_the_wizard_still_works_with_defaults()
    {
        var services = new SetupWizardServices(new FakeAddresses(), new FakePorts(PortStatus.Free), new FakeReach(_ => ReachabilityOutcome.Refused), new FakeElevated(),
            new UnavailableOutsideCheck(), new PortMappingSession([]), null, () => []);
        var wizard = new SetupWizard(services);
        Assert.Null(await wizard.LoadServerAsync());
        Assert.Equal(8443, wizard.Port);
        Assert.False(await wizard.ApplyToServerAsync());
        Assert.Null(wizard.GetRouterInstructions());
    }

    [Fact]
    public async Task A_free_port_is_free_and_a_port_held_by_this_server_is_recognised_as_such()
    {
        var (free, _, _, _) = Make(PortStatus.Free, _ => ReachabilityOutcome.Refused);
        Assert.Equal(PortStepVerdict.Free, (await free.CheckPortAsync()).Verdict);

        var (ours, _, _, _) = Make(PortStatus.InUse, _ => ReachabilityOutcome.Reachable);
        await ours.LoadServerAsync();
        Assert.Equal(PortStepVerdict.UsedByThisServer, (await ours.CheckPortAsync()).Verdict);

        var (other, _, _, _) = Make(PortStatus.InUse, _ => ReachabilityOutcome.WrongServer);
        await other.LoadServerAsync();
        Assert.Equal(PortStepVerdict.UsedBySomethingElse, (await other.CheckPortAsync()).Verdict);

        var (denied, _, _, _) = Make(PortStatus.NotAllowed, _ => ReachabilityOutcome.Refused);
        Assert.Equal(PortStepVerdict.NotAllowed, (await denied.CheckPortAsync()).Verdict);
    }

    [Fact]
    public async Task A_listener_is_not_called_ours_when_the_servers_key_is_not_known()
    {
        var (wizard, _, _, _) = Make(PortStatus.InUse, _ => ReachabilityOutcome.Reachable);
        Assert.Equal(PortStepVerdict.UsedBySomethingElse, (await wizard.CheckPortAsync()).Verdict);
    }

    [Fact]
    public async Task The_firewall_step_adds_the_rule_for_the_chosen_port()
    {
        var (wizard, elevated, _, _) = Make(PortStatus.Free, _ => ReachabilityOutcome.Reachable);
        Assert.True(wizard.TrySetPort(9443));
        Assert.False(wizard.TrySetPort(0));
        Assert.Equal(FirewallRuleState.Missing, await wizard.GetFirewallAsync());
        Assert.True((await wizard.AddFirewallRuleAsync()).Ok);
        Assert.Equal([9443], elevated.Added);
    }

    [Fact]
    public async Task The_inside_test_separates_server_down_from_blocked_by_a_firewall_and_a_working_server()
    {
        var (works, _, _, _) = Make(PortStatus.InUse, _ => ReachabilityOutcome.Reachable);
        await works.LoadServerAsync(); works.DetectAddresses();
        Assert.Equal(InsideVerdict.Works, (await works.TestInsideAsync()).Verdict);

        var (blocked, _, _, _) = Make(PortStatus.InUse, h => h == "127.0.0.1" ? ReachabilityOutcome.Reachable : ReachabilityOutcome.TimedOut);
        await blocked.LoadServerAsync(); blocked.DetectAddresses();
        var report = await blocked.TestInsideAsync();
        Assert.Equal(InsideVerdict.BlockedOnNetworkAddress, report.Verdict);
        Assert.Equal(ReachabilityOutcome.TimedOut, report.NetworkAddress!.Outcome);

        var (down, _, _, _) = Make(PortStatus.Free, _ => ReachabilityOutcome.Refused);
        await down.LoadServerAsync(); down.DetectAddresses();
        Assert.Equal(InsideVerdict.ServerNotAnswering, (await down.TestInsideAsync()).Verdict);

        var (wrong, _, _, _) = Make(PortStatus.InUse, _ => ReachabilityOutcome.WrongServer);
        await wrong.LoadServerAsync(); wrong.DetectAddresses();
        Assert.Equal(InsideVerdict.WrongServer, (await wrong.TestInsideAsync()).Verdict);
    }

    [Fact]
    public async Task A_failing_public_name_from_inside_is_reported_but_does_not_turn_a_working_server_into_a_failure()
    {
        var (wizard, _, _, _) = Make(PortStatus.InUse, h => h == "myhome.example.com" ? ReachabilityOutcome.TimedOut : ReachabilityOutcome.Reachable);
        await wizard.LoadServerAsync(); wizard.DetectAddresses();
        Assert.True(wizard.SetPublicName("myhome.example.com").IsValid);
        var report = await wizard.TestInsideAsync();
        Assert.Equal(InsideVerdict.Works, report.Verdict);
        Assert.Equal(ReachabilityOutcome.TimedOut, report.PublicName!.Outcome);
    }

    [Fact]
    public async Task The_router_step_uses_the_chosen_address_the_port_and_the_gateway()
    {
        var (wizard, _, _, _) = Make(PortStatus.Free, _ => ReachabilityOutcome.Reachable);
        wizard.DetectAddresses();
        wizard.TrySetPort(9443);
        await Task.CompletedTask;
        var r = wizard.GetRouterInstructions()!;
        Assert.Equal("192.168.1.50", r.InternalAddress);
        Assert.Equal(9443, r.ExternalPort);
        Assert.Equal("192.168.1.1", r.RouterAddress);
    }

    [Fact]
    public async Task The_outside_test_needs_a_name_or_an_outside_address_and_then_uses_only_the_servers_own_check_page()
    {
        var (wizard, _, _, outside) = Make(PortStatus.Free, _ => ReachabilityOutcome.Reachable);
        wizard.DetectAddresses();
        Assert.Null(await wizard.StartOutsideTestAsync(null));
        var started = await wizard.StartOutsideTestAsync("203.0.113.9");
        Assert.Equal("https://203.0.113.9:8443/check/7QK2", started!.Value.Url);
        wizard.SetPublicName("myhome.example.com");
        Assert.Equal("https://myhome.example.com:8443/check/7QK2", (await wizard.StartOutsideTestAsync("203.0.113.9"))!.Value.Url);
        var (verdict, _) = await wizard.WaitForOutsideAsync(started.Value.Ticket, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));
        Assert.Equal(OutsideVerdict.ReachableFromOutside, verdict);
        Assert.True(outside.Polls >= 1);
    }

    [Fact]
    public async Task The_certificate_step_shows_what_the_server_presents()
    {
        var (wizard, _, _, _) = Make(PortStatus.InUse, _ => ReachabilityOutcome.Reachable);
        await wizard.LoadServerAsync();
        var report = await wizard.GetCertificateAsync();
        Assert.Equal("ab", report.PinHex);
        Assert.Equal(ExpiryState.Ok, report.Expiry);
        Assert.Equal("AA:BB", report.Presented!.FingerprintSha256);
    }

    [Fact]
    public async Task Applying_the_choices_writes_port_and_public_name_to_the_server_settings()
    {
        var (wizard, _, admin, _) = Make(PortStatus.Free, _ => ReachabilityOutcome.Reachable);
        wizard.TrySetPort(9443);
        wizard.SetPublicName("myhome.example.com");
        Assert.True(await wizard.ApplyToServerAsync());
        Assert.Equal(9443, admin.Settings.Port);
        Assert.Equal("myhome.example.com", admin.Settings.Address);
        Assert.Equal(100, admin.Settings.MaxDevices);
    }

    [Fact]
    public async Task Automatic_mapping_through_the_wizard_stays_off_until_the_user_switches_it_on()
    {
        var mapper = new FakeMapper("UPnP", true);
        var session = new PortMappingSession([mapper]);
        var services = new SetupWizardServices(new FakeAddresses(Addr("192.168.1.50", "Wi-Fi", true)), new FakePorts(PortStatus.Free), new FakeReach(_ => ReachabilityOutcome.Reachable),
            new FakeElevated(), new FakeOutside(), session, null, () => []);
        var wizard = new SetupWizard(services);
        wizard.DetectAddresses();
        Assert.Equal("Setup_Upnp_Off", (await wizard.CreateAutomaticMappingAsync()).ErrorKey);
        Assert.Empty(mapper.Created);
        session.AutomaticMappingEnabled = true;
        Assert.True((await wizard.CreateAutomaticMappingAsync()).Ok);
        Assert.Equal(8443, mapper.Created.Single().ExternalPort);
    }
}
