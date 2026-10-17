using System.Net;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Remotely.Client;
using Remotely.Core.Admin;
using Remotely.Core.Crypto;
using Remotely.Server;

namespace Remotely.Tests;

public class ServerStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remotely-storage-" + Guid.NewGuid().ToString("N"));

    public ServerStorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>devices.json exactly as the servers up to 0.4.0 wrote it: a bare JSON array, property names as the record declares them, indented.
    /// (DeviceStore.cs had a single commit in the repository's history before this work, so the format did not change between releases.)</summary>
    private static string LegacyDevicesJson(DeviceIdentity identity, string name) => $$"""
        [
          {
            "DeviceId": "{{identity.DeviceId}}",
            "Name": "{{name}}",
            "PublicKey": "{{Convert.ToBase64String(identity.PublicKey)}}",
            "CanHost": true,
            "CreatedUtc": "2026-01-02T03:04:05+00:00"
          }
        ]
        """;

    // ---- device list schema ----

    [Fact]
    public void A_device_list_from_a_server_up_to_0_4_0_loads_and_is_migrated_keeping_the_original()
    {
        var identity = DeviceIdentity.Generate();
        var path = Path.Combine(_dir, "devices.json");
        File.WriteAllText(path, LegacyDevicesJson(identity, "Old PC"));

        var store = new DeviceStore(_dir);

        var device = Assert.Single(store.All());
        Assert.Equal(identity.DeviceId, device.DeviceId);
        Assert.Equal("Old PC", device.Name);
        Assert.Equal(identity.PublicKey, device.PublicKey);
        Assert.True(device.CanHost);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"), device.CreatedUtc);
        Assert.False(device.Blocked);
        Assert.Null(device.LastSeenUtc);

        using (var migrated = JsonDocument.Parse(File.ReadAllBytes(path)))
            Assert.Equal(DeviceStore.SchemaVersion, migrated.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(LegacyDevicesJson(identity, "Old PC"), File.ReadAllText(path + ".v1.bak"));
        Assert.Equal("Old PC", new DeviceStore(_dir).Find(identity.DeviceId)!.Name);   // and the migrated file loads again
    }

    [Fact]
    public async Task A_server_started_on_data_written_by_an_older_server_lets_the_old_device_in_without_pairing()
    {
        var identity = DeviceIdentity.Generate();
        File.WriteAllText(Path.Combine(_dir, "devices.json"), LegacyDevicesJson(identity, "Before the upgrade"));
        File.WriteAllText(Path.Combine(_dir, "address.txt"), "home.example.com");   // what set-address wrote

        await using var server = await TestServer.StartAsync(dataDir: _dir);

        await using var client = await CoordinatorClient.ConnectAsync(server.Options(), identity);
        Assert.True(client.IsPaired);
        Assert.Equal("Before the upgrade", Assert.Single(await client.ListDevicesAsync()).Name);
        Assert.Equal("home.example.com", (await server.Admin.GetSettingsAsync()).Address);
        var listed = Assert.Single(await server.Admin.ListDevicesAsync());
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"), listed.PairedUtc);
    }

    [Fact]
    public async Task Everything_the_current_server_writes_loads_again_after_a_restart()
    {
        await using var server = await TestServer.StartAsync(tls: true, dataDir: _dir);
        var pin = server.Pin;
        var (a, aId) = await server.PairedDeviceAsync("Kept", canHost: true);
        var (b, bId) = await server.PairedDeviceAsync("Blocked", canHost: false);
        await a.DisposeAsync();
        await b.DisposeAsync();
        await server.Admin.SetDeviceBlockedAsync(bId.DeviceId, true);
        await server.Admin.RenameDeviceAsync(aId.DeviceId, "Renamed");
        var settings = (await server.Admin.GetSettingsAsync()) with { Address = "myhome.example.com", MaxDevices = 7, MaxSessionsPerDevice = 3 };
        await server.Admin.SetSettingsAsync(settings);
        var token = server.AdminToken;

        await server.RestartAsync();

        var devices = await server.Admin.ListDevicesAsync();
        Assert.Equal("Renamed", devices.Single(d => d.DeviceId == aId.DeviceId).Name);
        Assert.True(devices.Single(d => d.DeviceId == bId.DeviceId).Blocked);
        Assert.NotNull(devices.Single(d => d.DeviceId == aId.DeviceId).LastSeenUtc);
        Assert.Equal(settings, await server.Admin.GetSettingsAsync());
        Assert.Equal(pin, (await server.Admin.GetStatusAsync()).CertificatePin);
        Assert.Equal(token, server.AdminToken);
        await using var again = await CoordinatorClient.ConnectAsync(server.Options(), aId);
        Assert.True(again.IsPaired);
    }

    [Fact]
    public void A_device_list_from_a_newer_server_is_refused_and_left_alone()
    {
        var path = Path.Combine(_dir, "devices.json");
        var future = """{"schemaVersion":99,"devices":[]}""";
        File.WriteAllText(path, future);

        var ex = Assert.Throws<DataFormatException>(() => new DeviceStore(_dir));

        Assert.Contains("newer", ex.Message);
        Assert.Equal(future, File.ReadAllText(path));
        Assert.Throws<DataFormatException>(() => RemotelyServer.Build([], o => { o.DataDir = _dir; o.TlsEnabled = false; o.PublicPort = 0; o.AdminPort = 0; }));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("42")]
    [InlineData("""{"devices":[]}""")]
    [InlineData("""[{"Name":"no id"}]""")]
    public void A_damaged_device_list_is_refused_with_a_plain_message_and_left_alone(string content)
    {
        var path = Path.Combine(_dir, "devices.json");
        File.WriteAllText(path, content);
        var ex = Assert.Throws<DataFormatException>(() => new DeviceStore(_dir));
        Assert.Contains("devices.json", ex.Message);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Settings_from_a_newer_server_are_refused()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{"schemaVersion":42,"port":1,"maxDevices":1,"maxSessionsPerDevice":1,"requireTls":true}""");
        Assert.Throws<DataFormatException>(() => RuntimeSettings.Load(_dir, new ServerOptions()));
    }

    [Fact]
    public void Saved_settings_win_over_the_options_the_server_was_started_with()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{"schemaVersion":1,"port":9555,"maxDevices":12,"maxSessionsPerDevice":2,"requireTls":false}""");
        var options = new ServerOptions { PublicPort = 8443, TlsEnabled = true };
        var settings = RuntimeSettings.Load(_dir, options);
        Assert.Equal(9555, options.PublicPort);
        Assert.False(options.TlsEnabled);
        Assert.Equal(12, settings.Current.MaxDevices);
        Assert.Equal(2, settings.Current.MaxSessionsPerDevice);
    }

    [Fact]
    public void Last_seen_times_are_kept_in_memory_and_written_on_flush()
    {
        var identity = DeviceIdentity.Generate();
        var store = new DeviceStore(_dir);
        store.Add(new DeviceRecord(identity.DeviceId, "D", identity.PublicKey, true, DateTimeOffset.UtcNow));
        var when = DateTimeOffset.Parse("2026-05-06T07:08:09+00:00");

        store.Touch(identity.DeviceId, when);
        Assert.Null(new DeviceStore(_dir).Find(identity.DeviceId)!.LastSeenUtc);
        store.Flush();
        Assert.Equal(when, new DeviceStore(_dir).Find(identity.DeviceId)!.LastSeenUtc);
    }

    // ---- audit log ring buffer ----

    [Fact]
    public void The_audit_log_never_grows_past_its_size_cap_and_keeps_the_newest_entries()
    {
        const long cap = 64 * 1024;
        var clock = new StepClock();
        using var log = new AuditLog(_dir, cap, clock);
        for (var i = 0; i < 2000; i++)
        {
            clock.Advance();
            log.Write("test", "device", null, "ok", $"entry {i} " + new string('x', 100));
        }

        var size = Directory.GetFiles(_dir, "audit*.log").Sum(f => new FileInfo(f).Length);
        Assert.True(size <= cap, $"audit files are {size} bytes, cap {cap}");
        var kept = log.Query(null, 5000);
        Assert.True(kept.Count > 100 && kept.Count < 2000);
        Assert.StartsWith("entry 1999 ", kept[0].Detail);
        Assert.Equal(kept.Count, kept.Select(e => e.Detail).Distinct().Count());
        // newest first, no gaps among the entries that were kept
        for (var i = 1; i < kept.Count; i++) Assert.True(kept[i].TimeUtc < kept[i - 1].TimeUtc);
        Assert.Equal(1999 - (kept.Count - 1), int.Parse(kept[^1].Detail!.Split(' ')[1]));
    }

    [Fact]
    public void A_half_written_audit_line_after_a_crash_is_skipped_and_new_entries_still_read_back()
    {
        var clock = new StepClock();
        using (var log = new AuditLog(_dir, 1024 * 1024, clock))
        {
            clock.Advance();
            log.Write("first", "d", null, "ok");
        }
        File.AppendAllText(Path.Combine(_dir, "audit.log"), """{"timeUtc":"2026-01-01T00:00:0""");   // power cut in the middle of a line

        using var again = new AuditLog(_dir, 1024 * 1024, clock);
        clock.Advance();
        again.Write("second", "d", null, "ok");

        var entries = again.Query(null, 10);
        Assert.Equal(["second", "first"], entries.Select(e => e.Kind));
    }

    [Fact]
    public void Audit_details_cannot_inject_lines_or_grow_without_bound()
    {
        using var log = new AuditLog(_dir, 1024 * 1024, TimeProvider.System);
        log.Write("t", "d", null, "ok", "line one\nline two\r\n" + new string('y', 5000));
        var entry = Assert.Single(log.Query(null, 10));
        Assert.DoesNotContain('\n', entry.Detail!);
        Assert.True(entry.Detail!.Length <= 500);
        Assert.Single(File.ReadAllLines(Path.Combine(_dir, "audit.log")));
    }

    // ---- logs ----

    [Fact]
    public void Logs_are_one_json_object_per_line_and_rotate_by_size_keeping_only_the_newest_files()
    {
        var logs = Path.Combine(_dir, "logs");
        using (var provider = new RotatingFileLoggerProvider(logs, maxBytes: 64 * 1024, keep: 2))
        {
            var logger = provider.CreateLogger("Test.Category");
            for (var i = 0; i < 1500; i++) logger.LogInformation("line {N} {Pad}", i, new string('z', 150));
            logger.LogError(new InvalidOperationException("boom"), "with an exception");
        }

        var current = File.ReadAllLines(Path.Combine(logs, "server.log"));
        var rotated = Directory.GetFiles(logs, "server-*.log");
        Assert.True(rotated.Length is >= 1 and <= 2, $"{rotated.Length} rotated files");
        foreach (var line in current.Concat(rotated.SelectMany(File.ReadAllLines)))
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("Test.Category", doc.RootElement.GetProperty("category").GetString());
            Assert.True(doc.RootElement.TryGetProperty("time", out _) && doc.RootElement.TryGetProperty("level", out _) && doc.RootElement.TryGetProperty("message", out _));
        }
        using var last = JsonDocument.Parse(current[^1]);
        Assert.Equal("Error", last.RootElement.GetProperty("level").GetString());
        Assert.Contains("boom", last.RootElement.GetProperty("exception").GetString());
        Assert.All(rotated.Concat([Path.Combine(logs, "server.log")]), f => Assert.True(new FileInfo(f).Length <= 64 * 1024 + 1024));
    }

    [Fact]
    public async Task The_running_server_writes_structured_logs_to_its_data_directory()
    {
        await using var server = await TestServer.StartAsync();
        var (c, _) = await server.PairedDeviceAsync("Logged", canHost: true);
        await using var _c = c;
        var file = RotatingFileLoggerProvider.CurrentFile(server.DataDir);

        await Wait.UntilAsync(() => ReadShared(file).Contains("Paired"), "the pairing to be logged");

        var lines = ReadShared(file).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => JsonDocument.Parse(l).Dispose());
        Assert.Contains(lines, l => l.Contains("starting"));
        Assert.DoesNotContain(lines, l => l.Contains(server.AdminToken));   // the token is never logged
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // ---- pairing codes, rate limiter, sources ----

    [Fact]
    public void Issued_codes_are_listed_with_uses_left_and_can_be_revoked()
    {
        var codes = new PairingCodes(TimeProvider.System);
        var a = codes.Issue(uses: 2, TimeSpan.FromMinutes(10));
        var b = codes.Issue();
        Assert.Equal(2, codes.Active().Count);
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", a.Code);
        Assert.True(codes.TryConsume("1.1.1.1", a.Code));
        Assert.Equal(1, codes.Active().Single(c => c.Id == a.Id).UsesLeft);
        Assert.True(codes.Revoke(b.Id));
        Assert.False(codes.Revoke(b.Id));
        Assert.False(codes.TryConsume("1.1.1.1", b.Code));
        Assert.True(codes.TryConsume("1.1.1.1", a.Code.ToLowerInvariant().Replace("-", " ")));   // case, separator and spaces do not matter
        Assert.Empty(codes.Active());
    }

    [Fact]
    public void A_code_expires()
    {
        var clock = new StepClock();
        var codes = new PairingCodes(clock);
        var issued = codes.Issue(1, TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(codes.TryConsume("1.1.1.1", issued.Code));
        Assert.Empty(codes.Active());
    }

    [Fact]
    public void The_rate_limiter_allows_a_burst_then_refills_over_a_minute_and_tracks_sources_separately()
    {
        var clock = new StepClock();
        var limiter = new RateLimiter(6, clock);
        for (var i = 0; i < 6; i++) Assert.Equal(RateResult.Allowed, limiter.Hit("a"));
        Assert.Equal(RateResult.DeniedFirst, limiter.Hit("a"));
        Assert.Equal(RateResult.Denied, limiter.Hit("a"));
        Assert.Equal(RateResult.Allowed, limiter.Hit("b"));

        clock.Advance(TimeSpan.FromSeconds(10));   // 6 per minute: one more attempt every 10 seconds
        Assert.Equal(RateResult.Allowed, limiter.Hit("a"));
        Assert.Equal(RateResult.DeniedFirst, limiter.Hit("a"));

        clock.Advance(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 6; i++) Assert.Equal(RateResult.Allowed, limiter.Hit("a"));
        Assert.Equal(RateResult.Allowed, new RateLimiter(0, clock).Hit("x"));   // 0 means no limit
    }

    [Fact]
    public void The_limiter_keeps_working_when_flooded_with_sources()
    {
        var clock = new StepClock();
        var limiter = new RateLimiter(10, clock);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200_000; i++)
        {
            limiter.Hit("source-" + i);
            if (i % 1000 == 0) clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(RateResult.Allowed, limiter.Hit("source-after"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"200000 new sources took {watch.Elapsed}");
    }

    [Fact]
    public void Sources_are_grouped_so_an_IPv6_host_cannot_dodge_the_limit_by_changing_its_address()
    {
        Assert.Equal("203.0.113.9", RateLimits.SourceKey(IPAddress.Parse("203.0.113.9")));
        Assert.Equal("203.0.113.9", RateLimits.SourceKey(IPAddress.Parse("::ffff:203.0.113.9")));
        Assert.Equal(RateLimits.SourceKey(IPAddress.Parse("2001:db8:1:2::1")), RateLimits.SourceKey(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")));
        Assert.NotEqual(RateLimits.SourceKey(IPAddress.Parse("2001:db8:1:2::1")), RateLimits.SourceKey(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("unknown", RateLimits.SourceKey(null));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("home.example.com", true)]
    [InlineData("192.168.1.10:9000", true)]
    [InlineData("wss://home.example.com:8443/ws", true)]
    [InlineData("has space", false)]
    [InlineData("under_score.com", true)]
    [InlineData("-leading.example.com", false)]
    [InlineData("http://home.example.com/ws", false)]
    [InlineData("wss://home.example.com/other", false)]
    public void Addresses_are_checked(string address, bool valid) =>
        Assert.Equal(valid, ServerAddress.Validate(address, true, 8443) is null);

    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => 1000;

        public void Advance(TimeSpan? by = null)
        {
            var step = by ?? TimeSpan.FromMilliseconds(1);
            _now += step;
            _ticks += (long)(step.TotalMilliseconds);
        }
    }
}
