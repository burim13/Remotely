using Remotely.Core.Admin;
using Remotely.Core.Protocol;

namespace Remotely.Core.ServerManager;

/// <summary>The server cannot be reached (not running, wrong port, admin API refused). Pages show this as a message, never as a crash.</summary>
public sealed class ServerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// An in-memory <see cref="IServerAdmin"/> with believable data. The Server Manager starts with it when launched with <c>--fake</c> (CI uses that to photograph
/// populated pages), and the tests use it. Everything it returns is invented; nothing here is read from a real server.
/// </summary>
public sealed class FakeServerAdmin : IServerAdmin
{
    public const string FakePin = "e3411e89584cc29534e669f1f94b3fd7b40abe129b0df653ab771638bda41aad";
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Random _random;
    private readonly List<AdminDevice> _devices = [];
    private readonly List<InviteInfo> _invites = [];
    private readonly List<AuditEntry> _audit = [];
    private readonly DateTimeOffset _started;
    private ServerSettings _settings = new("192.168.1.50", 8443, 25, 2, true);

    public FakeServerAdmin(TimeProvider? clock = null, bool populated = true, int seed = 11)
    {
        _clock = clock ?? TimeProvider.System;
        _random = new Random(seed);
        var now = _clock.GetUtcNow();
        _started = now.AddHours(-30).AddMinutes(-12);
        if (!populated) return;

        string Id(int n) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([(byte)n])).ToLowerInvariant();
        _devices.AddRange(
        [
            new AdminDevice(Id(1), "Burim's laptop", true, true, false, now.AddDays(-40), now.AddSeconds(-5)),
            new AdminDevice(Id(2), "Living room PC", true, true, false, now.AddDays(-33), now.AddSeconds(-9)),
            new AdminDevice(Id(3), "Mum's desktop", true, false, false, now.AddDays(-20), now.AddHours(-3)),
            new AdminDevice(Id(4), "Office NUC", true, true, false, now.AddDays(-12), now.AddSeconds(-2)),
            new AdminDevice(Id(5), "Garage mini PC", true, false, false, now.AddDays(-9), now.AddDays(-2)),
            new AdminDevice(Id(6), "Old tablet", false, false, true, now.AddDays(-60), now.AddDays(-35)),
            new AdminDevice(Id(7), "New phone (never connected)", false, false, false, now.AddMinutes(-20), null),
        ]);
        _invites.AddRange(
        [
            MakeInvite(now.AddMinutes(-20), TimeSpan.FromHours(1), 10, 9),
            MakeInvite(now.AddDays(-3), TimeSpan.FromHours(1), 1, 0),
            MakeInvite(now.AddDays(-9), TimeSpan.FromDays(1), 5, 3),
        ]);
        _audit.AddRange(
        [
            new AuditEntry(now.AddMinutes(-20), "pair", Id(7), null, "ok", "invite"),
            new AuditEntry(now.AddHours(-2), "connect", Id(1), Id(4), "ok", null),
            new AuditEntry(now.AddHours(-2).AddMinutes(41), "disconnect", Id(1), Id(4), "ok", "ended by viewer"),
            new AuditEntry(now.AddHours(-3), "connect", Id(3), Id(2), "refused", "viewer is not trusted by the host"),
            new AuditEntry(now.AddHours(-5), "connect", Id(1), Id(2), "ok", null),
            new AuditEntry(now.AddDays(-1), "pair", Id(5), null, "ok", "invite"),
            new AuditEntry(now.AddDays(-2), "connect", Id(6), Id(2), "refused", "device is blocked"),
            new AuditEntry(now.AddDays(-4), "pair", Id(4), null, "ok", "pairing code"),
            new AuditEntry(now.AddDays(-5), "pair", Id(6), null, "failed", "wrong code"),
        ]);
    }

    /// <summary>When true every call fails with <see cref="ServerUnavailableException"/>, like a server that is stopped.</summary>
    public bool Offline { get; set; }

    /// <summary>Adds a freshly paired device, as if someone had just used an invite. Used to exercise the new-device notification.</summary>
    public AdminDevice SimulatePairing(string name)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name + _devices.Count))).ToLowerInvariant();
            var device = new AdminDevice(id, name, true, true, false, now, now);
            _devices.Add(device);
            _audit.Add(new AuditEntry(now, "pair", id, null, "ok", "invite"));
            return device;
        }
    }

    private void ThrowIfOffline()
    {
        if (Offline) throw new ServerUnavailableException("The server is not running (fake).");
    }

    private InviteInfo MakeInvite(DateTimeOffset created, TimeSpan lifetime, int uses, int usesLeft)
    {
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = CodeAlphabet[_random.Next(CodeAlphabet.Length)];
        var code = new string(chars, 0, 4) + "-" + new string(chars, 4, 4);
        var text = new Invite("wss://" + _settings.Address + ":" + _settings.Port + "/ws", FakePin, code).Encode();
        return new InviteInfo(Guid.NewGuid().ToString("N")[..12], text, created + lifetime, uses, usesLeft);
    }

    public Task<ServerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            return Task.FromResult(new ServerStatus("1.0.0 (fake)", _started, _settings.Address, _settings.Port, _settings.RequireTls, FakePin,
                now.AddDays(364), _devices.Count, _devices.Count(d => d.Online), _devices.Count(d => d.Online) / 2));
        }
    }

    public Task<InviteInfo> CreateInviteAsync(TimeSpan lifetime, int uses, CancellationToken ct = default)
    {
        ThrowIfOffline();
        if (lifetime <= TimeSpan.Zero || uses < 1) throw new ArgumentOutOfRangeException(nameof(uses), "Lifetime and uses must be positive.");
        lock (_gate)
        {
            var invite = MakeInvite(_clock.GetUtcNow(), lifetime, uses, uses);
            _invites.Insert(0, invite);
            _audit.Add(new AuditEntry(_clock.GetUtcNow(), "invite-created", "", null, "ok", $"{uses} use(s)"));
            return Task.FromResult(invite);
        }
    }

    public Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(CancellationToken ct = default)
    {
        ThrowIfOffline();
        lock (_gate) return Task.FromResult<IReadOnlyList<InviteInfo>>(_invites.ToList());
    }

    public Task RevokeInviteAsync(string inviteId, CancellationToken ct = default)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            var index = _invites.FindIndex(i => i.Id == inviteId);
            if (index < 0) throw new KeyNotFoundException("No such invite.");
            _invites[index] = _invites[index] with { ExpiresUtc = _clock.GetUtcNow().AddSeconds(-1), UsesLeft = 0 };
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<AdminDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        ThrowIfOffline();
        lock (_gate) return Task.FromResult<IReadOnlyList<AdminDevice>>(_devices.ToList());
    }

    private int IndexOf(string deviceId)
    {
        var index = _devices.FindIndex(d => d.DeviceId == deviceId);
        return index < 0 ? throw new KeyNotFoundException("No such device.") : index;
    }

    public Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default)
    {
        ThrowIfOffline();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A device needs a name.", nameof(name));
        lock (_gate)
        {
            var i = IndexOf(deviceId);
            _devices[i] = _devices[i] with { Name = name.Trim() };
            return Task.CompletedTask;
        }
    }

    public Task RevokeDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            _devices.RemoveAt(IndexOf(deviceId));
            _audit.Add(new AuditEntry(_clock.GetUtcNow(), "revoke", deviceId, null, "ok", null));
            return Task.CompletedTask;
        }
    }

    public Task SetDeviceBlockedAsync(string deviceId, bool blocked, CancellationToken ct = default)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            var i = IndexOf(deviceId);
            _devices[i] = _devices[i] with { Blocked = blocked, Online = blocked ? false : _devices[i].Online };
            _audit.Add(new AuditEntry(_clock.GetUtcNow(), blocked ? "block" : "unblock", deviceId, null, "ok", null));
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(DateTimeOffset? since = null, int max = 500, CancellationToken ct = default)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            var rows = _audit.Where(a => since is null || a.TimeUtc >= since).OrderByDescending(a => a.TimeUtc).Take(Math.Max(0, max)).ToList();
            return Task.FromResult<IReadOnlyList<AuditEntry>>(rows);
        }
    }

    public Task<ServerSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        ThrowIfOffline();
        lock (_gate) return Task.FromResult(_settings);
    }

    public Task SetSettingsAsync(ServerSettings settings, CancellationToken ct = default)
    {
        ThrowIfOffline();
        lock (_gate) _settings = settings;
        return Task.CompletedTask;
    }

    /// <summary>Writes a small marker file so the backup page has something to show; a real backup is the server's job.</summary>
    public Task<string> CreateBackupAsync(string folder, CancellationToken ct = default)
    {
        ThrowIfOffline();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "remotely-server-backup-fake-" + _clock.GetUtcNow().ToString("yyyyMMdd-HHmmss") + ".txt");
        File.WriteAllText(path, "This file was written by the fake server of the Server Manager. It is not a real backup.");
        return Task.FromResult(path);
    }
}

/// <summary>Answers <c>sc.exe query</c> as if the server's service were running; used with <c>--fake</c> so the demo pages agree with each other.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default) =>
        Task.FromResult((0, "SERVICE_NAME: Remotely.Server\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 4  RUNNING\r\n"));
}

/// <summary>Stands in for the real client until it is wired in: every call says the server cannot be reached, so the pages show a clear message.</summary>
public sealed class UnavailableServerAdmin(string reason) : IServerAdmin
{
    private ServerUnavailableException Fail() => new(reason);
    public Task<ServerStatus> GetStatusAsync(CancellationToken ct = default) => throw Fail();
    public Task<InviteInfo> CreateInviteAsync(TimeSpan lifetime, int uses, CancellationToken ct = default) => throw Fail();
    public Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(CancellationToken ct = default) => throw Fail();
    public Task RevokeInviteAsync(string inviteId, CancellationToken ct = default) => throw Fail();
    public Task<IReadOnlyList<AdminDevice>> ListDevicesAsync(CancellationToken ct = default) => throw Fail();
    public Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default) => throw Fail();
    public Task RevokeDeviceAsync(string deviceId, CancellationToken ct = default) => throw Fail();
    public Task SetDeviceBlockedAsync(string deviceId, bool blocked, CancellationToken ct = default) => throw Fail();
    public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(DateTimeOffset? since = null, int max = 500, CancellationToken ct = default) => throw Fail();
    public Task<ServerSettings> GetSettingsAsync(CancellationToken ct = default) => throw Fail();
    public Task SetSettingsAsync(ServerSettings settings, CancellationToken ct = default) => throw Fail();
    public Task<string> CreateBackupAsync(string folder, CancellationToken ct = default) => throw Fail();
}
