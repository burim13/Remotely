using System.Text.Json;

namespace Remotely.HostCore;

/// <summary>Host settings file (host.json). The pairing code is single use and is cleared once the device is paired.</summary>
public sealed class HostConfig
{
    public string ServerUri { get; set; } = "";
    public string ServerPin { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    public string PairCode { get; set; } = "";
    /// <summary>Viewer device ids allowed to connect. Empty means nobody.</summary>
    public List<string> TrustedViewers { get; set; } = [];
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

    public bool IsTrusted(string viewerDeviceId) => TrustedViewers.Contains(viewerDeviceId, StringComparer.OrdinalIgnoreCase);
}
