using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Remotely.Core.Admin;
using Remotely.Core.Protocol;

namespace Remotely.Server;

/// <summary>An admin operation that cannot be done, with the HTTP status to answer with and a sentence for the person using the tool.</summary>
public sealed class AdminException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Everything the admin API can do, apart from the HTTP plumbing. Each change is written to the audit log.</summary>
public sealed class AdminOperations(
    ServerOptions options, string dataDir, DeviceStore store, PairingCodes codes, RuntimeSettings settings, AuditLog audit, Hub hub,
    CertificateHolder? certificate, AdminToken? token, ServerMetrics metrics, TimeProvider time, ILogger<AdminOperations> log)
{
    public static string Version { get; } = (typeof(AdminOperations).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0];

    public AdminToken? Token => token;
    public string DataDir => dataDir;

    public ServerStatus Status()
    {
        var address = settings.Current.Address is { Length: > 0 } saved ? saved : ServerAddress.LocalNetworkAddress() ?? "";
        return new ServerStatus(Version, metrics.StartedUtc, address, options.PublicPort, options.TlsEnabled,
            certificate?.Pin ?? "", certificate is null ? default : new DateTimeOffset(certificate.Current.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            store.Count, hub.OnlineCount, hub.SessionCount);
    }

    public ServerMetricsSnapshot Metrics() => metrics.Snapshot(store.Count, hub.OnlineCount, hub.SessionCount);

    // ---- invites ----

    public InviteInfo CreateInvite(int lifetimeMinutes, int uses)
    {
        if (lifetimeMinutes < 1 || lifetimeMinutes > PairingCodes.MaxLifetime.TotalMinutes) throw new AdminException(400, "The lifetime must be between 1 minute and 24 hours.");
        if (uses < 1 || uses > PairingCodes.MaxUses) throw new AdminException(400, $"The number of uses must be between 1 and {PairingCodes.MaxUses}.");
        var text = InviteTextOrThrow();
        var issued = codes.Issue(uses, TimeSpan.FromMinutes(lifetimeMinutes));
        audit.Write("invite.create", "", null, "ok", $"id {issued.Id}, {uses} uses, {lifetimeMinutes} min");
        return Describe(issued, text);
    }

    public IReadOnlyList<InviteInfo> ListInvites()
    {
        var active = codes.Active();
        if (active.Count == 0) return [];
        var text = InviteTextOrThrow();
        return [.. active.Select(c => Describe(c, text))];
    }

    private Func<string, string> InviteTextOrThrow()
    {
        var (uri, _) = ServerAddress.Resolve(dataDir, options.TlsEnabled, options.PublicPort);
        if (uri is null) throw new AdminException(409, "The server's address is not set and could not be worked out. Set the address in the settings first.");
        var pin = certificate?.Pin ?? "";
        return code => new Invite(uri, pin, code).Encode();
    }

    private static InviteInfo Describe(IssuedCode c, Func<string, string> text) => new(c.Id, text(c.Code), c.ExpiresUtc, c.UsesAllowed, c.UsesLeft);

    public void RevokeInvite(string id)
    {
        if (!codes.Revoke(id)) throw new AdminException(404, "There is no such invite (it may have expired or been used up).");
        audit.Write("invite.revoke", "", null, "ok", $"id {id}");
    }

    // ---- devices ----

    public IReadOnlyList<AdminDevice> Devices() =>
        [.. store.All().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.DeviceId, StringComparer.Ordinal)
            .Select(d => new AdminDevice(d.DeviceId, d.Name, d.CanHost, hub.IsOnline(d.DeviceId), d.Blocked, d.CreatedUtc, d.LastSeenUtc))];

    public void Rename(string id, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new AdminException(400, "The name cannot be empty.");
        var clean = Hub.CleanName(name);
        var before = store.Find(id) ?? throw NoSuchDevice();
        store.Update(id, d => d with { Name = clean });
        audit.Write("device.rename", id, null, "ok", $"'{before.Name}' to '{clean}'");
    }

    /// <summary>Forgets the device and disconnects it. It may join again with a new invite.</summary>
    public async Task RevokeAsync(string id, string via = "admin API")
    {
        var before = store.Find(id) ?? throw NoSuchDevice();
        store.Remove(id);
        audit.Write("device.revoke", id, null, "ok", $"'{before.Name}' removed via {via}");
        await hub.KickAsync(id, "Removed by the server").ConfigureAwait(false);
    }

    public async Task SetBlockedAsync(string id, bool blocked)
    {
        var updated = store.Update(id, d => d with { Blocked = blocked }) ?? throw NoSuchDevice();
        audit.Write(blocked ? "device.block" : "device.unblock", id, null, "ok", $"'{updated.Name}'");
        if (blocked) await hub.KickAsync(id, "Blocked by the server").ConfigureAwait(false);
    }

    private static AdminException NoSuchDevice() => new(404, "There is no such device.");

    // ---- settings ----

    public ServerSettings GetSettings() => settings.Current;

    public SettingsUpdateResult SetSettings(ServerSettings requested)
    {
        if (requested is null) throw new AdminException(400, "Settings are missing.");
        if (settings.Validate(requested, options.AdminPort) is { } problem) throw new AdminException(400, problem);
        var before = settings.Current;
        var restart = settings.Apply(requested);
        var changes = new List<string>();
        if (before.Address != settings.Current.Address) changes.Add("address");
        if (before.Port != requested.Port) changes.Add("port");
        if (before.MaxDevices != requested.MaxDevices) changes.Add("maxDevices");
        if (before.MaxSessionsPerDevice != requested.MaxSessionsPerDevice) changes.Add("maxSessionsPerDevice");
        if (before.RequireTls != requested.RequireTls) changes.Add("requireTls");
        if (!requested.RequireTls && before.RequireTls) log.LogWarning("TLS was switched off in the settings; this takes effect at the next start");
        audit.Write("settings", "", null, "ok", changes.Count == 0 ? "no change" : "changed " + string.Join(", ", changes) + (restart.Count > 0 ? "; restart needed for " + string.Join(", ", restart) : ""));
        return new SettingsUpdateResult(settings.Current, restart.Count > 0, restart);
    }

    // ---- backup and restore ----

    public byte[] Backup()
    {
        var bytes = DataBackup.Create(dataDir, store, Version, certificate?.Pin ?? "", time.GetUtcNow());
        audit.Write("backup", "", null, "ok", $"{store.Count} devices, {bytes.Length} bytes");
        return bytes;
    }

    public async Task<RestoreResult> RestoreAsync(byte[] zip)
    {
        DataBackup.Contents contents;
        DeviceRecord[] devices;
        X509Certificate2? newCert = null;
        try
        {
            contents = DataBackup.Read(zip);
            devices = DeviceStore.Parse(contents.Devices, "devices.json in the backup").Devices;
            if (contents.Settings is { } s) RuntimeSettings.Verify(s);
            if (contents.Pfx is { } pfx)
            {
                newCert = ServerCertificate.LoadFromBytes(pfx);
                if (!newCert.HasPrivateKey || newCert.GetECDsaPrivateKey() is null) throw new DataFormatException("The certificate in the backup has no usable key.");
            }
            if (contents.Address is { } a && ServerAddress.Validate(DataBackup.TextOf(a), true, options.PublicPort) is { } addressProblem)
                throw new DataFormatException("The address in the backup is not valid: " + addressProblem);
        }
        catch (DataFormatException ex)
        {
            audit.Write("restore", "", null, "refused", ex.Message);
            throw new AdminException(400, ex.Message);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            audit.Write("restore", "", null, "refused", "The certificate in the backup cannot be read.");
            throw new AdminException(400, "The certificate in the backup cannot be read.");
        }

        var oldPin = certificate?.Pin ?? "";
        // Keep what is there now, in case the restore was a mistake.
        var safety = Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(safety);
        File.WriteAllBytes(Path.Combine(safety, $"before-restore-{time.GetUtcNow():yyyyMMdd-HHmmss}.zip"), Backup());

        if (newCert is not null)
        {
            ServerCertificate.Install(dataDir, newCert.Export(X509ContentType.Pfx));
            certificate?.Replace(newCert);
        }
        store.ReplaceAll(devices);
        if (contents.Settings is { } settingsBytes) DataFiles.WriteAtomic(Path.Combine(dataDir, "settings.json"), settingsBytes);
        if (contents.Address is { } addressBytes) ServerAddress.Save(dataDir, DataBackup.TextOf(addressBytes));
        var restart = settings.Reload(options);

        var keep = devices.Where(d => !d.Blocked).Select(d => d.DeviceId).ToHashSet();
        foreach (var d in store.All().Where(d => d.Blocked).Select(d => d.DeviceId).ToList()) await hub.KickAsync(d, "Blocked by the server").ConfigureAwait(false);
        foreach (var id in hub.OnlineIds().Where(id => !keep.Contains(id)).ToList()) await hub.KickAsync(id, "Removed by the server").ConfigureAwait(false);

        var newPin = newCert is null ? oldPin : ServerCertificate.Pin(newCert);
        var changed = oldPin.Length > 0 && oldPin != newPin;
        var warning = changed ? "The backup holds a different server key. Devices that joined with the old key refuse this server until they join again with a new invite." : null;
        audit.Write("restore", "", null, "ok", $"{devices.Length} devices" + (changed ? "; the key pin changed" : "") + (restart.Count > 0 ? "; restart needed for " + string.Join(", ", restart) : ""));
        if (warning is not null) log.LogWarning("{Warning}", warning);
        return new RestoreResult(devices.Length, restart.Count > 0, oldPin, newPin, changed, warning);
    }

    // ---- certificate ----

    public CertificateRenewal RenewCertificate(bool newKey, bool confirmPinChange)
    {
        if (certificate is null) throw new AdminException(409, "TLS is switched off, so there is no certificate to renew.");
        if (newKey && !confirmPinChange)
            throw new AdminException(409, "A new key changes the server key pin: every device must join again with a new invite. Confirm that you want this and send the request again.");
        var oldPin = certificate.Pin;
        var renewed = ServerCertificate.Renew(dataDir, newKey);
        certificate.Replace(renewed);
        var newPin = certificate.Pin;
        var changed = oldPin != newPin;
        var warning = changed ? "The server key pin changed. Every device must join again with a new invite; the old pin is refused." : null;
        audit.Write("cert.renew", "", null, "ok", changed ? $"new key, pin {oldPin[..12]}... to {newPin[..12]}..." : "same key, new validity");
        if (warning is not null) log.LogWarning("{Warning}", warning);
        return new CertificateRenewal(oldPin, newPin, changed, new DateTimeOffset(renewed.NotAfter.ToUniversalTime(), TimeSpan.Zero), warning);
    }

    // ---- token ----

    public string RotateToken()
    {
        var value = (token ?? throw new AdminException(503, "The admin token is not available.")).Rotate();
        audit.Write("token.rotate", "", null, "ok");
        return value;
    }

    public IReadOnlyList<AuditEntry> Audit(DateTimeOffset? since, int max) => audit.Query(since, max);
}
