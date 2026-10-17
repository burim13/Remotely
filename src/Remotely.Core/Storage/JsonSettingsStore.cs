using System.Text.Json;
using System.Text.Json.Nodes;

namespace Remotely.Core.Storage;

/// <summary>Moves a settings file one schema version forward: <see cref="FromVersion"/> to <c>FromVersion + 1</c>. It edits the raw JSON, before the file is read into the settings class.</summary>
public sealed record SettingsMigration(int FromVersion, Action<JsonObject> Apply);

public enum LoadStatus
{
    /// <summary>No file yet; defaults are in use.</summary>
    Created,
    /// <summary>Read as is (same schema version).</summary>
    Loaded,
    /// <summary>An older file was brought forward; the original was copied to <see cref="SettingsLoadInfo.BackupPath"/>.</summary>
    Migrated,
    /// <summary>The file could not be read (not JSON, wrong shape, wrong value types). It was set aside as <c>.damaged</c> when possible and defaults are in use.</summary>
    Damaged,
    /// <summary>The file was written by a newer Remotely. It is read as far as this version understands it and copied to <see cref="SettingsLoadInfo.BackupPath"/> before it is ever overwritten.</summary>
    NewerThanThisVersion,
}

public sealed record SettingsLoadInfo(LoadStatus Status, int FileVersion, string? BackupPath);

/// <summary>Settings classes that fix up values read from a file (out-of-range numbers, unknown choices).</summary>
public interface ISettingsNormalize
{
    void Normalize();
}

/// <summary>
/// The file format shared by every store: a JSON object whose first property is <c>SchemaVersion</c>. A file without that property is version 0, which is what
/// Remotely 0.3.0 and 0.4.0 wrote for <c>viewer.json</c>. Writes go to a temporary file that is flushed to disk and then moved over the real one, so a crash
/// leaves either the old file or the new one, never half of each.
/// </summary>
public static class SettingsFile
{
    public const string VersionProperty = "SchemaVersion";

    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static T Read<T>(string path, int currentVersion, IEnumerable<SettingsMigration>? migrations, out SettingsLoadInfo info) where T : class, new()
    {
        if (!File.Exists(path))
        {
            info = new SettingsLoadInfo(LoadStatus.Created, currentVersion, null);
            return new T();
        }

        JsonObject root;
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(path)) is not JsonObject parsed) return Damaged<T>(path, currentVersion, out info);
            root = parsed;
        }
        catch (JsonException) { return Damaged<T>(path, currentVersion, out info); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            info = new SettingsLoadInfo(LoadStatus.Damaged, currentVersion, null);   // unreadable right now: do not move or overwrite what we could not read
            return new T();
        }

        var fileVersion = ReadVersion(root);
        string? backup = null;
        var status = LoadStatus.Loaded;
        if (fileVersion > currentVersion)
        {
            status = LoadStatus.NewerThanThisVersion;
            backup = CopyBackup(path, fileVersion);
        }
        else if (fileVersion < currentVersion)
        {
            status = LoadStatus.Migrated;
            backup = CopyBackup(path, fileVersion);
            var byVersion = (migrations ?? []).GroupBy(m => m.FromVersion).ToDictionary(g => g.Key, g => g.First());
            for (var v = fileVersion; v < currentVersion; v++)
            {
                if (byVersion.TryGetValue(v, out var step)) step.Apply(root);
                root[VersionProperty] = v + 1;
            }
        }

        try
        {
            var value = root.Deserialize<T>(Options) ?? new T();
            (value as ISettingsNormalize)?.Normalize();
            info = new SettingsLoadInfo(status, fileVersion, backup);
            return value;
        }
        catch (JsonException) { return Damaged<T>(path, currentVersion, out info); }
    }

    public static void Write<T>(string path, int version, T value) where T : class
    {
        var node = JsonSerializer.SerializeToNode(value, Options) as JsonObject ?? throw new InvalidOperationException("Settings must serialise to a JSON object.");
        var ordered = new JsonObject { [VersionProperty] = version };
        foreach (var (key, item) in node.ToList())
        {
            if (key == VersionProperty) continue;
            node.Remove(key);
            ordered[key] = item;
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            ordered.WriteTo(writer);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static int ReadVersion(JsonObject root)
    {
        try { return root[VersionProperty] is JsonValue v && v.TryGetValue<int>(out var n) && n >= 0 ? n : 0; }
        catch (InvalidOperationException) { return 0; }
    }

    private static T Damaged<T>(string path, int currentVersion, out SettingsLoadInfo info) where T : class, new()
    {
        string? moved;
        try
        {
            moved = path + ".damaged";
            File.Move(path, moved, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { moved = null; }
        info = new SettingsLoadInfo(LoadStatus.Damaged, currentVersion, moved);
        return new T();
    }

    private static string? CopyBackup(string path, int fileVersion)
    {
        var backup = $"{path}.v{fileVersion}.bak";
        try
        {
            if (!File.Exists(backup)) File.Copy(path, backup);   // the first copy of a version is the original; a later one must not replace it
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// <see cref="ISettingsStore{T}"/> over one JSON file. <see cref="Current"/> is the live object; <see cref="Update"/> changes it and writes the file.
/// A write that fails (disk full, file locked) does not throw: the change stays in memory and <see cref="LastSaveError"/> says why.
/// </summary>
public sealed class JsonSettingsStore<T> : ISettingsStore<T> where T : class, new()
{
    private readonly object _gate = new();
    private readonly int _version;
    private T _current;

    public JsonSettingsStore(string path, int schemaVersion, IEnumerable<SettingsMigration>? migrations = null)
    {
        Path = path;
        _version = schemaVersion;
        _current = SettingsFile.Read<T>(path, schemaVersion, migrations, out var info);
        LoadInfo = info;
        if (info.Status == LoadStatus.Migrated) TrySave();
    }

    public string Path { get; }
    public SettingsLoadInfo LoadInfo { get; }
    public Exception? LastSaveError { get; private set; }

    public T Current { get { lock (_gate) return _current; } }

    public event Action<T>? Changed;

    public void Update(Action<T> change)
    {
        T current;
        lock (_gate)
        {
            change(_current);
            (_current as ISettingsNormalize)?.Normalize();
            TrySave();
            current = _current;
        }
        Changed?.Invoke(current);
    }

    public void Reset()
    {
        T current;
        lock (_gate)
        {
            _current = new T();
            TrySave();
            current = _current;
        }
        Changed?.Invoke(current);
    }

    private void TrySave()
    {
        try
        {
            SettingsFile.Write(Path, _version, _current);
            LastSaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastSaveError = ex; }
    }
}
