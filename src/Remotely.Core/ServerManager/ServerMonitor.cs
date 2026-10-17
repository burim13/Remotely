using Remotely.Core.Admin;

namespace Remotely.Core.ServerManager;

public enum TrayStatus { Unknown, Running, Attention, Starting, Stopped }

public sealed record MonitorSnapshot(DateTimeOffset AtUtc, ServerStatus? Status, IReadOnlyList<AdminDevice> Devices, ServiceState Service, string? Error)
{
    public bool Reachable => Status is not null;
}

/// <summary>
/// Polls the server (through <see cref="IServerAdmin"/>) and the Windows service, keeps the latest picture, and says which devices are new since the last look.
/// The first successful look only sets the baseline, so opening the Manager never announces every existing device as new.
/// </summary>
public sealed class ServerMonitor(IServerAdmin admin, ServiceQuery? service = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private HashSet<string>? _known;

    public MonitorSnapshot? Latest { get; private set; }

    /// <summary>Raised after every refresh, on whatever thread called <see cref="RefreshAsync"/>.</summary>
    public event Action<MonitorSnapshot>? Updated;

    /// <summary>Raised with the devices that appeared since the previous successful refresh.</summary>
    public event Action<IReadOnlyList<AdminDevice>>? NewDevices;

    public async Task<MonitorSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        var serviceTask = service is null ? Task.FromResult(ServiceState.Unknown) : service.QueryAsync(ct);
        ServerStatus? status = null;
        IReadOnlyList<AdminDevice> devices = Latest?.Devices ?? [];
        string? error = null;
        try
        {
            status = await admin.GetStatusAsync(ct);
            devices = await admin.ListDevicesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = null;
            error = ex.Message;
        }
        var snapshot = new MonitorSnapshot(_clock.GetUtcNow(), status, devices, await serviceTask, error);
        Latest = snapshot;

        IReadOnlyList<AdminDevice> added = [];
        if (snapshot.Reachable)
        {
            if (_known is null) _known = devices.Select(d => d.DeviceId).ToHashSet();
            else
            {
                added = devices.Where(d => !_known.Contains(d.DeviceId)).ToList();
                foreach (var d in added) _known.Add(d.DeviceId);
            }
        }
        Updated?.Invoke(snapshot);
        if (added.Count > 0) NewDevices?.Invoke(added);
        return snapshot;
    }

    public static TrayStatus TrayStatusOf(MonitorSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null) return TrayStatus.Unknown;
        if (snapshot.Reachable)
        {
            var cert = snapshot.Status!.TlsEnabled ? ServerInfo.AssessCertificate(snapshot.Status.CertificateExpiresUtc, now) : CertificateHealth.Ok;
            return cert == CertificateHealth.Ok ? TrayStatus.Running : TrayStatus.Attention;
        }
        return snapshot.Service switch
        {
            ServiceState.Stopped or ServiceState.NotInstalled or ServiceState.StopPending => TrayStatus.Stopped,
            ServiceState.StartPending or ServiceState.Running => TrayStatus.Starting,
            _ => TrayStatus.Unknown,
        };
    }
}

/// <summary>Draws the tray icon: a dark disc with a ring of the status colour. Returns straight (not premultiplied) BGRA, the layout Windows wants for an icon bitmap.</summary>
public static class TrayIconRenderer
{
    // Portico palette: connected green, accent amber, error red, neutral grey.
    public static (byte R, byte G, byte B) ColourOf(TrayStatus status) => status switch
    {
        TrayStatus.Running => (0x6F, 0xCF, 0x97),
        TrayStatus.Attention or TrayStatus.Starting => (0xF7, 0xBE, 0x55),
        TrayStatus.Stopped => (0xFF, 0x9B, 0x8A),
        _ => (0x7F, 0x8A, 0x93),
    };

    public static byte[] RenderBgra(int size, TrayStatus status)
    {
        var (r, g, b) = ColourOf(status);
        var pixels = new byte[size * size * 4];
        double c = (size - 1) / 2.0, outer = size / 2.0 - 0.25, ring = outer * 0.72;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                var cover = Math.Clamp(outer - d + 0.5, 0, 1);   // 1 inside the disc, fading over one pixel at the edge
                if (cover <= 0) continue;
                var inner = Math.Clamp(ring - d + 0.5, 0, 1);    // 1 in the middle (the status colour), 0 on the dark rim
                var i = (y * size + x) * 4;
                pixels[i] = (byte)Math.Round(0x27 + (b - 0x27) * inner);
                pixels[i + 1] = (byte)Math.Round(0x21 + (g - 0x21) * inner);
                pixels[i + 2] = (byte)Math.Round(0x1C + (r - 0x1C) * inner);
                pixels[i + 3] = (byte)Math.Round(255 * cover);
            }
        return pixels;
    }
}
