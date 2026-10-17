namespace Remotely.Core.Admin;

/// <summary>Result of saving settings. Address and the two limits apply at once; the public port and the TLS switch need a restart of the server.</summary>
public sealed record SettingsUpdateResult(ServerSettings Settings, bool RestartRequired, IReadOnlyList<string> RestartRequiredFor);

/// <summary>Result of restoring a backup. <see cref="PinChanged"/> true means every device that pinned the old key must be given a new invite.</summary>
public sealed record RestoreResult(int DevicesRestored, bool RestartRequired, string OldPin, string NewPin, bool PinChanged, string? Warning);

/// <summary>Result of renewing the TLS certificate. A renewal that keeps the key leaves the pin alone; a new key changes the pin and devices must re-join.</summary>
public sealed record CertificateRenewal(string OldPin, string NewPin, bool PinChanged, DateTimeOffset ExpiresUtc, string? Warning);

/// <summary>Counters since the server started, plus a few process figures (used by the soak tests).</summary>
public sealed record ServerMetricsSnapshot(
    double UptimeSeconds, int DevicesTotal, int DevicesOnline, int SessionsActive,
    long ConnectionsAccepted, long ConnectionsRateLimited, long PairingsSucceeded, long PairingsFailed, long PairingsRateLimited,
    long SessionsOpened, long SessionsRefused, long FramesRelayed, long BytesRelayed, long AdminUnauthorized,
    long WorkingSetBytes, long ManagedHeapBytes, int ThreadCount);

/// <summary>An admin call failed. <see cref="StatusCode"/> is the HTTP status, or 0 when the server could not be reached or the token could not be read.</summary>
public sealed class ServerAdminException(int statusCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}

public sealed record OutsideCheckTicketInfo(string Id, string Path, DateTimeOffset ExpiresUtc);

/// <summary>Who asked for the check page first (null until someone does). <see cref="Kind"/> is the address class the server worked out, for information.</summary>
public sealed record OutsideCheckState(string? Source, DateTimeOffset? TimeUtc, string? Kind, DateTimeOffset ExpiresUtc);
