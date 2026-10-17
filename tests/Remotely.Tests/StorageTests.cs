using System.Text.Json;
using System.Text.Json.Nodes;
using Remotely.Core.Lan;
using Remotely.Core.Storage;

namespace Remotely.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remotely-storage-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class StorageTests
{
    public sealed class Sample
    {
        public string Name { get; set; } = "default";
        public int Count { get; set; } = 1;
    }

    public sealed class SampleV2
    {
        public string Title { get; set; } = "";
    }

    // What Remotely 0.3.0 and 0.4.0 wrote: the six values of the viewer settings class (identical at commits 8cc392b, d8e4166 and 0d0e4dd) plus the read-only IsConfigured.
    private const string Viewer040 = """
        {
          "ServerUri": "wss://server.example:8443",
          "ServerPin": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
          "DeviceName": "OFFICE-PC",
          "Theme": "dark",
          "SyncClipboard": false,
          "AllowInsecureTransport": false,
          "IsConfigured": true
        }
        """;

    private sealed class LegacyViewerSettings
    {
        public string ServerUri { get; set; } = "";
        public string ServerPin { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string Theme { get; set; } = "system";
        public bool SyncClipboard { get; set; } = true;
        public bool AllowInsecureTransport { get; set; }
        public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUri);
    }

    [Fact]
    public void The_captured_sample_is_what_the_old_class_wrote()
    {
        var old = new LegacyViewerSettings
        {
            ServerUri = "wss://server.example:8443",
            ServerPin = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            DeviceName = "OFFICE-PC", Theme = "dark", SyncClipboard = false,
        };
        var written = JsonSerializer.Serialize(old, new JsonSerializerOptions { WriteIndented = true });
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(written), JsonNode.Parse(Viewer040)));
    }

    [Fact]
    public void Store_starts_with_defaults_and_writes_nothing_until_changed()
    {
        using var dir = new TempDir();
        var store = new JsonSettingsStore<Sample>(dir.File("s.json"), 1);
        Assert.Equal(LoadStatus.Created, store.LoadInfo.Status);
        Assert.Equal("default", store.Current.Name);
        Assert.False(File.Exists(dir.File("s.json")));
    }

    [Fact]
    public void Update_persists_raises_Changed_and_leaves_no_temp_file()
    {
        using var dir = new TempDir();
        var store = new JsonSettingsStore<Sample>(dir.File("s.json"), 3);
        Sample? seen = null;
        store.Changed += s => seen = s;
        store.Update(s => s.Name = "changed");
        Assert.Same(store.Current, seen);
        Assert.False(File.Exists(dir.File("s.json.tmp")));
        var again = new JsonSettingsStore<Sample>(dir.File("s.json"), 3);
        Assert.Equal("changed", again.Current.Name);
        Assert.Equal(LoadStatus.Loaded, again.LoadInfo.Status);
        var root = JsonNode.Parse(File.ReadAllText(dir.File("s.json")))!.AsObject();
        Assert.Equal("SchemaVersion", root.First().Key);
        Assert.Equal(3, (int)root["SchemaVersion"]!);
    }

    [Fact]
    public void A_failed_write_does_not_throw_and_is_reported()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("blocker"), "x");
        var store = new JsonSettingsStore<Sample>(System.IO.Path.Combine(dir.File("blocker"), "s.json"), 1);
        store.Update(s => s.Count = 5);
        Assert.NotNull(store.LastSaveError);
        Assert.Equal(5, store.Current.Count);
    }

    [Fact]
    public void Reset_returns_defaults_and_writes_them()
    {
        using var dir = new TempDir();
        var store = new JsonSettingsStore<Sample>(dir.File("s.json"), 1);
        store.Update(s => s.Name = "x");
        store.Reset();
        Assert.Equal("default", store.Current.Name);
        Assert.Equal("default", new JsonSettingsStore<Sample>(dir.File("s.json"), 1).Current.Name);
    }

    [Fact]
    public void A_damaged_file_is_set_aside_and_defaults_are_used()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("s.json"), "{ not json");
        var store = new JsonSettingsStore<Sample>(dir.File("s.json"), 1);
        Assert.Equal(LoadStatus.Damaged, store.LoadInfo.Status);
        Assert.Equal("default", store.Current.Name);
        Assert.True(File.Exists(dir.File("s.json.damaged")));
        Assert.False(File.Exists(dir.File("s.json")));
    }

    [Fact]
    public void A_file_with_a_wrong_value_type_counts_as_damaged()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("s.json"), """{ "Count": "many" }""");
        Assert.Equal(LoadStatus.Damaged, new JsonSettingsStore<Sample>(dir.File("s.json"), 1).LoadInfo.Status);
    }

    [Fact]
    public void A_file_from_a_newer_version_is_backed_up_before_it_is_overwritten()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("s.json"), """{ "SchemaVersion": 9, "Name": "future", "Extra": 1 }""");
        var store = new JsonSettingsStore<Sample>(dir.File("s.json"), 1);
        Assert.Equal(LoadStatus.NewerThanThisVersion, store.LoadInfo.Status);
        Assert.Equal("future", store.Current.Name);
        store.Update(s => s.Name = "mine");
        Assert.Contains("\"Extra\": 1", File.ReadAllText(dir.File("s.json.v9.bak")));
    }

    [Fact]
    public void Migrations_run_in_order_and_the_original_is_kept()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("s.json"), """{ "Name": "old" }""");
        var order = new List<int>();
        var migrations = new[]
        {
            new SettingsMigration(1, o => { order.Add(1); o["Count"] = (int)o["Count"]! + 10; }),
            new SettingsMigration(0, o => { order.Add(0); o["Count"] = 1; }),
        };
        var store = new JsonSettingsStore<Sample>(dir.File("s.json"), 2, migrations);
        Assert.Equal([0, 1], order);
        Assert.Equal(LoadStatus.Migrated, store.LoadInfo.Status);
        Assert.Equal(11, store.Current.Count);
        Assert.Equal("""{ "Name": "old" }""", File.ReadAllText(dir.File("s.json.v0.bak")));
        Assert.Equal(2, (int)JsonNode.Parse(File.ReadAllText(dir.File("s.json")))!["SchemaVersion"]!);
    }

    [Fact]
    public void A_second_start_does_not_migrate_again_or_replace_the_backup()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("s.json"), """{ "Name": "old" }""");
        _ = new JsonSettingsStore<Sample>(dir.File("s.json"), 1);
        var second = new JsonSettingsStore<Sample>(dir.File("s.json"), 1);
        Assert.Equal(LoadStatus.Loaded, second.LoadInfo.Status);
        Assert.Equal("""{ "Name": "old" }""", File.ReadAllText(dir.File("s.json.v0.bak")));
    }

    [Theory]
    [InlineData("0.3.0")]
    [InlineData("0.4.0")]
    public void The_viewer_json_of_older_versions_still_loads(string version)
    {
        _ = version;   // the settings class and its serialisation are identical in both releases' source, so one captured sample stands for both
        using var dir = new TempDir();
        File.WriteAllText(dir.File("viewer.json"), Viewer040);
        var store = new JsonSettingsStore<ViewerSettingsData>(dir.File("viewer.json"), ViewerSettingsData.SchemaVersion, ViewerSettingsData.Migrations);
        var s = store.Current;
        Assert.Equal(LoadStatus.Migrated, store.LoadInfo.Status);
        Assert.Equal("wss://server.example:8443", s.ServerUri);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", s.ServerPin);
        Assert.Equal("OFFICE-PC", s.DeviceName);
        Assert.Equal("dark", s.Theme);
        Assert.False(s.SyncClipboard);
        Assert.True(s.IsConfigured);
        // everything added later has its default
        Assert.Equal("system", s.Language);
        Assert.Equal("auto", s.KeyboardMode);
        Assert.Equal(8, s.ClipboardConfirmMegabytes);
        Assert.True(s.FileTransferEnabled);
        Assert.False(s.CloseToTray);
        Assert.False(s.OnboardingDone);
        Assert.True(File.Exists(dir.File("viewer.json.v0.bak")));
        var rewritten = JsonNode.Parse(File.ReadAllText(dir.File("viewer.json")))!.AsObject();
        Assert.False(rewritten.ContainsKey("IsConfigured"));
        Assert.Equal(1, (int)rewritten["SchemaVersion"]!);
    }

    [Fact]
    public void The_new_file_can_still_be_read_by_the_old_class()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("viewer.json"), Viewer040);
        var store = new JsonSettingsStore<ViewerSettingsData>(dir.File("viewer.json"), ViewerSettingsData.SchemaVersion, ViewerSettingsData.Migrations);
        store.Update(s => s.Theme = "light");
        var old = JsonSerializer.Deserialize<LegacyViewerSettings>(File.ReadAllText(dir.File("viewer.json")))!;
        Assert.Equal("light", old.Theme);
        Assert.Equal("OFFICE-PC", old.DeviceName);
    }

    [Fact]
    public void A_file_with_the_shell_keys_of_the_current_build_loads_unchanged()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("viewer.json"), """{ "ServerUri": "", "CloseToTray": true, "StartWithWindows": true, "OnboardingDone": true, "WindowX": 10, "WindowWidth": 900, "IsConfigured": false }""");
        var s = new JsonSettingsStore<ViewerSettingsData>(dir.File("viewer.json"), ViewerSettingsData.SchemaVersion, ViewerSettingsData.Migrations).Current;
        Assert.True(s.CloseToTray && s.StartWithWindows && s.OnboardingDone);
        Assert.Equal(10, s.WindowX);
        Assert.Equal(900, s.WindowWidth);
    }

    [Fact]
    public void Out_of_range_values_in_a_file_are_fixed_on_load()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("viewer.json"), """{ "Theme": "purple", "KeyboardMode": "x", "Language": "fr", "ClipboardConfirmMegabytes": 99999, "DeviceName": " " }""");
        var s = new JsonSettingsStore<ViewerSettingsData>(dir.File("viewer.json"), 1).Current;
        Assert.Equal("system", s.Theme);
        Assert.Equal("auto", s.KeyboardMode);
        Assert.Equal("system", s.Language);
        Assert.Equal(1024, s.ClipboardConfirmMegabytes);
        Assert.Equal(Environment.MachineName, s.DeviceName);
    }

    [Fact]
    public void Property_names_are_read_without_regard_to_case()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("s.json"), """{ "schemaVersion": 1, "name": "lower" }""");
        Assert.Equal("lower", new JsonSettingsStore<Sample>(dir.File("s.json"), 1).Current.Name);
    }

    // ---- address book ----

    private static JsonAddressBook Book(TempDir dir, Func<DateTimeOffset>? now = null) => new(dir.File("address-book.json"), now);

    [Fact]
    public void Adding_saves_and_a_second_add_of_the_same_address_returns_the_first()
    {
        using var dir = new TempDir();
        var book = Book(dir);
        var a = book.Add("Office", "192.168.1.5", "Work");
        var b = book.Add("Other name", " 192.168.1.5 ");
        Assert.Equal(a.Id, b.Id);
        Assert.Single(book.All);
        Assert.Equal("Work", Book(dir).All[0].Group);
        Assert.Throws<ArgumentException>(() => book.Add("x", "  "));
    }

    [Fact]
    public void Update_remove_and_mark_used_work_and_are_saved()
    {
        using var dir = new TempDir();
        var t = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var book = Book(dir, () => t);
        var e = book.Add("A", "a.local");
        book.Update(e with { Notes = "  corner desk ", Favorite = true, ColorTag = "GREEN", Group = "Home" });
        book.MarkUsed(e.Id);
        var back = Book(dir).All.Single();
        Assert.Equal("corner desk", back.Notes);
        Assert.True(back.Favorite);
        Assert.Equal("green", back.ColorTag);
        Assert.Equal(t, back.LastUsedUtc);
        book.Remove(e.Id);
        Assert.Empty(Book(dir).All);
    }

    [Fact]
    public void An_unknown_colour_is_dropped_and_long_text_is_cut()
    {
        using var dir = new TempDir();
        var book = Book(dir);
        var e = book.Add("A", "a.local");
        book.Update(e with { ColorTag = "pink", Notes = new string('n', 5000) });
        var back = book.All.Single();
        Assert.Null(back.ColorTag);
        Assert.Equal(JsonAddressBook.MaxNotes, back.Notes!.Length);
    }

    [Fact]
    public void Two_entries_cannot_share_an_address()
    {
        using var dir = new TempDir();
        var book = Book(dir);
        book.Add("A", "a.local");
        var b = book.Add("B", "b.local");
        Assert.Throws<InvalidOperationException>(() => book.Update(b with { Target = "A.LOCAL" }));
        Assert.Throws<KeyNotFoundException>(() => book.Update(b with { Id = "nope" }));
    }

    [Fact]
    public void Changed_is_raised_for_every_kind_of_change()
    {
        using var dir = new TempDir();
        var book = Book(dir);
        var count = 0;
        book.Changed += () => count++;
        var e = book.Add("A", "a.local");
        book.Update(e with { Favorite = true });
        book.MarkUsed(e.Id);
        book.Remove(e.Id);
        Assert.Equal(4, count);
    }

    [Fact]
    public void Export_then_import_into_a_new_book_keeps_everything_but_the_ids_are_free()
    {
        using var dir = new TempDir();
        var source = Book(dir);
        var e = source.Add("A", "a.local", "Work");
        source.Update(e with { Notes = "n", Favorite = true, ColorTag = "red" });
        source.MarkUsed(e.Id);
        using var other = new TempDir();
        var target = Book(other);
        target.Import(source.Export());
        var copy = target.All.Single();
        Assert.Equal("A", copy.Name);
        Assert.Equal("Work", copy.Group);
        Assert.Equal("n", copy.Notes);
        Assert.True(copy.Favorite);
        Assert.Equal("red", copy.ColorTag);
        Assert.NotNull(copy.LastUsedUtc);
    }

    [Fact]
    public void Import_merges_by_address_and_counts_what_it_did()
    {
        using var dir = new TempDir();
        var book = Book(dir);
        var mine = book.Add("Mine", "a.local");
        book.Update(mine with { LastUsedUtc = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero) });
        var json = $$"""
            { "Format": "remotely-address-book", "Version": 1, "Entries": [
              { "Id": "x1", "Name": "Renamed", "Target": "A.local", "Group": "G", "Favorite": true, "LastUsedUtc": "2026-01-01T00:00:00+00:00" },
              { "Id": "{{mine.Id}}", "Name": "New", "Target": "b.local" },
              { "Id": "x3", "Name": "No address", "Target": "  " }
            ] }
            """;
        var result = book.ImportWithResult(json);
        Assert.Equal(new AddressBookImportResult(1, 1, 1), result);
        var a = book.All.Single(e => e.Target == "a.local");
        Assert.Equal(mine.Id, a.Id);
        Assert.Equal("Renamed", a.Name);
        Assert.True(a.Favorite);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), a.LastUsedUtc);   // the later time wins
        var b = book.All.Single(e => e.Target == "b.local");
        Assert.NotEqual(mine.Id, b.Id);   // its id was taken, so it got a new one
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "Format": "something-else", "Version": 1, "Entries": [] }""")]
    [InlineData("""{ "Format": "remotely-address-book", "Version": 2, "Entries": [] }""")]
    [InlineData("""{ "Format": "remotely-address-book", "Version": 1 }""")]
    [InlineData("""{ "Format": 5, "Version": 1, "Entries": [] }""")]
    public void A_file_that_is_not_an_export_changes_nothing(string json)
    {
        using var dir = new TempDir();
        var book = Book(dir);
        book.Add("A", "a.local");
        Assert.Throws<FormatException>(() => book.Import(json));
        Assert.Single(book.All);
    }

    [Fact]
    public void A_damaged_address_book_file_starts_empty_and_keeps_the_bad_file()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("address-book.json"), "garbage");
        var book = Book(dir);
        Assert.Empty(book.All);
        Assert.Equal(LoadStatus.Damaged, book.LoadInfo.Status);
        Assert.True(File.Exists(dir.File("address-book.json.damaged")));
    }

    [Fact]
    public void A_hand_edited_book_with_repeated_ids_and_missing_addresses_is_repaired()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("address-book.json"), """
            { "SchemaVersion": 1, "Entries": [
              { "Id": "same", "Name": "A", "Target": "a.local" },
              { "Id": "same", "Name": "B", "Target": "b.local" },
              { "Id": "c", "Name": "C", "Target": "" },
              { "Id": "d", "Name": "D", "Target": "A.LOCAL" } ] }
            """);
        var all = Book(dir).All;
        Assert.Equal(2, all.Count);
        Assert.Equal(2, all.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Groups_lists_each_group_once_sorted()
    {
        using var dir = new TempDir();
        var book = Book(dir);
        book.Add("1", "1.local", "work");
        book.Add("2", "2.local", "Home");
        book.Add("3", "3.local", "WORK");
        book.Add("4", "4.local");
        Assert.Equal(["Home", "work"], book.Groups);
    }

    [Fact]
    public void The_address_book_is_limited_in_size()
    {
        using var dir = new TempDir();
        var book = Book(dir);
        for (var i = 0; i < JsonAddressBook.MaxEntries; i++) book.Add("n" + i, "h" + i);
        Assert.Throws<InvalidOperationException>(() => book.Add("x", "overflow"));
    }

    // ---- recent PCs on the address book ----

    [Fact]
    public void Recent_pcs_are_the_used_entries_newest_first_and_at_most_eight()
    {
        using var dir = new TempDir();
        var t = DateTimeOffset.UtcNow;
        var clock = 0;
        var book = Book(dir, () => t.AddMinutes(clock++));
        var recent = new AddressBookRecentPcs(book);
        book.Add("Never used", "never.local");
        for (var i = 0; i < 10; i++) recent.Record("pc" + i, "pc" + i + ".local");
        recent.Record("pc3", "PC3.local");
        var list = recent.List();
        Assert.Equal(AddressBookRecentPcs.MaxEntries, list.Count);
        Assert.Equal("pc3.local", list[0].Address);
        Assert.DoesNotContain(list, p => p.Address == "never.local");
    }

    [Fact]
    public void Forgetting_a_recent_pc_keeps_what_the_person_wrote()
    {
        using var dir = new TempDir();
        var book = Book(dir);
        var recent = new AddressBookRecentPcs(book);
        recent.Record("plain", "plain.local");
        recent.Record("fav", "fav.local");
        book.Update(book.All.Single(e => e.Target == "fav.local") with { Favorite = true });
        recent.Remove("plain.local");
        recent.Remove("fav.local");
        Assert.Empty(recent.List());
        Assert.Equal("fav.local", book.All.Single().Target);
    }

    [Fact]
    public void The_old_recent_list_is_copied_into_the_book()
    {
        using var dir = new TempDir();
        var legacy = new JsonRecentPcs(dir.File("recent-pcs.json"));
        legacy.Record("one", "one.local");
        legacy.Record("two", "two.local");
        var book = Book(dir);
        Assert.Equal(2, AddressBookRecentPcs.ImportLegacy(book, legacy));
        Assert.Equal(["two.local", "one.local"], new AddressBookRecentPcs(book).List().Select(p => p.Address));
        Assert.Equal(0, AddressBookRecentPcs.ImportLegacy(book, legacy));
    }
}
