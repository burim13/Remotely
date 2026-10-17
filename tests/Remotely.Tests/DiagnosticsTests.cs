using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Xml.Linq;
using Remotely.Client;
using Remotely.Core.Admin;
using Remotely.Core.Crypto;
using Remotely.Core.Diagnostics;
using Remotely.Core.Lan;
using Remotely.Core.Lan.Consent;
using Remotely.Core.NetworkSetup;
using Remotely.Core.Protocol;
using Remotely.Core.ServerManager;
using Remotely.HostCore;

namespace Remotely.Tests;

public class DiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remotely-diag-" + Guid.NewGuid().ToString("N"));

    public DiagnosticsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    // ---- the canary secrets: values of the shapes the program really has ----

    private sealed record Canaries(string Token, string Invite, string Pin, string AdminToken, string PairingCodeInSentence, string BareRegistered, string PrivateKey, string Bearer)
    {
        public static Canaries Make() => new(
            LanToken.Create().Display,
            new Invite("wss://example.com/ws", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), "QWRT2345").Encode(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            "ZXCV7654",
            "ZorroSecret" + Random.Shared.Next(100000, 999999),
            Convert.ToBase64String(DeviceIdentity.Generate().ExportPkcs8()),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('='));

        /// <summary>Every value that must never appear, in every spelling a log line or a person could produce.</summary>
        public IEnumerable<string> Needles()
        {
            var plain = new[] { Token, Invite, Pin, AdminToken, PairingCodeInSentence, BareRegistered, PrivateKey, Bearer };
            foreach (var p in plain)
            {
                yield return p;
                yield return p.ToLowerInvariant();
                yield return p.ToUpperInvariant();
                yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(p));
                yield return Convert.ToHexString(Encoding.UTF8.GetBytes(p));
                yield return Uri.EscapeDataString(p);
            }
            yield return Token.Replace("-", "");
            yield return Token.Replace("-", " ");
            yield return Token[5..];   // the secret half
            yield return Invite["RMT1-".Length..];
        }

        public string Everywhere() => string.Join("\n",
            $"connecting with token {Token}", $"join invite {Invite}", $"server pin={Pin}", $"admin token: {AdminToken}",
            $"the pairing code is {PairingCodeInSentence}", $"unknown thing {BareRegistered} happened", $"private key {PrivateKey}", $"Authorization: Bearer {Bearer}");

        public Redactor Redactor()
        {
            var r = new Redactor();
            r.AddSecret(BareRegistered);
            return r;
        }
    }

    private static void AssertNoCanary(Canaries c, string text, string where)
    {
        foreach (var needle in c.Needles())
            Assert.False(text.Contains(needle, StringComparison.OrdinalIgnoreCase), $"{where} contains a secret: {needle[..Math.Min(8, needle.Length)]}...");
    }

    // ---- redaction ----

    [Fact]
    public void The_redactor_removes_every_planted_secret_in_every_spelling_and_keeps_ordinary_text()
    {
        var c = Canaries.Make();
        var redactor = c.Redactor();
        var clean = redactor.Apply(c.Everywhere() + "\nGUID 3f2504e0-4f89-11d3-9a0c-0305e82c3301 exit code: 5 device 1a2b3c4d5e6f connected");
        AssertNoCanary(c, clean, "the redacted text");
        Assert.Contains("3f2504e0-4f89-11d3-9a0c-0305e82c3301", clean);
        Assert.Contains("exit code: 5", clean);
        Assert.Contains("device 1a2b3c4d5e6f connected", clean);
        Assert.Contains("Viewer ab2c-3d4e-5f67 connected", c.Redactor().Apply("Viewer ab2c-3d4e-5f67 connected"));   // DeviceIds.Short: public, and CI greps for it
        Assert.DoesNotContain("K7P4", c.Redactor().Apply("typed k7p4-4nx2-qmh9 here"));
        Assert.Contains("RMT1-" + Redactor.Mask, clean);
    }

    [Fact]
    public void The_redactor_is_idempotent_and_handles_empty_input()
    {
        var c = Canaries.Make();
        var once = c.Redactor().Apply(c.Everywhere());
        Assert.Equal(once, c.Redactor().Apply(once));
        Assert.Equal("", c.Redactor().Apply(null));
        Assert.Equal("", c.Redactor().Apply(""));
    }

    [Fact]
    public void A_bare_value_that_was_never_registered_and_has_no_secret_shape_is_not_recognised()
    {
        // The documented limit: shape and registration are all the redactor has.
        var text = new Redactor().Apply("something ZorroSecret123456 happened");
        Assert.Contains("ZorroSecret123456", text);
    }

    // ---- the rolling log ----

    [Fact]
    public void Log_lines_have_a_level_and_a_time_and_lines_below_the_level_are_not_written()
    {
        var log = new RollingFileLog(_dir, "app", new Redactor(), LogLevel.Warning, clock: () => new DateTimeOffset(2026, 10, 17, 9, 30, 0, 123, TimeSpan.Zero));
        log.Info("test", "quiet");
        log.Warning("test", "loud");
        log.Error("test", "boom", new InvalidOperationException("bad"));
        var lines = RollingFileLog.Tail(_dir, "app", 50);
        Assert.Equal("2026-10-17T09:30:00.123Z WARN  [test] loud", lines[0]);
        Assert.StartsWith("2026-10-17T09:30:00.123Z ERROR [test] boom", lines[1]);
        Assert.Contains(lines, l => l.StartsWith("    System.InvalidOperationException: bad"));
        Assert.DoesNotContain(lines, l => l.Contains("quiet"));
        log.Minimum = LogLevel.Debug;
        log.Debug("test", "now visible");
        Assert.Contains(RollingFileLog.Tail(_dir, "app", 50), l => l.EndsWith("[test] now visible"));
    }

    [Fact]
    public void Secrets_never_reach_the_log_file_and_a_message_cannot_forge_a_log_line()
    {
        var c = Canaries.Make();
        var log = new RollingFileLog(_dir, "app", c.Redactor());
        log.Info("net", c.Everywhere());
        log.Error("net", "failed with " + c.Token, new InvalidOperationException("inner " + c.Invite));
        log.Info("net", "first\n2026-10-17T00:00:00.000Z ERROR [fake] forged");
        var all = string.Join("\n", Directory.GetFiles(_dir).Select(File.ReadAllText));
        AssertNoCanary(c, all, "the log file");
        Assert.Contains("connecting with token", all);   // the log is not empty
        Assert.DoesNotContain("\n2026-10-17T00:00:00.000Z ERROR [fake]", all);
        Assert.Contains("first\\n2026", all);
    }

    [Fact]
    public void The_log_starts_a_new_file_at_the_size_limit_and_keeps_only_the_newest_files()
    {
        var log = new RollingFileLog(_dir, "app", new Redactor(), maxFileBytes: 1000, filesKept: 3, clock: () => new DateTimeOffset(2026, 10, 17, 1, 0, 0, TimeSpan.Zero));
        for (var i = 0; i < 60; i++) log.Info("t", $"line {i:00} " + new string('x', 60));
        var files = RollingFileLog.Files(_dir, "app");
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 1000 + 200));
        var tail = RollingFileLog.Tail(_dir, "app", 5);
        Assert.Equal(5, tail.Count);
        Assert.EndsWith("line 59 " + new string('x', 60), tail[^1]);
        Assert.Equal(0, log.DroppedLines);
    }

    [Fact]
    public void A_new_day_starts_a_new_file_and_a_second_log_object_carries_on_the_same_file()
    {
        var day = new DateTimeOffset(2026, 10, 17, 23, 59, 0, TimeSpan.Zero);
        var log = new RollingFileLog(_dir, "app", new Redactor(), clock: () => day);
        log.Info("t", "late");
        day = day.AddMinutes(2);
        log.Info("t", "next day");
        Assert.Equal(["app-20261017-000.log", "app-20261018-000.log"], RollingFileLog.Files(_dir, "app").Select(Path.GetFileName));
        new RollingFileLog(_dir, "app", new Redactor(), clock: () => day).Info("t", "after restart");
        Assert.Equal(2, RollingFileLog.Files(_dir, "app").Count);
    }

    [Fact]
    public void A_log_that_cannot_be_written_drops_lines_and_never_throws()
    {
        var blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x");
        var log = new RollingFileLog(Path.Combine(blocker, "logs"), "app");
        log.Info("t", "nowhere to go");
        Assert.Equal(1, log.DroppedLines);
    }

    [Fact]
    public void The_log_adapts_to_the_Action_string_delegates_the_other_code_already_takes()
    {
        var log = new RollingFileLog(_dir, "host", new Redactor());
        Action<string> write = log.AsDelegate("host");
        write("hello from a delegate");
        Assert.Contains(RollingFileLog.Tail(_dir, "host", 5), l => l.EndsWith("[host] hello from a delegate"));
    }

    [Fact]
    public void Different_programs_keep_separate_files_in_one_folder()
    {
        new RollingFileLog(_dir, "app", new Redactor()).Info("t", "a");
        new RollingFileLog(_dir, "host", new Redactor()).Info("t", "b");
        Assert.Single(RollingFileLog.Files(_dir, "app"));
        Assert.Single(RollingFileLog.Files(_dir, "host"));
    }

    // ---- settings and crash dumps ----

    [Fact]
    public void Diagnostics_settings_default_to_info_and_no_dumps_and_survive_a_damaged_file()
    {
        var s = DiagnosticsSettings.Load(_dir);
        Assert.Equal(LogLevel.Info, s.LogLevel);
        Assert.False(s.CrashDumps);
        s.LogLevel = LogLevel.Debug;
        s.CrashDumps = true;
        Assert.True(s.TrySave(_dir));
        var back = DiagnosticsSettings.Load(_dir);
        Assert.Equal(LogLevel.Debug, back.LogLevel);
        Assert.True(back.CrashDumps);
        File.WriteAllText(Path.Combine(_dir, DiagnosticsSettings.FileName), "{ not json");
        Assert.Equal(LogLevel.Info, DiagnosticsSettings.Load(_dir).LogLevel);
        File.WriteAllText(Path.Combine(_dir, DiagnosticsSettings.FileName), "{\"LogLevel\": 99}");
        Assert.Equal(LogLevel.Info, DiagnosticsSettings.Load(_dir).LogLevel);
    }

    [Fact]
    public void Crash_dumps_keep_only_the_newest_few_and_are_not_written_off_Windows()
    {
        var dumps = Path.Combine(_dir, "dumps");
        Directory.CreateDirectory(dumps);
        for (var i = 1; i <= 5; i++) File.WriteAllText(Path.Combine(dumps, $"app-2026101{i}-120000.dmp"), "x");
        CrashDumps.Prune(dumps, "app", 3);
        Assert.Equal(["app-20261013-120000.dmp", "app-20261014-120000.dmp", "app-20261015-120000.dmp"], CrashDumps.List(dumps).Select(d => d.Name));
        if (!OperatingSystem.IsWindows()) Assert.Null(CrashDumps.TryWrite(dumps, "app"));
    }

    // ---- the diagnostics zip ----

    private sealed class PlantedSource(Canaries c, string dir) : IDiagnosticsSource
    {
        public string ProgramName => "Remotely test " + c.Token;
        public string? CrashDumpDirectory => Path.Combine(dir, "dumps");
        public IReadOnlyDictionary<string, string> Versions() => new Dictionary<string, string> { ["Version"] = "1.2.3", ["Odd"] = c.Invite };
        public string OperatingSystem() => "TestOS 1.0 " + c.AdminToken;
        public IReadOnlyList<AdapterDetail> Adapters() => [new("Ethernet " + c.Token, "Ethernet", "Up", ["192.168.1.5/255.255.255.0", "fe80::1"])];
        public Task<IReadOnlyList<FirewallRuleReport>> FirewallRulesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FirewallRuleReport>>([new("Remotely same-network (TCP)", "present", "enabled: Yes; " + c.Pin)]);
        public Task<IReadOnlyList<KeyValuePair<string, string>>> ServiceStatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>([new("Remotely.Host", "Running " + c.BareRegistered)]);
        public IReadOnlyList<LogSource> Logs() => [new LogSource("app", Path.Combine(dir, "logs"), "app")];
        public IReadOnlyList<SettingsSource> Settings() => [new SettingsSource("viewer.json", Path.Combine(dir, "viewer.json"), ["ServerUri", "Theme", "ServerPin"])];
        public IReadOnlyList<string> CrashFiles() => [Path.Combine(dir, "app-crash.log")];
    }

    [Fact]
    public async Task No_planted_secret_reaches_the_diagnostics_zip_whatever_file_it_started_in()
    {
        var c = Canaries.Make();
        Directory.CreateDirectory(Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(Path.Combine(_dir, "dumps"));
        // A log file that was NOT written through the redacting logger (an older version, another program): the zip must still be clean.
        File.WriteAllText(Path.Combine(_dir, "logs", "app-20261017-000.log"), "2026-10-17T00:00:00.000Z INFO  [raw] " + c.Everywhere().Replace("\n", "\n2026-10-17T00:00:01.000Z INFO  [raw] ") + "\n");
        File.WriteAllText(Path.Combine(_dir, "app-crash.log"), "2026-10-17 crash\n" + c.Everywhere() + "\nSystem.Exception: failed for " + c.Token + "\n");
        File.WriteAllText(Path.Combine(_dir, "viewer.json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ServerUri"] = "wss://example.com/ws",
            ["Theme"] = "dark",
            ["ServerPin"] = c.Pin,                          // allow-listed key, but the value is a pin-shaped secret
            ["AdminToken"] = c.AdminToken,
            ["DevicePrivateKey"] = c.PrivateKey,
            ["Note"] = "hello-" + c.BareRegistered,         // innocent name, free text
            ["Anything"] = c.Token,
        }));
        File.WriteAllBytes(Path.Combine(_dir, "dumps", "app-20261017-000000.dmp"), Encoding.UTF8.GetBytes("MEMORY " + c.Everywhere()));

        using var output = new MemoryStream();
        await DiagnosticsBundle.WriteAsync(new PlantedSource(c, _dir), output, c.Redactor());
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        var all = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            all.AppendLine("### " + entry.FullName).AppendLine(reader.ReadToEnd());
        }
        var text = all.ToString();

        AssertNoCanary(c, text, "the diagnostics zip");
        // not vacuous: the useful, non-secret facts are there
        Assert.Contains("Version: 1.2.3", text);
        Assert.Contains("192.168.1.5/255.255.255.0", text);
        Assert.Contains("Remotely same-network (TCP): present", text);
        Assert.Contains("Remotely.Host: Running", text);
        Assert.Contains("ServerUri: wss://example.com/ws", text);
        Assert.Contains("Theme: dark", text);
        Assert.Contains("values left out:", text);
        Assert.Contains("[raw]", text);
        Assert.Contains("System.Exception: failed for", text);
        // dumps are listed and never included
        Assert.Contains("dump file (not included): app-20261017-000000.dmp", text);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("MEMORY", text);
        Assert.Equal(["README.txt", "summary.txt", "network.txt", "firewall.txt", "services.txt", "settings.txt", "logs/app.txt", "crash.txt"], zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task The_zip_keeps_only_the_last_lines_of_a_long_log_and_works_when_nothing_exists()
    {
        var c = Canaries.Make();
        var logDir = Path.Combine(_dir, "logs");
        var log = new RollingFileLog(logDir, "app", new Redactor());
        for (var i = 0; i < 40; i++) log.Info("t", "entry " + i);
        using var output = new MemoryStream();
        await DiagnosticsBundle.WriteAsync(new PlantedSource(c, _dir), output, c.Redactor(), logLines: 10);
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        var text = new StreamReader(zip.GetEntry("logs/app.txt")!.Open()).ReadToEnd();
        Assert.Equal(10, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("entry 39", text);
        Assert.DoesNotContain("entry 29", text);
        Assert.Contains("(nothing recorded)", new StreamReader(zip.GetEntry("crash.txt")!.Open()).ReadToEnd());
    }

    [Fact]
    public void The_netsh_answer_is_read_into_a_state_and_a_missing_rule_is_not_called_present()
    {
        var present = SystemDiagnosticsSource.Parse("Remotely Server", 0, "Rule Name:   Remotely Server\nEnabled:     Yes\nLocalPort:   8080\n");
        Assert.Equal("present", present.State);
        Assert.Contains("enabled: Yes", present.Detail);
        Assert.Contains("8080", present.Detail);
        Assert.NotEqual("present", SystemDiagnosticsSource.Parse("Remotely Server", 1, "No rules match the specified criteria.").State);
    }

    // ---- connection quality ----

    private sealed class Clock { public long Now; }

    private static ConnectionQualityMonitor Monitor(Clock clock, QualityThresholds? t = null) => new(() => clock.Now, t);

    [Fact]
    public void Quality_is_unknown_until_something_is_heard_then_good_for_a_fast_steady_link()
    {
        var clock = new Clock();
        var m = Monitor(clock);
        Assert.Equal(new QualitySnapshot(QualityLevel.Unknown, QualityReason.NoData, null, null, null, 0), m.Snapshot());
        for (var i = 0; i < 5; i++) { clock.Now += 500; m.RecordRoundTrip(20 + i); m.RecordFrame(); }
        var s = m.Snapshot();
        Assert.Equal(QualityLevel.Good, s.Level);
        Assert.Equal(QualityReason.Healthy, s.Reason);
        Assert.Equal(22, s.RoundTripMs);
        Assert.Equal(1, s.JitterMs);
        Assert.Equal(2.0, s.FramesPerSecond!.Value, 3);
    }

    [Theory]
    [InlineData(100, QualityLevel.Good)]
    [InlineData(101, QualityLevel.Fair)]
    [InlineData(250, QualityLevel.Fair)]
    [InlineData(251, QualityLevel.Poor)]
    public void The_round_trip_limits_decide_good_fair_and_poor(double ms, QualityLevel expected)
    {
        var clock = new Clock();
        var m = Monitor(clock);
        for (var i = 0; i < 4; i++) { clock.Now += 100; m.RecordRoundTrip(ms); }
        var s = m.Snapshot();
        Assert.Equal(expected, s.Level);
        if (expected != QualityLevel.Good) Assert.Equal(QualityReason.SlowRoundTrip, s.Reason);
    }

    [Fact]
    public void An_unsteady_link_is_fair_or_poor_even_when_the_middle_value_is_fine()
    {
        var clock = new Clock();
        var m = Monitor(clock);
        foreach (var ms in new double[] { 20, 90, 20, 90, 20, 90 }) { clock.Now += 100; m.RecordRoundTrip(ms); }
        var s = m.Snapshot();
        Assert.Equal(QualityLevel.Fair, s.Level);
        Assert.Equal(QualityReason.UnstableRoundTrip, s.Reason);
        var clock2 = new Clock();
        var m2 = Monitor(clock2);
        foreach (var ms in new double[] { 20, 200, 20, 200, 20, 200 }) { clock2.Now += 100; m2.RecordRoundTrip(ms); }
        Assert.Equal(QualityLevel.Poor, m2.Snapshot().Level);
    }

    [Fact]
    public void Stalls_lower_the_level_and_age_out_of_the_window()
    {
        var clock = new Clock();
        var m = Monitor(clock);
        clock.Now = 1000; m.RecordRoundTrip(20);
        m.RecordStall();
        Assert.Equal(QualityLevel.Fair, m.Snapshot().Level);
        m.RecordStall(); m.RecordStall();
        var s = m.Snapshot();
        Assert.Equal(QualityLevel.Poor, s.Level);
        Assert.Equal(QualityReason.FramesStalled, s.Reason);
        Assert.Equal(3, s.Stalls);
    }

    [Fact]
    public void Silence_is_lost_and_reconnecting_is_lost_with_its_own_reason()
    {
        var clock = new Clock();
        var m = Monitor(clock);
        clock.Now = 100; m.RecordRoundTrip(20);
        clock.Now += 4999;
        Assert.Equal(QualityLevel.Good, m.Snapshot().Level);
        clock.Now += 1;
        var lost = m.Snapshot();
        Assert.Equal(QualityLevel.Lost, lost.Level);
        Assert.Equal(QualityReason.NoReply, lost.Reason);
        m.SetReconnecting(true);
        Assert.Equal(QualityReason.Reconnecting, m.Snapshot().Reason);
    }

    [Fact]
    public void A_worse_level_shows_at_once_and_a_better_one_only_after_it_has_held()
    {
        var clock = new Clock();
        var m = Monitor(clock);
        clock.Now = 100; m.RecordRoundTrip(400);
        Assert.Equal(QualityLevel.Poor, m.Snapshot().Level);
        // the link recovers: window is 10 s, so push the old sample out with good ones
        clock.Now += 10_500;
        for (var i = 0; i < 4; i++) { clock.Now += 100; m.RecordRoundTrip(20); }
        Assert.Equal(QualityLevel.Poor, m.Snapshot().Level);        // better, but not yet for 3 s
        clock.Now += 2_000; m.RecordRoundTrip(20);
        Assert.Equal(QualityLevel.Poor, m.Snapshot().Level);
        clock.Now += 1_100; m.RecordRoundTrip(20);
        Assert.Equal(QualityLevel.Good, m.Snapshot().Level);
        // and worse again at once
        clock.Now += 100; m.RecordRoundTrip(900);
        Assert.Equal(QualityLevel.Poor, m.Snapshot().Level);
    }

    [Fact]
    public void Nonsense_samples_are_ignored()
    {
        var m = Monitor(new Clock());
        m.RecordRoundTrip(double.NaN);
        m.RecordRoundTrip(-5);
        Assert.Equal(QualityLevel.Unknown, m.Snapshot().Level);
    }

    // ---- the network test ----

    private sealed class FakeEnvironment(IReadOnlyList<AdapterInfo> adapters, IReadOnlyList<LanDiscovery.Nearby>? found = null, TcpOutcome tcp = TcpOutcome.Connected) : IDiagnosisEnvironment
    {
        public IReadOnlyList<AdapterInfo> Adapters() => adapters;
        public Task<IReadOnlyList<LanDiscovery.Nearby>> DiscoverAsync(string? label, CancellationToken ct) => Task.FromResult(found ?? []);
        public Task<TcpOutcome> TryTcpAsync(IPEndPoint endpoint, CancellationToken ct) => Task.FromResult(tcp);
    }

    private static readonly AdapterInfo Wifi = new("Wi-Fi", IPAddress.Parse("192.168.1.5"), IPAddress.Parse("255.255.255.0"), false);

    private static LanDiscovery.Nearby Nearby(string label = "K7P4") => new(label, new IPEndPoint(IPAddress.Parse("192.168.1.20"), 47810), "Kitchen");

    [Fact]
    public async Task The_network_test_reports_each_same_network_step_with_the_catalogue_code_of_the_first_failure()
    {
        var none = await new NetworkTest(new FakeEnvironment([])).RunAsync(null, null, null, default);
        Assert.False(none.Passed);
        Assert.Equal(ErrorCatalogue.NoNetwork, none.FirstFailure!.Error);

        var v6 = await new NetworkTest(new FakeEnvironment([new AdapterInfo("Wi-Fi", null, null, true)])).RunAsync(null, null, null, default);
        Assert.Equal(ErrorCatalogue.OnlyIPv6, v6.FirstFailure!.Error);

        var good = await new NetworkTest(new FakeEnvironment([Wifi], [Nearby()])).RunAsync(null, "K7P4", null, default);
        Assert.True(good.Passed);
        Assert.Equal(["adapter", "subnet", "discovery", "port", "server"], good.Checks.Select(c => c.Id));
        Assert.Equal(CheckStatus.Skipped, good.Checks[^1].Status);

        var silent = await new NetworkTest(new FakeEnvironment([Wifi])).RunAsync(null, "K7P4", null, default);
        Assert.Equal(ErrorCatalogue.PcNotFound, silent.FirstFailure!.Error);

        var blocked = await new NetworkTest(new FakeEnvironment([Wifi], [Nearby()], TcpOutcome.TimedOut)).RunAsync("192.168.1.20", null, null, default);
        Assert.Equal(ErrorCatalogue.PcUnreachable, blocked.FirstFailure!.Error);

        var v6Typed = await new NetworkTest(new FakeEnvironment([Wifi])).RunAsync("fe80::1", null, null, default);
        Assert.Equal(ErrorCatalogue.AddressIsIPv6, v6Typed.FirstFailure!.Error);

        var other = await new NetworkTest(new FakeEnvironment([Wifi])).RunAsync("10.9.9.9", null, null, default);
        Assert.Equal(ErrorCatalogue.PcUnreachable, other.FirstFailure!.Error);
    }

    [Fact]
    public async Task With_no_particular_PC_in_mind_a_silent_network_is_a_note_and_not_a_failure()
    {
        var report = await new NetworkTest(new FakeEnvironment([Wifi])).RunAsync(null, null, null, default);
        Assert.True(report.Passed);
        Assert.Equal(CheckStatus.Note, report.Checks.Single(c => c.Id == "discovery").Status);
    }

    [Fact]
    public async Task The_server_check_measures_a_real_connection_and_names_the_failure_when_there_is_none()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var env = new FakeEnvironment([Wifi]);
        var up = await new NetworkTest(env).RunAsync(null, null, $"wss://127.0.0.1:{port}/ws", default);
        var server = up.Checks.Single(c => c.Id == "server");
        Assert.Equal(CheckStatus.Passed, server.Status);
        Assert.True(server.LatencyMs is >= 0 and < 4000);
        Assert.Contains($"127.0.0.1:{port}", server.Message);
        listener.Stop();

        var down = await new NetworkTest(env).RunAsync(null, null, $"127.0.0.1:{port}", default);
        Assert.Equal(ErrorCatalogue.ServerUnreachable, down.Checks.Single(c => c.Id == "server").Error);

        var bad = await new NetworkTest(env).RunAsync(null, null, "not a server / address", default);
        Assert.Equal(ErrorCatalogue.ServerAddressNotValid, bad.Checks.Single(c => c.Id == "server").Error);
    }

    [Theory]
    [InlineData("wss://host.example.com/ws", "host.example.com", 443)]
    [InlineData("ws://host.example.com/ws", "host.example.com", 80)]
    [InlineData("wss://host.example.com:8443/ws", "host.example.com", 8443)]
    [InlineData("host.example.com:8080", "host.example.com", 8080)]
    [InlineData("host.example.com", "host.example.com", 443)]
    [InlineData("[2001:db8::1]:9000", "2001:db8::1", 9000)]
    [InlineData("192.168.1.9:47800", "192.168.1.9", 47800)]
    public void Server_addresses_are_read_into_host_and_port(string text, string host, int port)
    {
        Assert.True(TcpServerProbe.TryParseAddress(text, out var h, out var p));
        Assert.Equal((host, port), (h, p));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("host:99999")]
    [InlineData("host:x")]
    [InlineData("ftp://host")]
    [InlineData("a:b:c")]
    public void Bad_server_addresses_are_refused(string text) => Assert.False(TcpServerProbe.TryParseAddress(text, out _, out _));

    // ---- the error catalogue ----

    private enum Kind { Real, Constructed, MappingOnly }

    private sealed class PipeChannel(ChannelReader<byte[]> input, ChannelWriter<byte[]> output) : IMessageChannel
    {
        public Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) { output.TryWrite(plaintext.ToArray()); return Task.CompletedTask; }
        public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default) => await input.ReadAsync(ct);
        public Task CloseAsync() { output.TryComplete(); return Task.CompletedTask; }
        public void Dispose() { }
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(0);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static int ClosedTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<ErrorInfo?> Catch(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { return ErrorCatalogue.Classify(ex); }
        return null;
    }

    private const string GoodToken = "K7P4-4NX2-QMH9";

    private static Task<ErrorInfo?> LanConnect(string token, string? address) =>
        Catch(() => LanConnector.ConnectAsync(token, "viewer", address, CancellationToken.None, extraDiscoveryTargets: [new IPEndPoint(IPAddress.Loopback, FreeUdpPort())], discoveryPort: FreeUdpPort(), discoveryTimeout: TimeSpan.FromMilliseconds(400)));

    private static async Task<ErrorInfo?> WithLanHost(Func<LanHost, Task<ErrorInfo?>> body)
    {
        var host = new LanHost(new LanHostOptions { TcpPort = 0, DiscoveryPort = FreeUdpPort(), AllowLoopback = true, MaxFailures = 3, TokenLifetime = TimeSpan.FromMinutes(10), HostName = "test-pc" },
            (_, _, _) => Task.CompletedTask, _ => { });
        host.Start();
        await Task.Delay(50);
        try { return await body(host); }
        finally { await host.DisposeAsync(); }
    }

    private static readonly Dictionary<string, (Kind Kind, Func<Task<ErrorInfo?>> Raise)> Triggers = new()
    {
        ["RMT-1001"] = (Kind.Real, () => LanConnect("K7P4-4NX2", null)),
        ["RMT-1002"] = (Kind.Real, () => LanConnect(GoodToken, "[::1]:47810")),
        ["RMT-1003"] = (Kind.Real, () => LanConnect(GoodToken, "not-an-address")),
        ["RMT-1004"] = (Kind.Real, async () => (await new NetworkTest(new FakeEnvironment([])).RunAsync(null, null, null, default)).FirstFailure?.Error),
        ["RMT-1005"] = (Kind.Real, async () => (await new NetworkTest(new FakeEnvironment([new AdapterInfo("Wi-Fi", null, null, true)])).RunAsync(null, null, null, default)).FirstFailure?.Error),
        ["RMT-1006"] = (Kind.Real, () => LanConnect(GoodToken, null)),
        ["RMT-1007"] = (Kind.Real, async () =>
        {
            var udp = LanDiscovery.OpenResponder(0);
            var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            using var cts = new CancellationTokenSource();
            var closed = ClosedTcpPort();
            var loop = Task.Run(() => LanDiscovery.ServeAsync(udp, () => "K7P4", () => closed, "Kitchen", allowLoopback: true, cts.Token));
            try
            {
                return await Catch(() => LanConnector.ConnectAsync(GoodToken, "viewer", null, CancellationToken.None, [new IPEndPoint(IPAddress.Loopback, port)], port, TimeSpan.FromSeconds(3)));
            }
            finally { cts.Cancel(); udp.Dispose(); await loop.WaitAsync(TimeSpan.FromSeconds(3)); }
        }),
        ["RMT-1008"] = (Kind.Real, () => WithLanHost(host =>
        {
            var wrong = new LanToken(host.CurrentToken.Id, host.CurrentToken.Secret[0] == 'A' ? "BBBBBBBB" : "AAAAAAAA").Display;
            return Catch(() => LanConnector.ConnectAsync(wrong, "viewer", "127.0.0.1:" + host.TcpPort, CancellationToken.None));
        })),
        ["RMT-1009"] = (Kind.Real, async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var token = LanToken.Create();
                using var client = new TcpClient();
                var accepting = listener.AcceptTcpClientAsync();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var server = await accepting;
                var state = new LanTokenState(token, DateTimeOffset.UtcNow.AddMinutes(10), maxAttempts: 1);
                Assert.True(state.TryBeginAttempt());   // the one allowed try is used up
                using var hello = new LanInitiatorHandshake(token);
                await new FramedStream(client.GetStream()).SendAsync(hello.CreateHello1(), CancellationToken.None);
                return await Catch(() => LanSecureSession.AcceptAsync(new FramedStream(server.GetStream()), state, CancellationToken.None));
            }
            finally { listener.Stop(); }
        }),
        ["RMT-1010"] = (Kind.Real, async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var accepting = Task.Run(async () => { using var c = await listener.AcceptTcpClientAsync(); });   // accepts and hangs up
                using var self = DeviceIdentity.Generate();
                var result = await Catch(() => LanResume.ConnectAsync((IPEndPoint)listener.LocalEndpoint, self, DeviceIdentity.Generate().DeviceId, CancellationToken.None));
                await accepting;
                return result;
            }
            finally { listener.Stop(); }
        }),
        ["RMT-1011"] = (Kind.Real, async () =>
        {
            var toViewer = Channel.CreateUnbounded<byte[]>();
            var toHost = Channel.CreateUnbounded<byte[]>();
            var hostSide = new PipeChannel(toHost.Reader, toViewer.Writer);
            var viewerSide = new PipeChannel(toViewer.Reader, toHost.Writer);
            using var host = DeviceIdentity.Generate();
            using var viewer = DeviceIdentity.Generate();
            var name = ControllerHello.Encode("viewer-pc");
            var respond = Catch(() => DeviceProof.RespondAsync(viewerSide, viewer, name, CancellationToken.None));
            await DeviceProof.ChallengeAsync(hostSide, host, "host", ControllerHello.Parse(name).Name, TimeSpan.FromSeconds(5), CancellationToken.None);
            await DeviceProof.SendDecisionAsync(hostSide, allowed: false, remembered: false, "The owner said no.", CancellationToken.None);
            return await respond;
        }),

        ["RMT-2001"] = (Kind.Real, () => Task.FromResult(HandshakeFailure((responder, hello) => responder.ProcessHello1(new byte[] { 1, 2, 3 }, key => true, out _, out _), expectedIsSomeoneElse: false))),
        ["RMT-2002"] = (Kind.Real, () => Task.FromResult(HandshakeFailure((responder, hello1) => responder.ProcessHello1(hello1, _ => true, out _, out _), expectedIsSomeoneElse: true))),
        ["RMT-2003"] = (Kind.Real, () => Task.FromResult(HandshakeFailure((responder, hello1) =>
        {
            hello1[^1] ^= 0xFF;   // damage the end of the signature
            responder.ProcessHello1(hello1, _ => true, out _, out _);
        }, expectedIsSomeoneElse: false))),
        ["RMT-2004"] = (Kind.Real, () => Task.FromResult(HandshakeFailure((responder, hello1) => responder.ProcessHello1(hello1, _ => false, out _, out _), expectedIsSomeoneElse: false))),

        ["RMT-3001"] = (Kind.Real, async () =>
        {
            await using var server = await TestServer.StartAsync();
            await using var client = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
            return await Catch(() => client.ConnectToAsync(new string('a', 64)));
        }),
        ["RMT-3002"] = (Kind.Real, () => Catch(() => CoordinatorClient.ConnectAsync(new CoordinatorOptions { ServerUri = new Uri("ws://127.0.0.1:1/ws") }, DeviceIdentity.Generate()))),
        ["RMT-3003"] = (Kind.Real, () => Catch(() => CoordinatorClient.ConnectAsync(new CoordinatorOptions { ServerUri = new Uri("wss://127.0.0.1:1/ws") }, DeviceIdentity.Generate()))),
        ["RMT-3004"] = (Kind.Constructed, () => Task.FromResult(ErrorCatalogue.Classify(new CoordinatorException("Server speaks a different protocol version.")))),
        ["RMT-3005"] = (Kind.Constructed, () => Task.FromResult(ErrorCatalogue.Classify(new CoordinatorException("Unexpected reply from server.")))),
        ["RMT-3006"] = (Kind.Real, async () =>
        {
            await using var server = await TestServer.StartAsync();
            await using var client = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
            return await Catch(() => client.PairAsync("AAAA-AAAA", "Guess", false));
        }),
        ["RMT-3007"] = (Kind.Real, async () =>
        {
            await using var server = await TestServer.StartAsync(configure: o => o.Limits.PairAttemptsPerMinute = 2);
            await using var client = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
            for (var i = 0; i < 2; i++) await Catch(() => client.PairAsync("AAAA-AAAA", "Guess", false));
            return await Catch(() => client.PairAsync("AAAA-AAAA", "One too many", false));
        }),
        ["RMT-3008"] = (Kind.Real, async () =>
        {
            await using var server = await TestServer.StartAsync();
            await server.Admin.SetSettingsAsync((await server.Admin.GetSettingsAsync()) with { MaxDevices = 1 });
            var (first, _) = await server.PairedDeviceAsync("First", canHost: false);
            await using var _f = first;
            await using var second = await CoordinatorClient.ConnectAsync(server.Options(), DeviceIdentity.Generate());
            return await Catch(async () => await second.PairAsync(await server.NewPairCodeAsync(), "Second", false));
        }),
        ["RMT-3009"] = (Kind.Real, async () =>
        {
            await using var server = await TestServer.StartAsync();
            var (client, identity) = await server.PairedDeviceAsync("Blocked", canHost: true);
            await using var _ = client;
            await server.Admin.SetDeviceBlockedAsync(identity.DeviceId, true);
            await using var retry = await CoordinatorClient.ConnectAsync(server.Options(), identity);
            return await Catch(async () => await retry.PairAsync(await server.NewPairCodeAsync(), "Again", true));
        }),
        ["RMT-3010"] = (Kind.Real, async () =>
        {
            await using var server = await TestServer.StartAsync();
            var (host, hostId) = await server.PairedDeviceAsync("Host", canHost: true);
            var (viewer, _) = await server.PairedDeviceAsync("Viewer", canHost: false);
            await using var _v = viewer;
            await host.DisposeAsync();
            for (var i = 0; i < 100; i++)
            {
                var result = await Catch(() => viewer.ConnectToAsync(hostId.DeviceId));
                if (result == ErrorCatalogue.DeviceOffline) return result;
                await Task.Delay(100);
            }
            return null;
        }),
        ["RMT-3011"] = (Kind.Real, async () =>
        {
            await using var server = await TestServer.StartAsync();
            var (viewer, _) = await server.PairedDeviceAsync("Viewer", canHost: false);
            await using var _v = viewer;
            return await Catch(() => viewer.ConnectToAsync(new string('c', 64)));
        }),
        ["RMT-3012"] = (Kind.Real, () => Catch(() => CoordinatorClient.ConnectAsync(new CoordinatorOptions { ServerUri = new Uri($"ws://127.0.0.1:{ClosedTcpPort()}/ws"), AllowInsecureTransport = true }, DeviceIdentity.Generate()))),
        ["RMT-3013"] = (Kind.Real, async () => (await new TcpServerProbe().ProbeAsync("a b c", 1, default)).Error),

        ["RMT-4001"] = (Kind.Real, () => Catch(() => ServerAdminClient.ForDataDirectory(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N")), 1).GetStatusAsync())),
        ["RMT-4002"] = (Kind.Real, () => Catch(() => ServerAdminClient.ForToken("x", ClosedTcpPort()).GetStatusAsync())),
        ["RMT-4003"] = (Kind.Real, async () =>
        {
            await using var server = await TestServer.StartAsync();
            return await Catch(() => ServerAdminClient.ForToken("wrong-token", server.AdminPort).GetStatusAsync());
        }),

        ["RMT-6001"] = (Kind.Real, () => Task.FromResult(Invite.TryParse("hello", out _, out var problem) ? null : ErrorCatalogue.Classify(problem))),
        ["RMT-6002"] = (Kind.Real, () => Task.FromResult(ErrorCatalogue.FromSetupKey(PublicNameValidator.Validate("https://x.example").ErrorKey))),
        ["RMT-6003"] = (Kind.Real, async () =>
        {
            var actions = new NetshElevatedActions(new FailingRunner(), Path.GetTempPath());
            var result = await actions.AddFirewallRuleAsync(8080);
            return ErrorCatalogue.FromSetupKey(result.ErrorKey);
        }),

        ["RMT-7001"] = (Kind.Real, async () => ErrorCatalogue.FromHelperExit(await new ServiceHelper(new FailingServiceRunner(), "x.exe", _ => { }).RunAsync(ServiceVerb.Install, null))),
        ["RMT-7002"] = (Kind.Real, async () => ErrorCatalogue.FromHelperExit(await new ServiceHelper(new FailingServiceRunner(), "x.exe", _ => { }).RunAsync(ServiceVerb.Restore, null))),
        ["RMT-7003"] = (Kind.Real, async () => ErrorCatalogue.FromHelperExit(await new ServiceHelper(new FailingServiceRunner(), null, _ => { }).RunAsync(ServiceVerb.Install, null))),
        ["RMT-7004"] = (Kind.MappingOnly, () => Task.FromResult(ErrorCatalogue.FromHelperExit(HelperExit.NotElevated))),
        ["RMT-7005"] = (Kind.MappingOnly, () => Task.FromResult(ErrorCatalogue.FromHelperExit(HelperExit.Cancelled))),

        ["RMT-9001"] = (Kind.Real, () => Task.FromResult<ErrorInfo?>(ErrorCatalogue.Describe(new InvalidOperationException("something nobody planned for")))),
    };

    private sealed class FailingRunner : Remotely.Core.NetworkSetup.IProcessRunner
    {
        public Task<ProcessOutput> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct = default) => Task.FromResult(new ProcessOutput(5, "Access is denied."));
    }

    private sealed class FailingServiceRunner : Remotely.Core.ServerManager.IProcessRunner
    {
        public Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default) => Task.FromResult((1, "failed"));
    }

    private static ErrorInfo? HandshakeFailure(Action<ResponderHandshake, byte[]> run, bool expectedIsSomeoneElse)
    {
        using var initiator = DeviceIdentity.Generate();
        using var responder = DeviceIdentity.Generate();
        using var other = DeviceIdentity.Generate();
        using var handshake = new InitiatorHandshake(initiator, expectedIsSomeoneElse ? other.DeviceId : responder.DeviceId);
        using var side = new ResponderHandshake(responder);
        try { run(side, handshake.CreateHello1()); }
        catch (Exception ex) { return ErrorCatalogue.Classify(ex); }
        return null;
    }

    public static IEnumerable<object[]> AllCodes() => ErrorCatalogue.All.Select(e => new object[] { e.Code });

    [Theory]
    [MemberData(nameof(AllCodes))]
    public async Task Every_code_in_the_catalogue_is_raised_by_a_test_that_makes_the_failure_happen(string code)
    {
        if (code.StartsWith("RMT-5", StringComparison.Ordinal))
        {
            await Transfer_codes_are_raised_by_the_transfer_failures(code);
            return;
        }
        Assert.True(Triggers.TryGetValue(code, out var trigger), $"No test raises {code}.");
        var raised = await trigger.Raise();
        Assert.True(raised is not null, $"{code}: the failure did not produce a code at all.");
        Assert.Equal(code, raised!.Code);
    }

    private static Task Transfer_codes_are_raised_by_the_transfer_failures(string code)
    {
        // The real failure-to-code path for files: any exception -> TransferException.From -> catalogue.
        var raised = new Dictionary<string, Exception>
        {
            ["RMT-5006"] = new UnauthorizedAccessException(),
            ["RMT-5008"] = new FileNotFoundException(),
            ["RMT-5011"] = new InvalidDataException("bad"),
            ["RMT-5015"] = new PathTooLongException(),
        };
        if (raised.TryGetValue(code, out var ex))
        {
            Assert.Equal(code, ErrorCatalogue.Classify(TransferException.From(ex))?.Code);
            return Task.CompletedTask;
        }
        // The other transfer errors are raised by HostCore with the enum value; the catalogue must map that very value.
        var name = Enum.GetValues<TransferError>().Select(v => v.ToString()).Single(n => ErrorCatalogue.FromTransferError(n)?.Code == code);
        var error = Enum.Parse<TransferError>(name);
        Assert.Equal(code, ErrorCatalogue.Classify(new TransferException(error, "x"))?.Code);
        return Task.CompletedTask;
    }

    [Fact]
    public void Every_transfer_error_except_none_and_cancelled_has_a_code_and_no_code_is_shared()
    {
        foreach (var value in Enum.GetValues<TransferError>())
        {
            var code = ErrorCatalogue.FromTransferError(value.ToString());
            if (value is TransferError.None or TransferError.Cancelled) Assert.Null(code);
            else Assert.NotNull(code);
            if (code is not null) Assert.Equal($"RMT-{5000 + (int)value}", code.Code);
        }
    }

    [Fact]
    public void Every_helper_exit_code_has_a_catalogue_entry_and_success_has_none()
    {
        Assert.Null(ErrorCatalogue.FromHelperExit(HelperExit.Ok));
        foreach (var exit in new[] { HelperExit.StepFailed, HelperExit.BadUsage, HelperExit.ServerExeMissing, HelperExit.NotElevated, HelperExit.Cancelled })
            Assert.NotNull(ErrorCatalogue.FromHelperExit(exit));
        Assert.Equal(5, new[] { HelperExit.StepFailed, HelperExit.BadUsage, HelperExit.ServerExeMissing, HelperExit.NotElevated, HelperExit.Cancelled }.Select(e => ErrorCatalogue.FromHelperExit(e)!.Code).Distinct().Count());
    }

    [Fact]
    public void Codes_are_unique_well_formed_and_every_trigger_belongs_to_a_code()
    {
        var all = ErrorCatalogue.All;
        Assert.Equal(all.Count, all.Select(e => e.Code).Distinct().Count());
        Assert.All(all, e => Assert.Matches("^RMT-[1-9][0-9]{3}$", e.Code));
        Assert.All(Triggers.Keys, k => Assert.NotNull(ErrorCatalogue.Find(k)));
        Assert.Equal(all.Count(e => !e.Code.StartsWith("RMT-5", StringComparison.Ordinal)), Triggers.Count);
    }

    [Fact]
    public void Some_exceptions_have_no_code_and_say_so_instead_of_guessing()
    {
        Assert.Null(ErrorCatalogue.Classify(new InvalidOperationException("nothing like it")));
        Assert.Null(ErrorCatalogue.Classify((Exception?)null));
        Assert.Null(ErrorCatalogue.Classify("a sentence nobody wrote"));
        Assert.Equal(ErrorCatalogue.Unexpected, ErrorCatalogue.Describe(null));
        Assert.Equal("RMT-1008", ErrorCatalogue.Label(new Exception("outer", new LanAuthException("Wrong token."))));
    }

    [Fact]
    public void Every_code_has_a_doc_page_and_every_doc_page_has_a_code()
    {
        var docs = Path.Combine(RepoRoot(), "docs", "errors");
        var pages = Directory.GetFiles(docs, "RMT-*.md").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(ErrorCatalogue.All.Select(e => e.DocFile).Order(), pages);
        var index = File.ReadAllText(Path.Combine(docs, "README.md"));
        foreach (var e in ErrorCatalogue.All)
        {
            Assert.Contains($"[{e.Code}]({e.DocFile})", index);
            var page = File.ReadAllText(Path.Combine(docs, e.DocFile));
            Assert.StartsWith($"# {e.Code}: ", page);
            Assert.Contains("## What it means", page);
            Assert.Contains("## What to do", page);
        }
    }

    private static Dictionary<string, string> Strings(string app, string language) =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", app, "Strings", language, "Resources.resw")).Root!.Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);

    [Theory]
    [InlineData("Remotely.App", ErrorScope.App)]
    [InlineData("Remotely.ServerManager", ErrorScope.Manager)]
    public void Every_code_has_title_cause_and_fix_in_English_and_Albanian_in_each_program_that_shows_it(string app, ErrorScope scope)
    {
        var en = Strings(app, "en-US");
        var sq = Strings(app, "sq-AL");
        foreach (var e in ErrorCatalogue.All.Where(e => e.Scope == scope || e.Scope == ErrorScope.Both))
            foreach (var key in new[] { e.TitleKey, e.CauseKey, e.FixKey })
            {
                Assert.True(en.TryGetValue(key, out var english) && english.Length > 10, $"{app} en-US lacks {key}");
                Assert.True(sq.TryGetValue(key, out var albanian) && albanian.Length > 10, $"{app} sq-AL lacks {key}");
                Assert.NotEqual(english, albanian);
            }
    }

    [Theory]
    [InlineData("Remotely.App")]
    [InlineData("Remotely.ServerManager")]
    public void The_string_files_have_no_error_text_for_codes_that_do_not_exist_and_stay_in_step(string app)
    {
        var en = Strings(app, "en-US");
        var sq = Strings(app, "sq-AL");
        Assert.Empty(en.Keys.Except(sq.Keys));
        Assert.Empty(sq.Keys.Except(en.Keys));
        var known = ErrorCatalogue.All.Select(e => e.Number).ToHashSet();
        foreach (var key in en.Keys.Where(k => k.StartsWith("Diag_Err_", StringComparison.Ordinal)))
            Assert.Contains(key.Split('_')[2], known);
    }

    [Fact]
    public void Albanian_error_text_is_marked_as_machine_written()
    {
        foreach (var app in new[] { "Remotely.App", "Remotely.ServerManager" })
            Assert.Contains("MACHINE-WRITTEN Albanian", File.ReadAllText(Path.Combine(RepoRoot(), "src", app, "Strings", "sq-AL", "Resources.resw")));
    }

    [Fact]
    public void The_diagnostics_pages_are_listed_for_the_CI_page_check()
    {
        var root = RepoRoot();
        var app = File.ReadAllLines(Path.Combine(root, "tools", "ci", "pages.txt")).Select(l => l.Trim()).ToList();
        var manager = File.ReadAllLines(Path.Combine(root, "tools", "ci", "manager-pages.txt")).Select(l => l.Trim()).ToList();
        foreach (var page in new[] { "DiagnosticsPage", "NetworkTestPage" })
        {
            Assert.Contains(page, app);
            Assert.Contains(page, manager);
            Assert.True(File.Exists(Path.Combine(root, "src", "Remotely.App", "Views", "Help", page + ".xaml.cs")));
            Assert.True(File.Exists(Path.Combine(root, "src", "Remotely.ServerManager", "Views", "Help", page + ".xaml.cs")));
        }
    }
}
