using System.Text.Json;
using Remotely.Core.Crypto;

namespace Remotely.Core.Lan.Consent;

/// <summary>A PC the person chose "Always allow" for. The identity is its public key (<see cref="DeviceId"/> is the SHA-256 of it); the name is only a label.</summary>
public sealed record RememberedDevice(string DeviceId, string PublicKey, string Name, DateTimeOffset AddedUtc, DateTimeOffset? LastSeenUtc, string? LastAddress);

internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T? TryRead<T>(string? path) where T : class
    {
        if (path is null || !File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Temp file then move, so a crash never leaves a half-written file. Returns false when the disk refused.</summary>
    public static bool TryWrite<T>(string? path, T value)
    {
        if (path is null) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, Options));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// The list of PCs that may reconnect without a new token. A PC is on it only after the person pressed "Always allow" for it, and leaves it on "Remove".
/// A PC that proves it holds the matching private key (see <see cref="LanResume"/>) is recognised; the name it gives is never trusted for this.
/// A damaged or unreadable file is treated as an empty list (fewer permissions, never more).
/// </summary>
public sealed class RememberedDevices
{
    public const int MaxEntries = 200;
    private sealed class FileShape { public int Version { get; set; } = 1; public List<RememberedDevice> Devices { get; set; } = []; }

    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, RememberedDevice> _items = new(StringComparer.Ordinal);

    /// <param name="path">JSON file, or null to keep the list in memory only (tests).</param>
    public RememberedDevices(string? path)
    {
        _path = path;
        var shape = JsonFile.TryRead<FileShape>(path);
        if (shape is null) return;
        foreach (var d in shape.Devices)
        {
            if (!IsConsistent(d)) continue;   // an entry whose id does not match its key is ignored, never trusted
            _items[d.DeviceId] = d;
        }
    }

    public event Action? Changed;

    public IReadOnlyList<RememberedDevice> All { get { lock (_gate) return [.. _items.Values.OrderByDescending(d => d.LastSeenUtc ?? d.AddedUtc)]; } }

    public bool Contains(string deviceId) { lock (_gate) return _items.ContainsKey(deviceId); }

    public bool TryGet(string deviceId, out RememberedDevice? device) { lock (_gate) return _items.TryGetValue(deviceId, out device); }

    private static bool IsConsistent(RememberedDevice d)
    {
        try { return d.DeviceId == DeviceIds.FromPublicKey(Convert.FromBase64String(d.PublicKey)); }
        catch (FormatException) { return false; }
    }

    /// <summary>Remembers a PC. Returns false when the key and id do not belong together, or the list is full.</summary>
    public bool Add(byte[] publicKey, string name, DateTimeOffset nowUtc)
    {
        var id = DeviceIds.FromPublicKey(publicKey);
        lock (_gate)
        {
            if (!_items.ContainsKey(id) && _items.Count >= MaxEntries) return false;
            _items[id] = new RememberedDevice(id, Convert.ToBase64String(publicKey), Clean(name), nowUtc, nowUtc, null);
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    public bool Remove(string deviceId)
    {
        lock (_gate)
        {
            if (!_items.Remove(deviceId)) return false;
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    public void RemoveAll()
    {
        lock (_gate) { _items.Clear(); Save(); }
        Changed?.Invoke();
    }

    public void Touch(string deviceId, string address, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(deviceId, out var d)) return;
            _items[deviceId] = d with { LastSeenUtc = nowUtc, LastAddress = address };
            Save();
        }
        Changed?.Invoke();
    }

    private static string Clean(string name)
    {
        var text = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length == 0 ? "Unnamed PC" : text.Length > 60 ? text[..60] : text;
    }

    private void Save() => JsonFile.TryWrite(_path, new FileShape { Devices = [.. _items.Values] });
}
