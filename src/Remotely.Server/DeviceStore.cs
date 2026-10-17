using System.Text.Json;

namespace Remotely.Server;

/// <summary>A data file cannot be used: damaged, or written by a newer server than this one.</summary>
public sealed class DataFormatException(string message) : Exception(message);

public sealed record DeviceRecord(string DeviceId, string Name, byte[] PublicKey, bool CanHost, DateTimeOffset CreatedUtc,
    bool Blocked = false, DateTimeOffset? LastSeenUtc = null);

/// <summary>
/// Paired devices, persisted as a JSON file written atomically. Holds public keys only.
/// Schema 1 (servers up to and including 0.4.0) is a bare JSON array; schema 2 is <c>{"schemaVersion":2,"devices":[...]}</c> and adds blocking and last-seen.
/// A schema 1 file is migrated on load (the original is kept next to it as devices.json.v1.bak); a file from a newer server is refused, never overwritten.
/// </summary>
public sealed class DeviceStore
{
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions LegacyRead = new() { PropertyNameCaseInsensitive = true };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceRecord> _devices = [];
    private bool _dirty;

    public DeviceStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "devices.json");
        if (!File.Exists(_path)) return;

        var (devices, version) = Parse(File.ReadAllBytes(_path), _path);
        foreach (var d in devices) _devices[d.DeviceId] = d;
        if (version < SchemaVersion)
        {
            var backup = $"{_path}.v{version}.bak";
            if (!File.Exists(backup)) File.Copy(_path, backup);
            Save();
        }
    }

    /// <summary>Reads a devices file of any schema this server knows. Throws <see cref="DataFormatException"/> with a plain message otherwise.</summary>
    public static (DeviceRecord[] Devices, int Version) Parse(byte[] json, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Array:
                    return (Clean(root.Deserialize<DeviceRecord[]>(LegacyRead), source), 1);
                case JsonValueKind.Object:
                    if (!root.TryGetProperty("schemaVersion", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
                        throw new DataFormatException($"{source} has no schema version.");
                    if (version > SchemaVersion)
                        throw new DataFormatException($"{source} was written by a newer Remotely server (data schema {version}; this server understands up to {SchemaVersion}). Update the server instead of opening it with an older one.");
                    if (version < 2 || !root.TryGetProperty("devices", out var list))
                        throw new DataFormatException($"{source} has an unknown layout.");
                    return (Clean(list.Deserialize<DeviceRecord[]>(WriteOptions), source), version);
                default:
                    throw new DataFormatException($"{source} is not a device list.");
            }
        }
        catch (JsonException ex)
        {
            throw new DataFormatException($"{source} is damaged ({ex.Message}). Restore it from a backup; the server will not overwrite it.");
        }
    }

    private static DeviceRecord[] Clean(DeviceRecord[]? records, string source)
    {
        if (records is null) throw new DataFormatException($"{source} is empty or not a device list.");
        foreach (var r in records)
            if (string.IsNullOrEmpty(r.DeviceId) || r.PublicKey is null) throw new DataFormatException($"{source} contains a device without an id or key.");
        return [.. records.Select(r => r.Name is null ? r with { Name = "Unnamed device" } : r)];
    }

    public int Count
    {
        get { lock (_gate) return _devices.Count; }
    }

    public DeviceRecord? Find(string deviceId)
    {
        lock (_gate) return _devices.GetValueOrDefault(deviceId);
    }

    public IReadOnlyList<DeviceRecord> All()
    {
        lock (_gate) return [.. _devices.Values];
    }

    public void Add(DeviceRecord device)
    {
        lock (_gate)
        {
            _devices[device.DeviceId] = device;
            Save();
        }
    }

    public bool Remove(string deviceId)
    {
        lock (_gate)
        {
            var removed = _devices.Remove(deviceId);
            if (removed) Save();
            return removed;
        }
    }

    /// <summary>Changes one record and saves. Returns the new record, or null when the device is unknown.</summary>
    public DeviceRecord? Update(string deviceId, Func<DeviceRecord, DeviceRecord> change)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var current)) return null;
            var updated = change(current);
            _devices[deviceId] = updated;
            Save();
            return updated;
        }
    }

    /// <summary>Remembers when a device was last on the server. Kept in memory and written by <see cref="Flush"/>, so a busy server does not rewrite the file on every connect.</summary>
    public void Touch(string deviceId, DateTimeOffset when)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var d)) return;
            _devices[deviceId] = d with { LastSeenUtc = when };
            _dirty = true;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_dirty) Save();
        }
    }

    /// <summary>Replaces everything (used by restore).</summary>
    public void ReplaceAll(IEnumerable<DeviceRecord> devices)
    {
        lock (_gate)
        {
            _devices.Clear();
            foreach (var d in devices) _devices[d.DeviceId] = d;
            Save();
        }
    }

    /// <summary>The file content as it would be saved now (used by backup).</summary>
    public byte[] Snapshot()
    {
        lock (_gate) return Serialize();
    }

    private byte[] Serialize() =>
        JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = SchemaVersion, devices = _devices.Values.ToArray() }, WriteOptions);

    private void Save()
    {
        DataFiles.WriteAtomic(_path, Serialize());
        _dirty = false;
    }
}
