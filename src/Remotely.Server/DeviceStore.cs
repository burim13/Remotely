using System.Text.Json;

namespace Remotely.Server;

public sealed record DeviceRecord(string DeviceId, string Name, byte[] PublicKey, bool CanHost, DateTimeOffset CreatedUtc);

/// <summary>Paired devices, persisted as a JSON file written atomically. Holds public keys only.</summary>
public sealed class DeviceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceRecord> _devices;

    public DeviceStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "devices.json");
        _devices = File.Exists(_path)
            ? (JsonSerializer.Deserialize<DeviceRecord[]>(File.ReadAllBytes(_path)) ?? []).ToDictionary(d => d.DeviceId)
            : [];
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

    private void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_devices.Values.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}
