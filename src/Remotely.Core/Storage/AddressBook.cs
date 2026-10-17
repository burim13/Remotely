using System.Text.Json;
using System.Text.Json.Nodes;
using Remotely.Core.Lan;

namespace Remotely.Core.Storage;

public static class AddressBookTags
{
    /// <summary>The names map to the theme brushes Error, Warning, Connected, Info and Offline, so a tag follows light, dark and high-contrast themes.</summary>
    public static readonly string[] Names = ["red", "amber", "green", "blue", "gray"];
}

public sealed record AddressBookImportResult(int Added, int Updated, int Skipped);

/// <summary>The shape of <c>address-book.json</c>.</summary>
public sealed class AddressBookFile
{
    public List<AddressEntry> Entries { get; set; } = [];
}

/// <summary>
/// The saved PCs, kept in <c>address-book.json</c> (schema versioned, written atomically). An entry never holds a token or any secret: only a name, where the PC is,
/// and the person's own notes. Two entries cannot point at the same target (compared without regard to case); adding one that exists returns the existing entry.
/// </summary>
public sealed class JsonAddressBook : IAddressBook
{
    public const int SchemaVersion = 1;
    public const string FileName = "address-book.json";
    public const string ExportFormat = "remotely-address-book";
    public const int MaxEntries = 2000;
    public const int MaxName = 100, MaxTarget = 300, MaxGroup = 60, MaxNotes = 2000;

    private readonly object _gate = new();
    private readonly JsonSettingsStore<AddressBookFile> _store;
    private readonly Func<DateTimeOffset> _now;

    public JsonAddressBook(string path, Func<DateTimeOffset>? now = null)
    {
        _store = new JsonSettingsStore<AddressBookFile>(path, SchemaVersion);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Clean();
    }

    public SettingsLoadInfo LoadInfo => _store.LoadInfo;

    public IReadOnlyList<AddressEntry> All { get { lock (_gate) return [.. _store.Current.Entries]; } }

    public event Action? Changed;

    /// <summary>Group names in use, sorted.</summary>
    public IReadOnlyList<string> Groups => [.. All.Select(e => e.Group).Where(g => !string.IsNullOrEmpty(g)).Select(g => g!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];

    public AddressEntry Add(string name, string target, string? group = null)
    {
        AddressEntry result;
        lock (_gate)
        {
            target = Limit(target, MaxTarget) ?? "";
            if (target.Length == 0) throw new ArgumentException("A saved PC needs an address.", nameof(target));
            if (FindByTarget(target) is { } existing) return existing;
            if (_store.Current.Entries.Count >= MaxEntries) throw new InvalidOperationException("The address book is full.");
            var entry = new AddressEntry(NewId(), Limit(name, MaxName) ?? target, target, Limit(group, MaxGroup), null, false, null);
            result = entry;
            _store.Update(f => f.Entries.Add(entry));
        }
        Changed?.Invoke();
        return result;
    }

    public void Update(AddressEntry entry)
    {
        lock (_gate)
        {
            var index = _store.Current.Entries.FindIndex(e => e.Id == entry.Id);
            if (index < 0) throw new KeyNotFoundException("That saved PC no longer exists.");
            var clean = Sanitize(entry);
            if (clean is null) throw new ArgumentException("A saved PC needs an address.", nameof(entry));
            if (_store.Current.Entries.Any(e => e.Id != clean.Id && SameTarget(e.Target, clean.Target))) throw new InvalidOperationException("Another saved PC already uses that address.");
            _store.Update(f => f.Entries[index] = clean);
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_store.Current.Entries.All(e => e.Id != id)) return;
            _store.Update(f => f.Entries.RemoveAll(e => e.Id == id));
        }
        Changed?.Invoke();
    }

    public void MarkUsed(string id)
    {
        lock (_gate)
        {
            var index = _store.Current.Entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            _store.Update(f => f.Entries[index] = f.Entries[index] with { LastUsedUtc = _now() });
        }
        Changed?.Invoke();
    }

    /// <summary>The whole book as the JSON file offered by "Export".</summary>
    public string Export()
    {
        var entries = JsonSerializer.SerializeToNode(All, SettingsFile.Options);
        var root = new JsonObject { ["Format"] = ExportFormat, ["Version"] = 1, ["Entries"] = entries };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void Import(string json) => ImportWithResult(json);

    /// <summary>
    /// Merges a file made by <see cref="Export"/>. An entry whose address is already saved updates that entry (name, group, notes, favourite, colour; the later last-used time wins);
    /// any other entry is added. Entries without an address are skipped and counted. A file that is not an address-book export, or from a newer format, changes nothing and throws <see cref="FormatException"/>.
    /// </summary>
    public AddressBookImportResult ImportWithResult(string json)
    {
        const string notOurs = "This is not a Remotely address book file.";
        List<AddressEntry> incoming;
        try
        {
            var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException(notOurs);
            if (root["Format"]?.GetValue<string>() != ExportFormat) throw new FormatException(notOurs);
            var v = root["Version"]?.GetValue<int>() ?? 0;
            if (v < 1) throw new FormatException(notOurs);
            if (v > 1) throw new FormatException("This address book file comes from a newer Remotely. Update Remotely to read it.");
            incoming = root["Entries"]?.Deserialize<List<AddressEntry>>(SettingsFile.Options) ?? throw new FormatException(notOurs);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new FormatException(notOurs); }

        int added = 0, updated = 0, skipped = 0;
        lock (_gate)
        {
            var entries = new List<AddressEntry>(_store.Current.Entries);
            foreach (var raw in incoming)
            {
                var clean = raw is null ? null : Sanitize(raw with { Id = raw.Id ?? "" });
                if (clean is null) { skipped++; continue; }
                var index = entries.FindIndex(e => SameTarget(e.Target, clean.Target));
                if (index >= 0)
                {
                    var old = entries[index];
                    entries[index] = clean with { Id = old.Id, Target = old.Target, LastUsedUtc = Later(old.LastUsedUtc, clean.LastUsedUtc) };
                    updated++;
                }
                else if (entries.Count >= MaxEntries) skipped++;
                else
                {
                    entries.Add(entries.Any(e => e.Id == clean.Id) || clean.Id.Length == 0 ? clean with { Id = NewId() } : clean);
                    added++;
                }
            }
            _store.Update(f => f.Entries = entries);
        }
        Changed?.Invoke();
        return new AddressBookImportResult(added, updated, skipped);
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private AddressEntry? FindByTarget(string target) => _store.Current.Entries.FirstOrDefault(e => SameTarget(e.Target, target));

    private static bool SameTarget(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string? Limit(string? text, int max)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > max ? text[..max] : text;
    }

    /// <summary>Trims and limits every field. Null when there is no address.</summary>
    private static AddressEntry? Sanitize(AddressEntry e)
    {
        var target = Limit(e.Target, MaxTarget);
        if (target is null) return null;
        var color = e.ColorTag is { } c && AddressBookTags.Names.Contains(c, StringComparer.OrdinalIgnoreCase) ? c.ToLowerInvariant() : null;
        return new AddressEntry(Limit(e.Id, 64) ?? "", Limit(e.Name, MaxName) ?? target, target, Limit(e.Group, MaxGroup), Limit(e.Notes, MaxNotes), e.Favorite, e.LastUsedUtc, color);
    }

    /// <summary>A hand-edited or damaged file can hold entries without an address or with repeated ids; drop and repair them in memory (the file is rewritten on the next change).</summary>
    private void Clean()
    {
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cleaned = new List<AddressEntry>();
            foreach (var raw in _store.Current.Entries)
            {
                var e = raw is null ? null : Sanitize(raw);
                if (e is null || cleaned.Any(x => SameTarget(x.Target, e.Target))) continue;
                if (e.Id.Length == 0 || !seen.Add(e.Id)) e = e with { Id = NewId() };
                seen.Add(e.Id);
                cleaned.Add(e);
            }
            _store.Current.Entries = cleaned;
        }
    }
}

/// <summary>
/// <see cref="IRecentPcs"/> on top of the address book, so the Connect screen's "recent" list and the address book are one list. A recent PC is an entry with a last-used time.
/// Forgetting a recent PC clears that time; the entry itself is deleted only when it carries nothing the person wrote (no group, notes, colour or favourite).
/// </summary>
public sealed class AddressBookRecentPcs(JsonAddressBook book) : IRecentPcs
{
    public const int MaxEntries = JsonRecentPcs.MaxEntries;

    public IReadOnlyList<RecentPc> List() => [.. book.All.Where(e => e.LastUsedUtc is not null).OrderByDescending(e => e.LastUsedUtc).Take(MaxEntries).Select(e => new RecentPc(e.Name, e.Target, e.LastUsedUtc!.Value))];

    public void Record(string name, string address)
    {
        address = address.Trim();
        if (address.Length == 0) return;
        var entry = book.Add(string.IsNullOrWhiteSpace(name) ? address : name.Trim(), address);
        book.MarkUsed(entry.Id);
    }

    public void Remove(string address)
    {
        var entry = book.All.FirstOrDefault(e => string.Equals(e.Target, address.Trim(), StringComparison.OrdinalIgnoreCase));
        if (entry is not null) Forget(entry);
    }

    public void Clear()
    {
        foreach (var e in book.All.Where(e => e.LastUsedUtc is not null)) Forget(e);
    }

    private void Forget(AddressEntry e)
    {
        if (e.Group is null && e.Notes is null && e.ColorTag is null && !e.Favorite) book.Remove(e.Id);
        else book.Update(e with { LastUsedUtc = null });
    }

    /// <summary>Copies the old <c>recent-pcs.json</c> into the book (once: PCs already in the book are left alone). Returns how many were added.</summary>
    public static int ImportLegacy(JsonAddressBook book, IRecentPcs legacy)
    {
        var added = 0;
        foreach (var pc in legacy.List().Reverse())
        {
            if (book.All.Any(e => string.Equals(e.Target, pc.Address, StringComparison.OrdinalIgnoreCase))) continue;
            var entry = book.Add(pc.Name, pc.Address);
            book.Update(entry with { LastUsedUtc = pc.LastUsedUtc });
            added++;
        }
        return added;
    }
}
