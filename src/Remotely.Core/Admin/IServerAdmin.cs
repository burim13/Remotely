namespace Remotely.Core.Admin;

/// <summary>
/// The server's admin API as a typed client. The server (Remotely.Server) implements the HTTP side; the Server Manager, the command line and the tests use this.
/// CONTRACT: owned by the integrator. Agent A10 implements the server side and <see cref="ServerAdminClient"/> (to be added); nobody else changes these shapes without asking.
/// </summary>
public interface IServerAdmin
{
    Task<ServerStatus> GetStatusAsync(CancellationToken ct = default);
    Task<InviteInfo> CreateInviteAsync(TimeSpan lifetime, int uses, CancellationToken ct = default);
    Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(CancellationToken ct = default);
    Task RevokeInviteAsync(string inviteId, CancellationToken ct = default);
    Task<IReadOnlyList<AdminDevice>> ListDevicesAsync(CancellationToken ct = default);
    Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default);
    Task RevokeDeviceAsync(string deviceId, CancellationToken ct = default);
    Task SetDeviceBlockedAsync(string deviceId, bool blocked, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> GetAuditAsync(DateTimeOffset? since = null, int max = 500, CancellationToken ct = default);
    Task<ServerSettings> GetSettingsAsync(CancellationToken ct = default);
    Task SetSettingsAsync(ServerSettings settings, CancellationToken ct = default);
    Task<string> CreateBackupAsync(string folder, CancellationToken ct = default);
}

public sealed record ServerStatus(string Version, DateTimeOffset StartedUtc, string Address, int Port, bool TlsEnabled, string CertificatePin,
    DateTimeOffset CertificateExpiresUtc, int DevicesTotal, int DevicesOnline, int SessionsActive);

public sealed record InviteInfo(string Id, string InviteText, DateTimeOffset ExpiresUtc, int UsesAllowed, int UsesLeft);

public sealed record AdminDevice(string DeviceId, string Name, bool CanHost, bool Online, bool Blocked, DateTimeOffset PairedUtc, DateTimeOffset? LastSeenUtc);

public sealed record AuditEntry(DateTimeOffset TimeUtc, string Kind, string DeviceId, string? PeerDeviceId, string Result, string? Detail);

public sealed record ServerSettings(string Address, int Port, int MaxDevices, int MaxSessionsPerDevice, bool RequireTls);
