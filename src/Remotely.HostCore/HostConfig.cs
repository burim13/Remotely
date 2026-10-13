using System.Text.Json;

namespace Remotely.HostCore;

/// <summary>Host settings file (host.json). The pairing code is single use and is cleared once the device is paired.</summary>
public sealed class HostConfig
{
    public string ServerUri { get; set; } = "";
    public string ServerPin { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    public string PairCode { get; set; } = "";
    /// <summary>Viewer device ids allowed to connect and control this PC. Empty means nobody.</summary>
    public List<string> TrustedViewers { get; set; } = [];
    /// <summary>Viewer device ids allowed to watch only (no input, clipboard or files).</summary>
    public List<string> ViewOnlyViewers { get; set; } = [];
    /// <summary>
    /// Family mode: accept any device that is paired with your server (the server only lets paired devices connect) instead of a list of ids.
    /// Viewers listed in <see cref="ViewOnlyViewers"/> still get view-only access. The trade-off: you rely on your own server's pairing instead of
    /// a per-PC list, so anyone who could pair a device with your server could reach this PC.
    /// </summary>
    public bool TrustAllPaired { get; set; }
    /// <summary>Permit ws:// (no encryption to the coordinator). Local testing only.</summary>
    public bool AllowInsecureTransport { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static HostConfig Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<HostConfig>(File.ReadAllBytes(path)) ?? new() : new();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    public bool IsTrusted(string viewerDeviceId) => TrustAllPaired || CanControl(viewerDeviceId) || IsViewOnlyListed(viewerDeviceId);

    public bool CanControl(string viewerDeviceId) =>
        !IsViewOnlyListed(viewerDeviceId) && (TrustAllPaired || TrustedViewers.Contains(viewerDeviceId, StringComparer.OrdinalIgnoreCase));

    private bool IsViewOnlyListed(string id) => ViewOnlyViewers.Contains(id, StringComparer.OrdinalIgnoreCase);
}
