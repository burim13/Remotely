using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Remotely.Core.Admin;
using Remotely.Core.Protocol;
using Remotely.Core.ServerManager;

namespace Remotely.Tests;

/// <summary>The logic behind the Server Manager (src/Remotely.Core/ServerManager) and static checks of its WinUI sources, which cannot be built on Linux.</summary>
public partial class ManagerLogicTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Remotely.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remotely-manager-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- QR code ----

    [Theory]
    [InlineData(QrEcc.Low, 2953)]
    [InlineData(QrEcc.Medium, 2331)]
    [InlineData(QrEcc.Quartile, 1663)]
    [InlineData(QrEcc.High, 1273)]
    public void Qr_capacity_in_bytes_matches_the_standard_for_version_40(QrEcc ecc, int capacity)
    {
        var fits = QrCode.EncodeBytes(new byte[capacity], ecc);
        Assert.Equal(40, fits.Version);
        Assert.Throws<ArgumentException>(() => QrCode.EncodeBytes(new byte[capacity + 1], ecc));
    }

    [Fact]
    public void Qr_picks_the_smallest_version_and_raises_the_level_when_it_is_free()
    {
        Assert.Equal(1, QrCode.EncodeBytes(new byte[17], QrEcc.Low).Version);
        Assert.Equal(2, QrCode.EncodeBytes(new byte[18], QrEcc.Low).Version);
        var small = QrCode.EncodeText("hi", QrEcc.Low);
        Assert.Equal(1, small.Version);
        Assert.Equal(QrEcc.High, small.Ecc);
    }

    [Fact]
    public void Qr_block_tables_add_up_to_the_raw_codewords_for_every_version_and_level()
    {
        foreach (var ecc in Enum.GetValues<QrEcc>())
            for (var version = 1; version <= 40; version++)
            {
                var (eccPerBlock, blocks) = QrCode.BlockInfo(version, ecc);
                var raw = QrCode.RawCodewords(version);
                Assert.True(QrCode.DataCodewords(version, ecc) > 0, $"v{version} {ecc}");
                Assert.True(raw / blocks > eccPerBlock, $"v{version} {ecc}");
            }
        Assert.Equal(26, QrCode.RawCodewords(1));
        Assert.Equal(3706, QrCode.RawCodewords(40));
    }

    [Fact]
    public void Qr_of_a_real_invite_is_the_code_that_a_decoder_read_back()
    {
        // The expected hash is the matrix of this exact invite, which OpenCV's QR detector decoded back to the same text when the encoder was written.
        var invite = new Invite("wss://192.168.1.50:8443/ws", "e3411e89584cc29534e669f1f94b3fd7b40abe129b0df653ab771638bda41aad", "ABCD-2345").Encode();
        var qr = QrCode.EncodeText(invite);
        Assert.Equal((6, QrEcc.Medium, 41), (qr.Version, qr.Ecc, qr.Size));
        var text = qr.ToText().Replace("\r\n", "\n");
        Assert.Equal("ce9b5807f386d9492149cf946fb0c9fc4452d0dd35b8e98f6e537c680ad0ed35", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
    }

    [Fact]
    public void Qr_has_finder_patterns_timing_lines_and_the_dark_module()
    {
        var qr = QrCode.EncodeText("Remotely");
        var n = qr.Size;
        foreach (var (ox, oy) in new[] { (0, 0), (n - 7, 0), (0, n - 7) })
        {
            for (var i = 0; i < 7; i++)
            {
                Assert.True(qr.GetModule(ox + i, oy) && qr.GetModule(ox + i, oy + 6) && qr.GetModule(ox, oy + i) && qr.GetModule(ox + 6, oy + i));
            }
            Assert.False(qr.GetModule(ox + 1, oy + 1));
            Assert.True(qr.GetModule(ox + 3, oy + 3));
        }
        for (var i = 8; i < n - 8; i++)
        {
            Assert.Equal(i % 2 == 0, qr.GetModule(i, 6));
            Assert.Equal(i % 2 == 0, qr.GetModule(6, i));
        }
        Assert.True(qr.GetModule(8, n - 8));
    }

    // ---- fake server ----

    [Fact]
    public async Task Fake_server_creates_lists_and_revokes_invites_that_parse()
    {
        var admin = new FakeServerAdmin(new FixedClock(Now));
        var invite = await admin.CreateInviteAsync(TimeSpan.FromHours(1), 5);
        Assert.True(Invite.TryParse(invite.InviteText, out var parsed, out var error), error);
        Assert.Equal(FakeServerAdmin.FakePin, parsed!.PinHex);
        Assert.Equal(Now.AddHours(1), invite.ExpiresUtc);
        Assert.Contains(await admin.ListInvitesAsync(), i => i.Id == invite.Id);

        await admin.RevokeInviteAsync(invite.Id);
        var after = (await admin.ListInvitesAsync()).Single(i => i.Id == invite.Id);
        Assert.Equal(InviteStatus.UsedUp, InviteRules.StatusOf(after, Now));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => admin.CreateInviteAsync(TimeSpan.Zero, 1));
    }

    [Fact]
    public async Task Fake_server_renames_blocks_and_revokes_devices_and_records_it()
    {
        var admin = new FakeServerAdmin(new FixedClock(Now));
        var devices = await admin.ListDevicesAsync();
        var online = devices.First(d => d.Online);
        await admin.RenameDeviceAsync(online.DeviceId, "  Study PC  ");
        await admin.SetDeviceBlockedAsync(online.DeviceId, true);
        var changed = (await admin.ListDevicesAsync()).Single(d => d.DeviceId == online.DeviceId);
        Assert.Equal("Study PC", changed.Name);
        Assert.True(changed.Blocked);
        Assert.False(changed.Online);

        await admin.RevokeDeviceAsync(online.DeviceId);
        Assert.DoesNotContain(await admin.ListDevicesAsync(), d => d.DeviceId == online.DeviceId);
        var audit = await admin.GetAuditAsync();
        Assert.Contains(audit, a => a.Kind == "revoke" && a.DeviceId == online.DeviceId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => admin.RevokeDeviceAsync(online.DeviceId));
        await Assert.ThrowsAsync<ArgumentException>(() => admin.RenameDeviceAsync(devices[1].DeviceId, "   "));
    }

    [Fact]
    public async Task Fake_server_offline_fails_every_call_with_a_clear_exception()
    {
        var admin = new FakeServerAdmin { Offline = true };
        await Assert.ThrowsAsync<ServerUnavailableException>(() => admin.GetStatusAsync());
        await Assert.ThrowsAsync<ServerUnavailableException>(() => admin.ListDevicesAsync());
        var none = new UnavailableServerAdmin("no client");
        var ex = await Assert.ThrowsAsync<ServerUnavailableException>(() => none.GetSettingsAsync());
        Assert.Equal("no client", ex.Message);
    }

    [Fact]
    public async Task Fake_backup_writes_into_the_folder_it_is_given()
    {
        var dir = TempDir();
        try
        {
            var path = await new FakeServerAdmin(new FixedClock(Now)).CreateBackupAsync(Path.Combine(dir, "sub"));
            Assert.True(File.Exists(path));
            Assert.StartsWith(Path.Combine(dir, "sub"), path);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Self_test_passes_against_the_fake_and_fails_against_an_unavailable_server()
    {
        var fake = new FakeServerAdmin(new FixedClock(Now));
        var target = (await fake.ListDevicesAsync())[2].DeviceId;
        var log = new List<string>();
        Assert.True(await ManagerSelfTest.RunAsync(fake, target, log.Add));
        Assert.Equal("PASS", log[^1]);
        Assert.DoesNotContain(await fake.ListDevicesAsync(), d => d.DeviceId == target);

        var failLog = new List<string>();
        Assert.False(await ManagerSelfTest.RunAsync(new UnavailableServerAdmin("down"), null, failLog.Add));
        Assert.Equal("FAIL", failLog[^1]);
        Assert.Contains(failLog, l => l.StartsWith("FAIL status:", StringComparison.Ordinal));

        var missing = new List<string>();
        Assert.False(await ManagerSelfTest.RunAsync(new FakeServerAdmin(new FixedClock(Now)), "not-a-device", missing.Add));
        Assert.Contains(missing, l => l.StartsWith("FAIL revoke device:", StringComparison.Ordinal));
    }

    // ---- lists, filters, CSV ----

    [Fact]
    public async Task Device_search_and_state_filters_work_and_online_devices_come_first()
    {
        var devices = await new FakeServerAdmin(new FixedClock(Now)).ListDevicesAsync();
        Assert.Single(DeviceView.Filter(devices, "LIVING"));
        Assert.Single(DeviceView.Filter(devices, devices[0].DeviceId[..6]));
        Assert.Empty(DeviceView.Filter(devices, "zzz"));
        Assert.All(DeviceView.Filter(devices, null, DeviceStateFilter.Blocked), d => Assert.True(d.Blocked));
        Assert.All(DeviceView.Filter(devices, "", DeviceStateFilter.Online), d => Assert.True(d.Online && !d.Blocked));
        var all = DeviceView.Filter(devices, null);
        Assert.Equal(devices.Count, all.Count);
        var firstOffline = all.ToList().FindIndex(d => !d.Online);
        Assert.All(all.Skip(firstOffline), d => Assert.False(d.Online));
    }

    [Theory]
    [InlineData(-5, LastSeenKind.JustNow, 0)]
    [InlineData(-90, LastSeenKind.Minutes, 1)]
    [InlineData(-3 * 3600, LastSeenKind.Hours, 3)]
    [InlineData(-2 * 86400, LastSeenKind.Days, 2)]
    [InlineData(-40 * 86400, LastSeenKind.Date, 0)]
    public void Last_seen_is_described_by_age(int seconds, LastSeenKind kind, int count)
    {
        var device = new AdminDevice("id", "n", true, false, false, Now.AddDays(-100), Now.AddSeconds(seconds));
        var info = DeviceView.DescribeLastSeen(device, Now);
        Assert.Equal((kind, count), (info.Kind, info.Count));
        Assert.Equal(LastSeenKind.Online, DeviceView.DescribeLastSeen(device with { Online = true }, Now).Kind);
        Assert.Equal(LastSeenKind.Never, DeviceView.DescribeLastSeen(device with { LastSeenUtc = null }, Now).Kind);
    }

    [Fact]
    public async Task Audit_filters_combine_and_a_search_by_device_name_finds_the_entry()
    {
        var admin = new FakeServerAdmin(new FixedClock(Now));
        var devices = await admin.ListDevicesAsync();
        var names = devices.ToDictionary(d => d.DeviceId, d => d.Name);
        var entries = await admin.GetAuditAsync();
        var refused = AuditView.Apply(entries, new AuditFilter(Result: "REFUSED"), names);
        Assert.NotEmpty(refused);
        Assert.All(refused, e => Assert.Equal("refused", e.Result));
        Assert.Equal(refused.OrderByDescending(e => e.TimeUtc), refused);

        var byName = AuditView.Apply(entries, new AuditFilter(Text: "mum's"), names);
        Assert.NotEmpty(byName);
        Assert.All(byName, e => Assert.Equal("Mum's desktop", names[e.DeviceId]));

        Assert.All(AuditView.Apply(entries, new AuditFilter(FromUtc: Now.AddHours(-6))), e => Assert.True(e.TimeUtc >= Now.AddHours(-6)));
        var device = devices[0].DeviceId;
        Assert.All(AuditView.Apply(entries, new AuditFilter(DeviceId: device)), e => Assert.True(e.DeviceId == device || e.PeerDeviceId == device));
        Assert.Contains("connect", AuditView.DistinctKinds(entries));
        Assert.Contains("refused", AuditView.DistinctResults(entries));
    }

    [Fact]
    public void Csv_quotes_special_characters_and_neutralises_spreadsheet_formulas()
    {
        Assert.Equal("", AuditCsv.Cell(null));
        Assert.Equal("plain", AuditCsv.Cell("plain"));
        Assert.Equal("\"a,b\"", AuditCsv.Cell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", AuditCsv.Cell("say \"hi\""));
        Assert.Equal("\"two\r\nlines\"", AuditCsv.Cell("two\r\nlines"));
        Assert.Equal("\"'=HYPERLINK(\"\"x\"\")\"", AuditCsv.Cell("=HYPERLINK(\"x\")"));
        Assert.Equal("'+1", AuditCsv.Cell("+1"));
        Assert.Equal("'-1", AuditCsv.Cell("-1"));
        Assert.Equal("'@sum", AuditCsv.Cell("@sum"));

        var entries = new[] { new AuditEntry(Now, "connect", "aa", "bb", "ok", "he said \"go\", then left") };
        var csv = AuditCsv.Write(entries, new Dictionary<string, string> { ["aa"] = "Laptop", ["bb"] = "PC, big" });
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(AuditCsv.Header, lines[0]);
        Assert.Equal("2026-10-16T12:00:00Z,connect,aa,Laptop,bb,\"PC, big\",ok,\"he said \"\"go\"\", then left\"", lines[1]);
    }

    // ---- invites, settings, certificate ----

    [Fact]
    public void Invite_rules_limit_lifetime_and_uses_and_classify_status()
    {
        Assert.Null(InviteRules.Validate(TimeSpan.FromHours(1), 10));
        Assert.Equal("Invite_BadLifetime", InviteRules.Validate(TimeSpan.FromSeconds(30), 1));
        Assert.Equal("Invite_BadLifetime", InviteRules.Validate(TimeSpan.FromDays(31), 1));
        Assert.Equal("Invite_BadUses", InviteRules.Validate(TimeSpan.FromHours(1), 0));
        Assert.Equal("Invite_BadUses", InviteRules.Validate(TimeSpan.FromHours(1), 101));
        Assert.All(InviteRules.LifetimeChoices, l => Assert.Null(InviteRules.Validate(l, 1)));
        Assert.All(InviteRules.UseChoices, u => Assert.Null(InviteRules.Validate(TimeSpan.FromHours(1), u)));

        var invite = new InviteInfo("i", "RMT1-x", Now.AddMinutes(5), 3, 2);
        Assert.Equal(InviteStatus.Active, InviteRules.StatusOf(invite, Now));
        Assert.Equal(InviteStatus.Expired, InviteRules.StatusOf(invite, Now.AddMinutes(5)));
        Assert.Equal(InviteStatus.UsedUp, InviteRules.StatusOf(invite with { UsesLeft = 0 }, Now));
    }

    [Fact]
    public void Server_settings_are_validated_before_they_are_sent()
    {
        var good = new ServerSettings("home.example.com", 8443, 25, 2, true);
        Assert.Empty(SettingsValidator.Validate(good));
        Assert.Empty(SettingsValidator.Validate(good with { Address = "192.168.1.5" }));
        Assert.Equal("Settings_BadAddress", Assert.Single(SettingsValidator.Validate(good with { Address = "https://x" })).MessageKey);
        Assert.Single(SettingsValidator.Validate(good with { Address = "" }));
        Assert.Single(SettingsValidator.Validate(good with { Address = "a b" }));
        Assert.Single(SettingsValidator.Validate(good with { Address = "host/path" }));
        Assert.Equal("Port", Assert.Single(SettingsValidator.Validate(good with { Port = 0 })).Field);
        Assert.Single(SettingsValidator.Validate(good with { Port = 65536 }));
        Assert.Equal("Settings_BadMaxDevices", Assert.Single(SettingsValidator.Validate(good with { MaxDevices = 0 })).MessageKey);
        Assert.Equal("Settings_BadMaxSessions", Assert.Single(SettingsValidator.Validate(good with { MaxSessionsPerDevice = 0 })).MessageKey);
        Assert.Equal(4, SettingsValidator.Validate(new ServerSettings("", 0, 0, 0, true)).Count);
    }

    [Fact]
    public void Certificate_health_and_support_text()
    {
        Assert.Equal(CertificateHealth.Expired, ServerInfo.AssessCertificate(Now, Now));
        Assert.Equal(CertificateHealth.ExpiringSoon, ServerInfo.AssessCertificate(Now.AddDays(30), Now));
        Assert.Equal(CertificateHealth.Ok, ServerInfo.AssessCertificate(Now.AddDays(31), Now));

        var status = new ServerStatus("1.0.0", Now.AddHours(-2), "home.example.com", 8443, true, FakeServerAdmin.FakePin, Now.AddDays(200), 4, 2, 1);
        var text = ServerInfo.SupportText(status, ServiceState.Running, "1.0.0", "Windows 11");
        Assert.Contains("home.example.com:8443", text);
        Assert.Contains(FakeServerAdmin.FakePin, text);
        Assert.Contains("4 paired, 2 online, 1 active sessions", text);
        Assert.DoesNotContain("RMT1-", text);
        Assert.Contains("not reachable", ServerInfo.SupportText(null, ServiceState.Stopped, "1.0.0", "Windows 11"));
    }

    [Fact]
    public async Task Reachability_tells_listening_here_from_answering_at_the_address()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probe = new TcpProbe();
        Assert.Equal(ReachabilityVerdict.AnswersAtAddress, await Reachability.CheckAsync(probe, "127.0.0.1", port));
        Assert.Equal(ReachabilityVerdict.ThisPcOnly, await Reachability.CheckAsync(probe, "", port));
        listener.Stop();
        Assert.Equal(ReachabilityVerdict.NotListening, await Reachability.CheckAsync(probe, "127.0.0.1", port));
    }

    private sealed class ScriptedProbe(Func<string, bool> answers) : ITcpProbe
    {
        public Task<bool> CanConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(answers(host));
    }

    [Fact]
    public async Task Reachability_reports_this_pc_only_when_the_public_address_does_not_answer()
    {
        var probe = new ScriptedProbe(host => host == "127.0.0.1");
        Assert.Equal(ReachabilityVerdict.ThisPcOnly, await Reachability.CheckAsync(probe, "home.example.com", 8443));
        Assert.Equal(ReachabilityVerdict.NotListening, await Reachability.CheckAsync(new ScriptedProbe(_ => false), "home.example.com", 8443));
    }

    // ---- service control and the elevated helper ----

    private sealed class ScriptedRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public Queue<(int, string)> QueryStates { get; } = new();
        public Dictionary<string, int> ExitCodes { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
        {
            var line = Path.GetFileName(file) + " " + string.Join(' ', args);
            Calls.Add(line);
            if (file == "sc.exe" && args[0] == "query")
                return Task.FromResult(QueryStates.Count > 0 ? QueryStates.Dequeue() : (0, "STATE : 4  RUNNING"));
            return Task.FromResult((ExitCodes.GetValueOrDefault(line, 0), "output of " + line));
        }
    }

    private static (int, string) State(int n) => (0, $"SERVICE_NAME: Remotely.Server\r\n        STATE              : {n}  X");

    [Theory]
    [InlineData(0, "STATE              : 4  RUNNING", ServiceState.Running)]
    [InlineData(0, "STATE              : 1  STOPPED", ServiceState.Stopped)]
    [InlineData(0, "STATE              : 2  START_PENDING", ServiceState.StartPending)]
    [InlineData(0, "STATE              : 3  STOP_PENDING", ServiceState.StopPending)]
    [InlineData(0, "STATE              : 7  PAUSED", ServiceState.Paused)]
    [InlineData(0, "STATE              : 4  LÄUFT", ServiceState.Running)]
    [InlineData(1060, "The specified service does not exist as an installed service.", ServiceState.NotInstalled)]
    [InlineData(5, "Access is denied.", ServiceState.Unknown)]
    [InlineData(0, "", ServiceState.Unknown)]
    public void Sc_output_is_read_by_state_number_in_any_language(int code, string output, ServiceState expected) =>
        Assert.Equal(expected, ScOutput.Parse(code, output));

    [Fact]
    public async Task Service_query_returns_unknown_when_sc_cannot_be_started()
    {
        var query = new ServiceQuery(new ThrowingRunner());
        Assert.Equal(ServiceState.Unknown, await query.QueryAsync());
        var runner = new ScriptedRunner();
        runner.QueryStates.Enqueue(State(1));
        Assert.Equal(ServiceState.Stopped, await new ServiceQuery(runner).QueryAsync());
        Assert.Equal(["sc.exe query Remotely.Server"], runner.Calls);
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default) =>
            throw new System.ComponentModel.Win32Exception("not found");
    }

    private static ServiceHelper Helper(ScriptedRunner runner, string? exe, List<string>? log = null, Func<string, int>? restore = null) =>
        new(runner, exe, (log ?? []).Add, restore, (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Restart_stops_waits_for_stopped_starts_and_waits_for_running()
    {
        var runner = new ScriptedRunner();
        foreach (var n in new[] { 4, 3, 1, 2, 4 }) runner.QueryStates.Enqueue(State(n));
        Assert.Equal(HelperExit.Ok, await Helper(runner, null).RunAsync(ServiceVerb.Restart, null));
        Assert.Equal(["sc.exe stop Remotely.Server", "sc.exe query Remotely.Server", "sc.exe query Remotely.Server", "sc.exe query Remotely.Server",
            "sc.exe start Remotely.Server", "sc.exe query Remotely.Server", "sc.exe query Remotely.Server"], runner.Calls);
    }

    [Fact]
    public async Task Stop_and_start_accept_already_stopped_and_already_running()
    {
        var runner = new ScriptedRunner();
        runner.ExitCodes["sc.exe stop Remotely.Server"] = ScOutput.ServiceNotActive;
        runner.QueryStates.Enqueue(State(1));
        Assert.Equal(HelperExit.Ok, await Helper(runner, null).RunAsync(ServiceVerb.Stop, null));

        runner = new ScriptedRunner();
        runner.ExitCodes["sc.exe start Remotely.Server"] = ScOutput.ServiceAlreadyRunning;
        runner.QueryStates.Enqueue(State(4));
        Assert.Equal(HelperExit.Ok, await Helper(runner, null).RunAsync(ServiceVerb.Start, null));

        runner = new ScriptedRunner();
        runner.ExitCodes["sc.exe start Remotely.Server"] = 5;
        Assert.Equal(HelperExit.StepFailed, await Helper(runner, null).RunAsync(ServiceVerb.Start, null));
    }

    [Fact]
    public async Task Start_gives_up_when_the_service_never_reaches_running()
    {
        var runner = new ScriptedRunner();
        for (var i = 0; i < 100; i++) runner.QueryStates.Enqueue(State(2));
        var log = new List<string>();
        Assert.Equal(HelperExit.StepFailed, await Helper(runner, null, log).RunAsync(ServiceVerb.Start, null));
        Assert.Contains(log, l => l.StartsWith("Timed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_and_uninstall_run_only_the_servers_own_command()
    {
        var runner = new ScriptedRunner();
        Assert.Equal(HelperExit.Ok, await Helper(runner, "srv.exe").RunAsync(ServiceVerb.Install, null));
        Assert.Equal(HelperExit.Ok, await Helper(runner, "srv.exe").RunAsync(ServiceVerb.Uninstall, null));
        Assert.Equal(["srv.exe install", "srv.exe uninstall"], runner.Calls);
        Assert.Equal(HelperExit.ServerExeMissing, await Helper(new ScriptedRunner(), null).RunAsync(ServiceVerb.Install, null));

        var failing = new ScriptedRunner();
        failing.ExitCodes["x.exe install"] = 1;
        Assert.Equal(HelperExit.StepFailed, await Helper(failing, "x.exe").RunAsync(ServiceVerb.Install, null));
    }

    [Fact]
    public async Task Restore_stops_the_service_restores_and_starts_it_again_even_when_the_restore_fails()
    {
        var runner = new ScriptedRunner();
        runner.QueryStates.Enqueue(State(1));
        var restored = new List<string>();
        var code = await Helper(runner, null, restore: p => { restored.Add(p); return 3; }).RunAsync(ServiceVerb.Restore, @"D:\backup.zip");
        Assert.Equal(HelperExit.Ok, code);
        Assert.Equal([@"D:\backup.zip"], restored);
        Assert.Equal("sc.exe stop Remotely.Server", runner.Calls[0]);
        Assert.Contains("sc.exe start Remotely.Server", runner.Calls);

        runner = new ScriptedRunner();
        runner.QueryStates.Enqueue(State(1));
        var log = new List<string>();
        Assert.Equal(HelperExit.StepFailed, await Helper(runner, null, log, _ => throw new InvalidDataException("bad zip")).RunAsync(ServiceVerb.Restore, "x.zip"));
        Assert.Contains(log, l => l.Contains("bad zip", StringComparison.Ordinal));
        Assert.Contains("sc.exe start Remotely.Server", runner.Calls);

        Assert.Equal(HelperExit.BadUsage, await Helper(new ScriptedRunner(), null, restore: _ => 0).RunAsync(ServiceVerb.Restore, null));
    }

    [Fact]
    public void Helper_command_line_round_trips_and_rejects_unknown_verbs()
    {
        Assert.Null(HelperCommandLine.Parse(["Remotely.ServerManager.exe", "--fake"]));
        var plain = HelperCommandLine.Parse(["x.exe", "--service-helper", "restart"]);
        Assert.Equal(new HelperRequest(ServiceVerb.Restart, null, true), plain);
        var restore = HelperCommandLine.Parse(["x.exe", "--service-helper", "RESTORE", @"D:\My Backups\b.zip"]);
        Assert.Equal(new HelperRequest(ServiceVerb.Restore, @"D:\My Backups\b.zip", true), restore);
        Assert.False(HelperCommandLine.Parse(["x.exe", "--service-helper", "format-disk"])!.IsValid);
        Assert.False(HelperCommandLine.Parse(["x.exe", "--service-helper"])!.IsValid);
        Assert.False(HelperCommandLine.Parse(["x.exe", "--service-helper", "99"])!.IsValid);

        Assert.Equal("--service-helper start", HelperCommandLine.Build(ServiceVerb.Start));
        Assert.Equal("--service-helper restore \"D:\\b.zip\"", HelperCommandLine.Build(ServiceVerb.Restore, "D:\\b\"\".zip"));
    }

    [Fact]
    public void Server_exe_is_looked_up_in_fixed_places_only_and_install_prefers_the_fresh_copy()
    {
        var install = ServerExeLocator.Candidates(ServiceVerb.Install, "M", "PF");
        Assert.Equal(Path.Combine("M", "Remotely.Server.exe"), install[0]);
        Assert.Equal(Path.Combine("PF", "Remotely Server", "Remotely.Server.exe"), install[^1]);
        var uninstall = ServerExeLocator.Candidates(ServiceVerb.Uninstall, "M", "PF");
        Assert.Equal(Path.Combine("PF", "Remotely Server", "Remotely.Server.exe"), uninstall[0]);
        Assert.Equal(3, uninstall.Count);
        Assert.Equal(install[1], ServerExeLocator.Find(install, p => p == install[1] || p == install[2]));
        Assert.Null(ServerExeLocator.Find(install, _ => false));
    }

    // ---- backup restore ----

    [Fact]
    public void Restore_from_a_zip_replaces_files_and_keeps_the_old_ones_aside()
    {
        var dir = TempDir();
        try
        {
            var data = Path.Combine(dir, "data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "devices.json"), "old");
            File.WriteAllText(Path.Combine(data, "keep.txt"), "untouched");
            var zipPath = Path.Combine(dir, "backup.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("devices.json");
                using (var w = new StreamWriter(entry.Open())) w.Write("new");
                var nested = zip.CreateEntry("sub/more.txt");
                using (var w = new StreamWriter(nested.Open())) w.Write("nested");
            }
            Assert.Equal(2, BackupRestorer.Restore(zipPath, data));
            Assert.Equal("new", File.ReadAllText(Path.Combine(data, "devices.json")));
            Assert.Equal("nested", File.ReadAllText(Path.Combine(data, "sub", "more.txt")));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(data, "keep.txt")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(data, BackupRestorer.SafetyFolder, "devices.json")));
            Assert.DoesNotContain(Directory.GetDirectories(dir), d => Path.GetFileName(d).StartsWith(".restore-", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Restore_from_a_folder_works_and_a_second_restore_does_not_nest_the_safety_copy()
    {
        var dir = TempDir();
        try
        {
            var data = Path.Combine(dir, "data");
            var backup = Path.Combine(dir, "backup");
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "a.json"), "1");
            Assert.Equal(1, BackupRestorer.Restore(backup, data));
            File.WriteAllText(Path.Combine(backup, "a.json"), "2");
            Assert.Equal(1, BackupRestorer.Restore(backup, data));
            Assert.Equal("2", File.ReadAllText(Path.Combine(data, "a.json")));
            Assert.Equal("1", File.ReadAllText(Path.Combine(data, BackupRestorer.SafetyFolder, "a.json")));
            Assert.False(Directory.Exists(Path.Combine(data, BackupRestorer.SafetyFolder, BackupRestorer.SafetyFolder)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Restore_refuses_a_zip_that_writes_outside_the_data_folder_and_empty_or_missing_backups()
    {
        var dir = TempDir();
        try
        {
            var data = Path.Combine(dir, "data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "devices.json"), "precious");
            var evil = Path.Combine(dir, "evil.zip");
            using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
            {
                using var w = new StreamWriter(zip.CreateEntry("../outside.txt").Open());
                w.Write("pwned");
            }
            Assert.Throws<InvalidDataException>(() => BackupRestorer.Restore(evil, data));
            Assert.False(File.Exists(Path.Combine(dir, "outside.txt")));
            Assert.Equal("precious", File.ReadAllText(Path.Combine(data, "devices.json")));

            var empty = Path.Combine(dir, "empty");
            Directory.CreateDirectory(empty);
            Assert.Throws<InvalidDataException>(() => BackupRestorer.Restore(empty, data));
            Assert.Throws<FileNotFoundException>(() => BackupRestorer.Restore(Path.Combine(dir, "nope.zip"), data));
            Assert.Throws<FileNotFoundException>(() => BackupRestorer.Restore(Path.Combine(data, "devices.json"), data));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- monitor, tray, settings, arguments ----

    private static async Task<(FakeServerAdmin Admin, ServerMonitor Monitor)> StartedMonitorAsync()
    {
        var admin = new FakeServerAdmin(new FixedClock(Now));
        var monitor = new ServerMonitor(admin, new ServiceQuery(new FakeProcessRunner()), new FixedClock(Now));
        await monitor.RefreshAsync();
        return (admin, monitor);
    }

    [Fact]
    public async Task Monitor_does_not_announce_existing_devices_but_announces_a_new_one_once()
    {
        var (admin, monitor) = await StartedMonitorAsync();
        var announced = new List<AdminDevice>();
        monitor.NewDevices += announced.AddRange;
        await monitor.RefreshAsync();
        Assert.Empty(announced);

        admin.SimulatePairing("Neighbour's laptop");
        await monitor.RefreshAsync();
        Assert.Equal("Neighbour's laptop", Assert.Single(announced).Name);
        await monitor.RefreshAsync();
        Assert.Single(announced);
    }

    [Fact]
    public async Task Monitor_survives_an_outage_keeps_its_baseline_and_reports_the_error()
    {
        var (admin, monitor) = await StartedMonitorAsync();
        var announced = new List<AdminDevice>();
        monitor.NewDevices += announced.AddRange;
        admin.Offline = true;
        var down = await monitor.RefreshAsync();
        Assert.False(down.Reachable);
        Assert.NotEmpty(down.Error!);
        Assert.NotEmpty(down.Devices);
        Assert.Equal(ServerHeadline.NotAnswering, Headline.Of(down));
        admin.Offline = false;
        await monitor.RefreshAsync();
        Assert.Empty(announced);
    }

    [Fact]
    public async Task Tray_status_and_headline_follow_the_server_and_service_state()
    {
        var (admin, monitor) = await StartedMonitorAsync();
        Assert.Equal(TrayStatus.Running, ServerMonitor.TrayStatusOf(monitor.Latest, Now));
        Assert.Equal(ServerHeadline.Running, Headline.Of(monitor.Latest));
        Assert.Equal(TrayStatus.Attention, ServerMonitor.TrayStatusOf(monitor.Latest, Now.AddDays(400)));
        Assert.Equal(TrayStatus.Unknown, ServerMonitor.TrayStatusOf(null, Now));

        admin.Offline = true;
        var stopped = new ServerMonitor(admin, new ServiceQuery(new CannedRunner(1060, "x")), new FixedClock(Now));
        var snap = await stopped.RefreshAsync();
        Assert.Equal(TrayStatus.Stopped, ServerMonitor.TrayStatusOf(snap, Now));
        Assert.Equal(ServerHeadline.NotInstalled, Headline.Of(snap));
        var off = new ServerMonitor(admin, new ServiceQuery(new CannedRunner(0, "STATE : 1  STOPPED")), new FixedClock(Now));
        Assert.Equal(ServerHeadline.Stopped, Headline.Of(await off.RefreshAsync()));
        var starting = new ServerMonitor(admin, new ServiceQuery(new CannedRunner(0, "STATE : 2  START_PENDING")), new FixedClock(Now));
        Assert.Equal(TrayStatus.Starting, ServerMonitor.TrayStatusOf(await starting.RefreshAsync(), Now));
    }

    private sealed class CannedRunner(int code, string output) : IProcessRunner
    {
        public Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default) => Task.FromResult((code, output));
    }

    [Theory]
    [InlineData(TrayStatus.Running, 0x6F, 0xCF, 0x97)]
    [InlineData(TrayStatus.Stopped, 0xFF, 0x9B, 0x8A)]
    [InlineData(TrayStatus.Attention, 0xF7, 0xBE, 0x55)]
    public void Tray_icon_is_a_round_disc_with_the_status_colour_in_the_middle(TrayStatus status, int r, int g, int b)
    {
        const int size = 32;
        var pixels = TrayIconRenderer.RenderBgra(size, status);
        Assert.Equal(size * size * 4, pixels.Length);
        int At(int x, int y, int channel) => pixels[(y * size + x) * 4 + channel];
        Assert.Equal(0, At(0, 0, 3));                                         // corner is transparent
        Assert.Equal(255, At(size / 2, size / 2, 3));                         // middle is opaque
        Assert.Equal((b, g, r), (At(size / 2, size / 2, 0), At(size / 2, size / 2, 1), At(size / 2, size / 2, 2)));
        Assert.Equal(0x1C, At(size / 2, 1, 2));                               // the rim is the dark panel colour
        Assert.NotEqual(TrayIconRenderer.ColourOf(TrayStatus.Running), TrayIconRenderer.ColourOf(TrayStatus.Stopped));
    }

    [Fact]
    public void Manager_settings_round_trip_and_a_damaged_file_gives_defaults()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "sub", "manager.json");
            Assert.True(ManagerSettings.Load(path).CloseToTray);
            new ManagerSettings { StartWithWindows = true, CloseToTray = false, Theme = "dark", NotifyNewDevices = false }.Save(path);
            var back = ManagerSettings.Load(path);
            Assert.True(back.StartWithWindows);
            Assert.False(back.CloseToTray);
            Assert.False(back.NotifyNewDevices);
            Assert.Equal("dark", back.Theme);
            Assert.False(File.Exists(path + ".tmp"));

            File.WriteAllText(path, "{ this is not json");
            Assert.Equal("system", ManagerSettings.Load(path).Theme);
            File.WriteAllText(path, "{\"Theme\":\"neon\"}");
            Assert.Equal("system", ManagerSettings.Load(path).Theme);
            Assert.Equal("\"C:\\Program Files\\Remotely Server Manager\\Remotely.ServerManager.exe\" --minimized",
                ManagerSettings.AutoStartCommand(@"C:\Program Files\Remotely Server Manager\Remotely.ServerManager.exe"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Command_line_options_are_parsed()
    {
        var a = ManagerArgs.Parse(["x.exe", "--fake", "--show", "DevicesPage", "--minimized", "--selftest", "r.txt", "--revoke-device", "abc"]);
        Assert.Equal(new ManagerArgs(true, "DevicesPage", true, "r.txt", "abc"), a);
        Assert.Equal(new ManagerArgs(false, null, false, null, null), ManagerArgs.Parse(["x.exe"]));
        Assert.Null(ManagerArgs.Parse(["x.exe", "--show"]).Show);
    }

    // ---- static checks of the WinUI sources (they cannot be compiled on this machine) ----

    private static IEnumerable<string> ManagerSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Remotely.ServerManager"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "Setup" + Path.DirectorySeparatorChar));

    private static Dictionary<string, string> Resw(string language)
    {
        var path = Path.Combine(RepoRoot(), "src", "Remotely.ServerManager", "Strings", language, "Resources.resw");
        return XDocument.Load(path).Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);
    }

    [GeneratedRegex("""(?:Ui\.T|Loc\.Get|(?<![A-Za-z.])T)\(\s*"([A-Za-z0-9_]+)"\s*[,)]""")]
    private static partial Regex KeyUse();

    [Fact]
    public void Every_string_key_used_by_the_Manager_exists_in_English_and_Albanian_with_the_same_placeholders()
    {
        var en = Resw("en-US");
        var sq = Resw("sq-AL");
        Assert.Equal(en.Keys.OrderBy(k => k, StringComparer.Ordinal), sq.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var used = ManagerSources().SelectMany(f => KeyUse().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet();
        foreach (var coreFile in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Remotely.Core", "ServerManager"), "*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(coreFile), "\"((?:Invite|Settings)_[A-Za-z0-9]+)\"")) used.Add(m.Groups[1].Value);
        foreach (var state in Enum.GetNames<ServiceState>()) used.Add("Service_" + state);
        used.Remove("Service_");   // the prefix of the keys built from ServiceState names, added above

        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k), "missing string key " + k));
        foreach (var (key, value) in en)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), key);
            Assert.False(string.IsNullOrWhiteSpace(sq[key]), key);
            Assert.Equal(Placeholders(value), Placeholders(sq[key]));
        }
        Assert.All(en.Keys, k => Assert.True(used.Contains(k) || k.StartsWith("Kind_", StringComparison.Ordinal) || k.StartsWith("Result_", StringComparison.Ordinal)
            || k.StartsWith("Setup_", StringComparison.Ordinal),   // the setup wizard (Setup/) has its own checks in NetworkSetupTests
            "unused string key " + k));
    }

    private static string Placeholders(string text) =>
        string.Join(",", Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(v => v, StringComparer.Ordinal));

    [Fact]
    public void Xaml_files_are_well_formed_and_every_handler_and_class_they_name_exists()
    {
        var root = Path.Combine(RepoRoot(), "src", "Remotely.ServerManager");
        var xamls = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "Setup" + Path.DirectorySeparatorChar)).ToList();
        Assert.True(xamls.Count >= 8);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var file in xamls)
        {
            var doc = XDocument.Load(file);
            var cls = (string?)doc.Root!.Attribute(x + "Class");
            var code = file + ".cs";
            if (cls is null) continue;   // a resource dictionary
            Assert.True(File.Exists(code), file + " has no code-behind");
            var source = File.ReadAllText(code);
            Assert.Contains("partial class " + cls[(cls.LastIndexOf('.') + 1)..], source);
            Assert.Contains("namespace " + cls[..cls.LastIndexOf('.')], source);
            foreach (var attribute in doc.Descendants().SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName is "Click" or "SelectionChanged" or "Toggled" or "TextChanged" or "KeyDown"))
                Assert.Matches(@"\b(void|Task)\s+" + Regex.Escape(attribute.Value) + @"\s*\(", source);
            var names = doc.Descendants().Select(e => (string?)e.Attribute(x + "Name")).Where(n => n is not null).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
        }
    }

    [Fact]
    public void Every_Manager_page_is_built_by_the_start_up_check_and_listed_for_the_screenshot_run()
    {
        var managerRoot = Path.Combine(RepoRoot(), "src", "Remotely.ServerManager");
        var pages = new[] { "Views", "Setup" }.SelectMany(dir => Directory.EnumerateFiles(Path.Combine(managerRoot, dir), "*Page.xaml"))
            .Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(pages);
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Remotely.ServerManager", "App.xaml.cs"));
        var listed = File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "ci", "manager-pages.txt")).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(pages, listed);
        foreach (var page in pages) Assert.Contains("new " + page + "()", app);
    }

    [Fact]
    public void Manager_code_never_runs_anything_elevated_except_through_the_helper_and_hides_nothing_from_Ui()
    {
        foreach (var file in ManagerSources())
        {
            var text = File.ReadAllText(file);
            if (Path.GetFileName(file) == "Elevation.cs") continue;
            Assert.DoesNotContain("runas", text);
            Assert.DoesNotContain("\"sc.exe\"", text);
        }
        var manifest = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Remotely.ServerManager", "app.manifest"));
        Assert.Contains("asInvoker", manifest);
        Assert.DoesNotContain("requireAdministrator", manifest);
    }
}
