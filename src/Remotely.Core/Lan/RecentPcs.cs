using System.Text.Json;

namespace Remotely.Core.Lan;

/// <summary>A PC this one connected to before. Holds a name and where it was; never a token or any secret.</summary>
public sealed record RecentPc(string Name, string Address, DateTimeOffset LastUsedUtc);

/// <summary>Recently used PCs, newest first. A14's address book can replace the implementation later.</summary>
public interface IRecentPcs
{
    IReadOnlyList<RecentPc> List();
    /// <summary>Remembers a successful connection. A PC already in the list (same address) moves to the top.</summary>
    void Record(string name, string address);
    void Remove(string address);
    void Clear();
}

public sealed class JsonRecentPcs : IRecentPcs
{
    public const int MaxEntries = 8;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    public JsonRecentPcs(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<RecentPc> List() { lock (_gate) return Load(); }

    public void Record(string name, string address)
    {
        address = address.Trim();
        if (address.Length == 0) return;
        lock (_gate)
        {
            var items = Load().Where(p => !string.Equals(p.Address, address, StringComparison.OrdinalIgnoreCase)).ToList();
            items.Insert(0, new RecentPc(name.Trim(), address, _now()));
            Save(items.Take(MaxEntries).ToList());
        }
    }

    public void Remove(string address)
    {
        lock (_gate) Save(Load().Where(p => !string.Equals(p.Address, address, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    public void Clear() { lock (_gate) Save([]); }

    private List<RecentPc> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var items = JsonSerializer.Deserialize<List<RecentPc>>(File.ReadAllBytes(_path)) ?? [];
            return [.. items.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Address)).OrderByDescending(p => p.LastUsedUtc).Take(MaxEntries)];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }   // unreadable or damaged: start empty, never crash the Connect screen
    }

    private void Save(List<RecentPc> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(items, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* history is a convenience; losing a write must not break connecting */ }
    }
}
