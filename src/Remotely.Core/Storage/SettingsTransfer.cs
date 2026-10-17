using System.Text.Json;
using System.Text.Json.Nodes;

namespace Remotely.Core.Storage;

/// <summary>What an import did, by setting id.</summary>
public sealed record SettingsImportResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Unknown, IReadOnlyList<string> Rejected, IReadOnlyList<string> LockedByPolicy);

/// <summary>
/// Settings export and import. The file holds only catalogued settings by id. It never holds the server address, the pin, pairing, the device key, the first-run
/// state, the window position, the address book, or this PC's name (see <see cref="SettingDef.Portable"/>), so it is safe to copy to another PC.
/// </summary>
public static class SettingsTransfer
{
    public const string Format = "remotely-settings";
    public const int Version = 1;

    public static string Export(SettingsSet set)
    {
        var values = new JsonObject();
        foreach (var d in SettingsCatalog.All.Where(d => d.Portable))
            values[d.Id] = d.Get(set) switch { bool b => JsonValue.Create(b), int i => JsonValue.Create(i), long l => JsonValue.Create(l), var o => JsonValue.Create(o.ToString()) };
        var root = new JsonObject { ["Format"] = Format, ["Version"] = Version, ["Settings"] = values };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Applies a file made by <see cref="Export"/>. Nothing is changed when the file is not a Remotely settings file or comes from a newer format (<see cref="FormatException"/>).
    /// A single bad value is skipped and reported; the rest is applied. Settings an administrator's policy forces are skipped.
    /// </summary>
    public static SettingsImportResult Import(string json, SettingsSet set, PolicyOverrides policy)
    {
        const string notOurs = "This is not a Remotely settings file.";
        JsonObject values;
        int version;
        try
        {
            var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException(notOurs);
            if (root["Format"]?.GetValue<string>() != Format || root["Settings"] is not JsonObject found) throw new FormatException(notOurs);
            values = found;
            version = root["Version"]?.GetValue<int>() ?? 0;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { throw new FormatException(notOurs); }
        if (version < 1) throw new FormatException(notOurs);
        if (version > Version) throw new FormatException("This settings file comes from a newer Remotely. Update Remotely to read it.");

        var applied = new List<string>();
        var unknown = new List<string>();
        var rejected = new List<string>();
        var locked = new List<string>();
        foreach (var (id, node) in values)
        {
            var def = SettingsCatalog.Find(id);
            if (def is null) { unknown.Add(id); continue; }
            if (!def.Portable) { rejected.Add(def.Id); continue; }
            if (!TryRead(node, out var raw) || !def.TryNormalize(raw, out var value)) { rejected.Add(def.Id); continue; }
            if (def.ForcedBy?.Invoke(policy) is not null) { locked.Add(def.Id); continue; }
            def.Set(set, value);
            applied.Add(def.Id);
        }
        return new SettingsImportResult(applied, unknown, rejected, locked);
    }

    private static bool TryRead(JsonNode? node, out object? value)
    {
        value = null;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var b)) { value = b; return true; }
        if (v.TryGetValue<long>(out var l)) { value = l; return true; }
        if (v.TryGetValue<string>(out var s)) { value = s; return true; }
        return false;
    }
}
