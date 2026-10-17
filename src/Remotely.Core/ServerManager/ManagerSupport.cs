using Remotely.Core.Admin;
using Remotely.Core.Protocol;

namespace Remotely.Core.ServerManager;

/// <summary>The Manager's command line. <c>--fake</c> uses invented data, <c>--show Page</c> opens a page by class name, <c>--minimized</c> starts in the tray, <c>--selftest file</c> runs <see cref="ManagerSelfTest"/>.</summary>
public sealed record ManagerArgs(bool Fake, string? Show, bool Minimized, string? SelfTestFile, string? RevokeDeviceId)
{
    public static ManagerArgs Parse(IReadOnlyList<string> argv)
    {
        string? Value(string name)
        {
            for (var i = 0; i < argv.Count - 1; i++) if (argv[i] == name) return argv[i + 1];
            return null;
        }
        return new ManagerArgs(argv.Contains("--fake"), Value("--show"), argv.Contains("--minimized"), Value("--selftest"), Value("--revoke-device"));
    }
}

public enum ServerHeadline { Running, NotAnswering, Stopped, NotInstalled, Unknown }

public static class Headline
{
    public static ServerHeadline Of(MonitorSnapshot? snapshot)
    {
        if (snapshot is null) return ServerHeadline.Unknown;
        if (snapshot.Reachable) return ServerHeadline.Running;
        return snapshot.Service switch
        {
            ServiceState.NotInstalled => ServerHeadline.NotInstalled,
            ServiceState.Stopped or ServiceState.StopPending => ServerHeadline.Stopped,
            ServiceState.Running or ServiceState.StartPending => ServerHeadline.NotAnswering,
            _ => ServerHeadline.Unknown,
        };
    }
}

/// <summary>
/// What <c>--selftest</c> does through <see cref="IServerAdmin"/>: read the status, create an invite and check it parses, list it, list devices, and (when a device id
/// is given, for example one a probe has just paired) revoke that device and check it is gone from the list. That the server then refuses the revoked device is checked
/// by whoever paired it, because only the device can try to connect.
/// </summary>
public static class ManagerSelfTest
{
    public static async Task<bool> RunAsync(IServerAdmin admin, string? revokeDeviceId, Action<string> log, CancellationToken ct = default)
    {
        var ok = true;
        async Task Step(string name, Func<Task<string>> body)
        {
            try { log($"PASS {name}: {await body().ConfigureAwait(false)}"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { ok = false; log($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
        }

        await Step("status", async () =>
        {
            var s = await admin.GetStatusAsync(ct).ConfigureAwait(false);
            return $"version {s.Version}, {s.Address}:{s.Port}, {s.DevicesTotal} device(s)";
        }).ConfigureAwait(false);

        InviteInfo? invite = null;
        await Step("create invite", async () =>
        {
            invite = await admin.CreateInviteAsync(TimeSpan.FromHours(1), 3, ct).ConfigureAwait(false);
            if (!Invite.TryParse(invite.InviteText, out _, out var error)) throw new InvalidDataException("The invite does not parse: " + error);
            return "created and parsed";
        }).ConfigureAwait(false);

        await Step("list invites", async () =>
        {
            var all = await admin.ListInvitesAsync(ct).ConfigureAwait(false);
            if (invite is null || all.All(i => i.Id != invite.Id)) throw new InvalidOperationException("The new invite is not in the list.");
            return $"{all.Count} invite(s)";
        }).ConfigureAwait(false);

        await Step("list devices", async () => $"{(await admin.ListDevicesAsync(ct).ConfigureAwait(false)).Count} device(s)").ConfigureAwait(false);
        await Step("audit", async () => $"{(await admin.GetAuditAsync(null, 50, ct).ConfigureAwait(false)).Count} entr(ies)").ConfigureAwait(false);

        if (revokeDeviceId is not null)
        {
            await Step("revoke device", async () =>
            {
                var before = await admin.ListDevicesAsync(ct).ConfigureAwait(false);
                if (before.All(d => d.DeviceId != revokeDeviceId)) throw new InvalidOperationException("The device is not in the list.");
                await admin.RevokeDeviceAsync(revokeDeviceId, ct).ConfigureAwait(false);
                var after = await admin.ListDevicesAsync(ct).ConfigureAwait(false);
                if (after.Any(d => d.DeviceId == revokeDeviceId)) throw new InvalidOperationException("The device is still listed after the revoke.");
                return "revoked and gone from the list";
            }).ConfigureAwait(false);
        }

        if (invite is not null)
            await Step("revoke invite", async () =>
            {
                await admin.RevokeInviteAsync(invite.Id, ct).ConfigureAwait(false);
                return "revoked";
            }).ConfigureAwait(false);

        log(ok ? "PASS" : "FAIL");
        return ok;
    }
}
