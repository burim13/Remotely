using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Remotely.Core.Admin;

/// <summary>
/// HTTP client for the server's admin API v1 (see docs/server-admin-api.md). The API listens on loopback only and wants the local admin token
/// (<c>admin.token</c> in the server's data directory) as a bearer token, so this runs on the server's own PC.
/// </summary>
public sealed class ServerAdminClient : IServerAdmin, IDisposable
{
    public const string TokenFileName = "admin.token";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<string?> _tokenProvider;
    private readonly bool _adoptRotation;
    private volatile string? _rotated;

    /// <param name="http">Client whose BaseAddress is the admin port, for example http://127.0.0.1:8181/. The caller keeps ownership.</param>
    /// <param name="tokenProvider">Called for every request, so a rotated token in the file is picked up.</param>
    public ServerAdminClient(HttpClient http, Func<string?> tokenProvider) : this(http, tokenProvider, ownsHttp: false, adoptRotation: false) { }

    private ServerAdminClient(HttpClient http, Func<string?> tokenProvider, bool ownsHttp, bool adoptRotation)
    {
        _adoptRotation = adoptRotation;
        if (http.BaseAddress is null) throw new ArgumentException("The HttpClient needs a BaseAddress.", nameof(http));
        _http = http;
        _tokenProvider = tokenProvider;
        _ownsHttp = ownsHttp;
    }

    /// <summary>Where the server keeps its data when nothing else is configured: the same rule the server uses.</summary>
    public static string DefaultDataDirectory
    {
        get
        {
            var root = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, OperatingSystem.IsWindows() ? "Remotely Server" : "RemotelyServer");
        }
    }

    /// <summary>A client that reads the token from <paramref name="dataDir"/> (default: the standard data directory) on every call.</summary>
    public static ServerAdminClient ForDataDirectory(string? dataDir = null, int adminPort = 8181)
    {
        var path = Path.Combine(string.IsNullOrWhiteSpace(dataDir) ? DefaultDataDirectory : dataDir, TokenFileName);
        return new ServerAdminClient(NewHttp(adminPort), () => ReadTokenFile(path), ownsHttp: true, adoptRotation: false);
    }

    public static ServerAdminClient ForToken(string token, int adminPort = 8181) =>
        new(NewHttp(adminPort), () => token, ownsHttp: true, adoptRotation: true);

    private static HttpClient NewHttp(int port) => new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(30) };

    private static string? ReadTokenFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ServerAdminException(0, $"The admin token at {path} cannot be read ({ex.Message}). Run this as an administrator on the server PC.", ex);
        }
    }

    public Task<ServerStatus> GetStatusAsync(CancellationToken ct = default) => GetAsync<ServerStatus>("admin/v1/status", ct);

    public Task<InviteInfo> CreateInviteAsync(TimeSpan lifetime, int uses, CancellationToken ct = default) =>
        SendAsync<InviteInfo>(HttpMethod.Post, "admin/v1/invites", new { lifetimeMinutes = (int)Math.Ceiling(lifetime.TotalMinutes), uses }, ct);

    public async Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(CancellationToken ct = default) =>
        await GetAsync<InviteInfo[]>("admin/v1/invites", ct).ConfigureAwait(false);

    public Task RevokeInviteAsync(string inviteId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"admin/v1/invites/{Uri.EscapeDataString(inviteId)}", null, ct);

    public async Task<IReadOnlyList<AdminDevice>> ListDevicesAsync(CancellationToken ct = default) =>
        await GetAsync<AdminDevice[]>("admin/v1/devices", ct).ConfigureAwait(false);

    public Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Put, $"admin/v1/devices/{Uri.EscapeDataString(deviceId)}/name", new { name }, ct);

    /// <summary>Forgets the device. It can join again with a new invite; use <see cref="SetDeviceBlockedAsync"/> to keep it out.</summary>
    public Task RevokeDeviceAsync(string deviceId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"admin/v1/devices/{Uri.EscapeDataString(deviceId)}", null, ct);

    public Task SetDeviceBlockedAsync(string deviceId, bool blocked, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Put, $"admin/v1/devices/{Uri.EscapeDataString(deviceId)}/blocked", new { blocked }, ct);

    /// <summary>Newest first. <paramref name="since"/> returns only entries after that time.</summary>
    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(DateTimeOffset? since = null, int max = 500, CancellationToken ct = default)
    {
        var query = $"admin/v1/audit?max={max}" + (since is { } s ? "&since=" + Uri.EscapeDataString(s.UtcDateTime.ToString("O")) : "");
        return await GetAsync<AuditEntry[]>(query, ct).ConfigureAwait(false);
    }

    public Task<ServerSettings> GetSettingsAsync(CancellationToken ct = default) => GetAsync<ServerSettings>("admin/v1/settings", ct);

    public async Task SetSettingsAsync(ServerSettings settings, CancellationToken ct = default) =>
        await UpdateSettingsAsync(settings, ct).ConfigureAwait(false);

    /// <summary>Like <see cref="SetSettingsAsync"/>, and says whether a restart is needed for the change to take effect.</summary>
    public Task<SettingsUpdateResult> UpdateSettingsAsync(ServerSettings settings, CancellationToken ct = default) =>
        SendAsync<SettingsUpdateResult>(HttpMethod.Put, "admin/v1/settings", settings, ct);

    /// <summary>Downloads a backup of the server's data (device list, settings, address, TLS key) into <paramref name="folder"/>; returns the file path. Keep it private: it holds the server's TLS key.</summary>
    public async Task<string> CreateBackupAsync(string folder, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Get, "admin/v1/backup", null, ct).ConfigureAwait(false);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Remotely-Server-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.zip");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Restores a file made by <see cref="CreateBackupAsync"/>. The result says whether a restart is needed and warns when the key pin changed.</summary>
    public async Task<RestoreResult> RestoreBackupAsync(string backupFile, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(backupFile, ct).ConfigureAwait(false);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var response = await SendRawAsync(HttpMethod.Post, "admin/v1/restore", content, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<RestoreResult>(Json, ct).ConfigureAwait(false))!;
    }

    /// <summary>Renews the TLS certificate. With <paramref name="newKey"/> false the key (and so the pin devices hold) stays; true changes the pin and needs <paramref name="confirmPinChange"/>.</summary>
    public Task<CertificateRenewal> RenewCertificateAsync(bool newKey = false, bool confirmPinChange = false, CancellationToken ct = default) =>
        SendAsync<CertificateRenewal>(HttpMethod.Post, "admin/v1/certificate/renew", new { newKey, confirmPinChange }, ct);

    /// <summary>Makes a new admin token and invalidates the old one. Returns the new token. A client that reads the token from a file picks the new one up by itself; one made with <see cref="ForToken"/> switches to it.</summary>
    public async Task<string> RotateTokenAsync(CancellationToken ct = default)
    {
        var reply = await SendAsync<Dictionary<string, string>>(HttpMethod.Post, "admin/v1/token/rotate", null, ct).ConfigureAwait(false);
        if (_adoptRotation) _rotated = reply["token"];   // a client built from a fixed token keeps working; one that reads the file follows the file
        return reply["token"];
    }

    /// <summary>Makes a ticket for the public check page /check/{id}; see <see cref="ServerOutsideCheck"/>.</summary>
    public Task<OutsideCheckTicketInfo> StartOutsideCheckAsync(TimeSpan? lifetime = null, CancellationToken ct = default) =>
        SendAsync<OutsideCheckTicketInfo>(HttpMethod.Post, "admin/v1/outside-check" + (lifetime is { } l ? $"?minutes={(int)Math.Ceiling(l.TotalMinutes)}" : ""), null, ct);

    public Task<OutsideCheckState> GetOutsideCheckAsync(string id, CancellationToken ct = default) =>
        SendAsync<OutsideCheckState>(HttpMethod.Get, $"admin/v1/outside-check/{Uri.EscapeDataString(id)}", null, ct);

    public Task<ServerMetricsSnapshot> GetMetricsAsync(CancellationToken ct = default) => GetAsync<ServerMetricsSnapshot>("admin/v1/metrics", ct);

    private Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var content = body is null ? null : JsonContent.Create(body, body.GetType(), options: Json);
        using var response = await SendRawAsync(method, path, content, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default!;
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false))!;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        var token = _rotated ?? _tokenProvider();
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ServerAdminException(0, "The server's admin port cannot be reached. Is the server running?", ex);
        }
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            string message;
            try
            {
                var error = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Json, ct).ConfigureAwait(false);
                message = error is not null && error.TryGetValue("error", out var e) ? e : response.ReasonPhrase ?? "Request failed.";
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { message = response.ReasonPhrase ?? "Request failed."; }
            throw new ServerAdminException((int)response.StatusCode, message);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
