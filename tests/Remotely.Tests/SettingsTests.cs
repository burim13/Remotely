using System.Text.RegularExpressions;
using System.Xml.Linq;
using Remotely.Core.Storage;

namespace Remotely.Tests;

public class SettingsTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static Dictionary<string, string> Strings(string language) =>
        XDocument.Load(Path.Combine(Root(), "src", "Remotely.App", "Strings", language, "Resources.resw")).Root!.Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);

    // A valid value that differs from the default, for every catalogued setting. A new setting without an entry here fails Every_setting_has_a_test_value.
    private static readonly Dictionary<string, object> Other = new()
    {
        ["DeviceName"] = "KITCHEN-PC",
        ["StartWithWindows"] = true,
        ["CloseToTray"] = true,
        ["Language"] = "sq-AL",
        ["AskBeforeConnect"] = false,
        ["AllowRemembered"] = false,
        ["ShareOnStart"] = true,
        ["KeyboardMode"] = "unicode",
        ["SyncClipboard"] = false,
        ["FileTransferEnabled"] = false,
        ["ReceiveFolder"] = @"D:\Received",
        ["MaxItemMegabytes"] = 100L,
        ["ReceiveQuotaMegabytes"] = 500L,
        ["ExtractFolders"] = false,
        ["ClipboardConfirmMegabytes"] = 64,
        ["Theme"] = "light",
    };

    public static IEnumerable<object[]> Ids() => SettingsCatalog.All.Select(d => new object[] { d.Id });

    [Fact]
    public void Every_setting_has_a_test_value() => Assert.Empty(SettingsCatalog.All.Select(d => d.Id).Except(Other.Keys));

    [Fact]
    public void Ids_are_unique_and_every_section_with_settings_is_known()
    {
        Assert.Equal(SettingsCatalog.All.Count, SettingsCatalog.All.Select(d => d.Id).Distinct().Count());
        Assert.Contains(SettingsCatalog.All, d => d.Section == SettingsSection.Security);
        Assert.NotEmpty(SettingsCatalog.In(SettingsSection.General));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void The_default_is_what_a_new_settings_object_holds(string id)
    {
        var def = SettingsCatalog.Find(id)!;
        Assert.Equal(def.Default, def.Get(new SettingsSet()));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void A_value_can_be_set_read_back_and_set_to_default_again(string id)
    {
        var def = SettingsCatalog.Find(id)!;
        var set = new SettingsSet();
        Assert.True(def.TryNormalize(Other[id], out var value));
        def.Set(set, value);
        Assert.Equal(Other[id], def.Get(set));
        Assert.NotEqual(def.Default, def.Get(set));
        def.Set(set, def.Default);
        Assert.Equal(def.Default, def.Get(set));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Every_setting_has_a_label_and_a_help_string_in_both_languages(string id)
    {
        var def = SettingsCatalog.Find(id)!;
        foreach (var language in new[] { "en-US", "sq-AL" })
        {
            var s = Strings(language);
            Assert.True(s.TryGetValue(def.LabelKey, out var label) && label.Length > 0, language + " " + def.LabelKey);
            Assert.True(s.TryGetValue(def.HelpKey, out var help) && help.Length > 20, language + " " + def.HelpKey);
            foreach (var choice in def.Choices) Assert.True(s.ContainsKey(choice.LabelKey), language + " " + choice.LabelKey);
        }
    }

    [Fact]
    public void Wrong_types_and_out_of_range_values_are_refused()
    {
        Assert.False(SettingsCatalog.Find("CloseToTray")!.TryNormalize("yes", out _));
        Assert.False(SettingsCatalog.Find("Theme")!.TryNormalize("purple", out _));
        Assert.False(SettingsCatalog.Find("Language")!.TryNormalize(1, out _));
        var clip = SettingsCatalog.Find("ClipboardConfirmMegabytes")!;
        Assert.False(clip.TryNormalize(0L, out _));
        Assert.False(clip.TryNormalize(1025L, out _));
        Assert.True(clip.TryNormalize(1024L, out var max));
        Assert.IsType<int>(max);
        Assert.False(SettingsCatalog.Find("MaxItemMegabytes")!.TryNormalize(0L, out _));
        Assert.True(SettingsCatalog.Find("ReceiveQuotaMegabytes")!.TryNormalize(0L, out _));
        Assert.False(SettingsCatalog.Find("ReceiveFolder")!.TryNormalize(new string('x', 300), out _));
    }

    [Fact]
    public void A_blank_device_name_becomes_the_computer_name()
    {
        var set = new SettingsSet();
        SettingsCatalog.Find("DeviceName")!.Set(set, "  ");
        Assert.Equal(Environment.MachineName, set.Viewer.DeviceName);
    }

    [Fact]
    public void Reset_restores_the_catalogued_settings_and_leaves_the_server_alone()
    {
        var set = new SettingsSet();
        foreach (var d in SettingsCatalog.All) d.Set(set, d.TryNormalize(Other[d.Id], out var v) ? v : d.Default);
        set.Viewer.ServerUri = "wss://keep.me";
        set.Viewer.OnboardingDone = true;
        SettingsCatalog.ResetAll(set);
        foreach (var d in SettingsCatalog.All) Assert.Equal(d.Default, d.Get(set));
        Assert.Equal("wss://keep.me", set.Viewer.ServerUri);
        Assert.True(set.Viewer.OnboardingDone);
    }

    [Fact]
    public void The_file_transfer_defaults_match_the_apps_own_class()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Remotely.App", "Session", "Files", "FileTransferSettings.cs"));
        Assert.Matches(@"MaxItemMegabytes \{ get; set; \} = 32 \* 1024;", source);
        Assert.Matches(@"ExtractFolders \{ get; set; \} = true;", source);
        Assert.Equal(32L * 1024, FileTransferPrefs.MaxItemMegabytesDefault);
        Assert.Equal("", new FileTransferPrefs().ReceiveFolder);
    }

    [Fact]
    public void The_defaults_of_the_other_agents_settings_are_the_ones_the_code_uses()
    {
        var share = new Remotely.Core.Lan.Consent.ConsentOptions();
        var set = new SettingsSet();
        Assert.Equal(share.AskBeforeConnect, set.Share.AskBeforeConnect);
        Assert.Equal(share.AllowRemembered, set.Share.AllowRemembered);
        Assert.Equal(ViewerSettingsData.ClipboardConfirmMegabytesDefault * 1024L * 1024, Remotely.HostCore.ClipLimits.Default.ConfirmAboveBytes);
        Assert.Equal(ViewerSettingsData.KeyboardModes.Select(m => m.ToLowerInvariant()).Order(), Enum.GetNames<Remotely.ViewerCore.KeyboardModeSetting>().Select(m => m.ToLowerInvariant()).Order());
    }

    // ---- export and import ----

    [Fact]
    public void Export_then_import_moves_every_portable_setting()
    {
        var source = new SettingsSet();
        foreach (var d in SettingsCatalog.All) d.Set(source, d.TryNormalize(Other[d.Id], out var v) ? v : d.Default);
        var json = SettingsTransfer.Export(source);
        var target = new SettingsSet();
        var result = SettingsTransfer.Import(json, target, PolicyOverrides.None);
        Assert.Empty(result.Rejected);
        Assert.Empty(result.Unknown);
        foreach (var d in SettingsCatalog.All.Where(d => d.Portable)) Assert.Equal(d.Get(source), d.Get(target));
        Assert.Equal(Environment.MachineName, target.Viewer.DeviceName);
    }

    [Fact]
    public void The_export_holds_no_server_pairing_or_machine_values()
    {
        var set = new SettingsSet();
        set.Viewer.ServerUri = "wss://secret.example";
        set.Viewer.ServerPin = new string('a', 64);
        set.Viewer.DeviceName = "MY-PC";
        var json = SettingsTransfer.Export(set);
        Assert.DoesNotContain("secret.example", json);
        Assert.DoesNotContain(new string('a', 64), json);
        Assert.DoesNotContain("MY-PC", json);
        Assert.DoesNotContain("WindowX", json);
        Assert.DoesNotContain("WindowWidth", json);
        Assert.DoesNotContain("Onboarding", json);
    }

    [Fact]
    public void Import_skips_bad_unknown_and_non_portable_values_and_applies_the_rest()
    {
        var json = """
            { "Format": "remotely-settings", "Version": 1, "Settings": {
              "Theme": "dark", "CloseToTray": "yes", "ClipboardConfirmMegabytes": 5000, "Nope": 1, "DeviceName": "X", "ShareOnStart": true } }
            """;
        var set = new SettingsSet();
        var result = SettingsTransfer.Import(json, set, PolicyOverrides.None);
        Assert.Equal(["Theme", "ShareOnStart"], result.Applied);
        Assert.Equal(["Nope"], result.Unknown);
        Assert.Equal(["CloseToTray", "ClipboardConfirmMegabytes", "DeviceName"], result.Rejected);
        Assert.Equal("dark", set.Viewer.Theme);
        Assert.False(set.Viewer.CloseToTray);
        Assert.Equal(8, set.Viewer.ClipboardConfirmMegabytes);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("[]")]
    [InlineData("""{ "Format": "remotely-address-book", "Version": 1, "Settings": {} }""")]
    [InlineData("""{ "Format": "remotely-settings", "Version": 7, "Settings": {} }""")]
    [InlineData("""{ "Format": "remotely-settings", "Version": 1 }""")]
    [InlineData("""{ "Format": 3, "Version": 1, "Settings": {} }""")]
    public void A_file_that_is_not_ours_changes_nothing(string json)
    {
        var set = new SettingsSet();
        Assert.Throws<FormatException>(() => SettingsTransfer.Import(json, set, PolicyOverrides.None));
        foreach (var d in SettingsCatalog.All) Assert.Equal(d.Default, d.Get(set));
    }

    [Fact]
    public void Import_does_not_override_a_setting_the_policy_forces()
    {
        var json = """{ "Format": "remotely-settings", "Version": 1, "Settings": { "AskBeforeConnect": false, "FileTransferEnabled": false, "Theme": "dark" } }""";
        var set = new SettingsSet();
        var result = SettingsTransfer.Import(json, set, new PolicyOverrides(false, true, false));
        Assert.Equal(["AskBeforeConnect"], result.LockedByPolicy);
        Assert.True(set.Share.AskBeforeConnect);
        Assert.False(set.Viewer.FileTransferEnabled);
    }

    // ---- policy ----

    private static PolicyOverrides Policy(params (string Name, object Value)[] values) =>
        PolicyOverrides.Read(new DictionaryPolicySource(values.ToDictionary(v => v.Name, v => v.Value)));

    [Fact]
    public void Only_a_nonzero_dword_turns_a_policy_on()
    {
        Assert.Equal(PolicyOverrides.None, Policy());
        Assert.Equal(PolicyOverrides.None, Policy(("DisableServerMode", 0), ("ForceConsent", "1"), ("DisableFileTransfer", 1L)));
        Assert.Equal(new PolicyOverrides(true, false, false), Policy(("DisableServerMode", 1)));
        Assert.Equal(new PolicyOverrides(false, true, true), Policy(("ForceConsent", 1), ("DisableFileTransfer", 7)));
        Assert.True(Policy(("ForceConsent", 1)).Any);
        Assert.False(PolicyOverrides.None.Any);
    }

    [Fact]
    public void Effective_values_follow_the_policy()
    {
        var on = new PolicyOverrides(false, true, true);
        Assert.True(on.EffectiveAskBeforeConnect(false));
        Assert.False(on.EffectiveFileTransfer(true));
        Assert.False(PolicyOverrides.None.EffectiveAskBeforeConnect(false));
        Assert.True(PolicyOverrides.None.EffectiveFileTransfer(true));
        var set = new SettingsSet();
        set.Share.AskBeforeConnect = false;
        Assert.Equal(true, SettingsCatalog.Effective(SettingsCatalog.Find("AskBeforeConnect")!, set, on));
        Assert.Equal(false, SettingsCatalog.Effective(SettingsCatalog.Find("AskBeforeConnect")!, set, PolicyOverrides.None));
        Assert.Equal(false, SettingsCatalog.Effective(SettingsCatalog.Find("FileTransferEnabled")!, set, on));
    }

    [Fact]
    public void No_policy_applies_when_the_registry_is_not_there()
    {
        if (!OperatingSystem.IsWindows()) Assert.Equal(PolicyOverrides.None, PolicyOverrides.ReadMachine());
    }

    [Fact]
    public void The_admx_matches_the_values_the_code_reads()
    {
        XNamespace ns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";
        var admx = XDocument.Load(Path.Combine(Root(), "docs", "policy", "Remotely.admx"));
        var adml = XDocument.Load(Path.Combine(Root(), "docs", "policy", "en-US", "Remotely.adml"));
        var policies = admx.Descendants(ns + "policy").ToList();
        Assert.Equal(
            new[] { PolicyOverrides.DisableServerModeValue, PolicyOverrides.ForceConsentValue, PolicyOverrides.DisableFileTransferValue }.Order(),
            policies.Select(p => p.Attribute("valueName")!.Value).Order());
        foreach (var p in policies)
        {
            Assert.Equal(PolicyOverrides.RegistryPath, p.Attribute("key")!.Value);
            Assert.Equal("Machine", p.Attribute("class")!.Value);
            Assert.Equal("1", p.Element(ns + "enabledValue")!.Element(ns + "decimal")!.Attribute("value")!.Value);
            Assert.Equal("0", p.Element(ns + "disabledValue")!.Element(ns + "decimal")!.Attribute("value")!.Value);
            Assert.Equal(p.Attribute("name")!.Value, p.Attribute("valueName")!.Value);
        }
        var defined = adml.Descendants(ns + "string").Select(s => s.Attribute("id")!.Value).ToHashSet();
        var used = Regex.Matches(admx.ToString(), @"\$\(string\.([A-Za-z0-9_]+)\)").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.NotEmpty(used);
        Assert.Empty(used.Except(defined));
        var supported = admx.Descendants(ns + "definition").Select(d => d.Attribute("name")!.Value).ToHashSet();
        Assert.All(policies, p => Assert.Contains(p.Element(ns + "supportedOn")!.Attribute("ref")!.Value, supported));
        var categories = admx.Descendants(ns + "category").Select(c => c.Attribute("name")!.Value).ToHashSet();
        Assert.All(policies, p => Assert.Contains(p.Element(ns + "parentCategory")!.Attribute("ref")!.Value, categories));
    }

    // ---- search ----

    [Theory]
    [InlineData("", true)]
    [InlineData("tray", true)]
    [InlineData("TRAY clos", true)]
    [InlineData("tray banana", false)]
    [InlineData("  window  ", true)]
    public void Search_needs_every_word_in_the_text(string query, bool expected) =>
        Assert.Equal(expected, SettingsSearch.Matches(query, "Keep running in the tray", "Closing the window hides Remotely"));

    // ---- strings and pages ----

    [Fact]
    public void Every_Settings_string_the_code_asks_for_exists_in_both_languages()
    {
        var files = Directory.GetFiles(Path.Combine(Root(), "src", "Remotely.App", "Views", "Settings"), "*.cs");
        var literal = files.SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(Settings_[A-Za-z_]+)\"").Select(m => m.Groups[1].Value)).ToHashSet();
        var keys = new HashSet<string>(literal.Where(k => !k.EndsWith('_')));
        foreach (var s in Enum.GetValues<SettingsSection>())
        {
            keys.Add("Settings_Section_" + s);
            keys.Add("Settings_Intro_" + s);
        }
        foreach (var c in AddressBookTags.Names.Append("none")) keys.Add("Settings_AddressBook_Color_" + c);
        foreach (var language in new[] { "en-US", "sq-AL" })
            Assert.Empty(keys.Except(Strings(language).Keys));
    }

    [Fact]
    public void Albanian_Settings_strings_keep_their_placeholders_and_are_marked_machine_written()
    {
        var en = Strings("en-US");
        var sq = Strings("sq-AL");
        foreach (var key in en.Keys.Where(k => k.StartsWith("Settings_")))
        {
            string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Distinct().Order());
            Assert.Equal(Holes(en[key]), Holes(sq[key]));
        }
        var text = File.ReadAllText(Path.Combine(Root(), "src", "Remotely.App", "Strings", "sq-AL", "Resources.resw"));
        Assert.Contains("A14: settings pages and address book. MACHINE-WRITTEN Albanian", text);
    }

    [Fact]
    public void Every_settings_page_listed_for_CI_has_a_xaml_file_and_a_matching_class()
    {
        var folder = Path.Combine(Root(), "src", "Remotely.App", "Views", "Settings");
        var listed = File.ReadAllLines(Path.Combine(Root(), "tools", "ci", "pages.txt")).Select(l => l.Trim()).Where(l => l.StartsWith("Settings") && l != "SettingsPage").ToList();
        Assert.Equal(8, listed.Count);
        foreach (var page in listed)
        {
            var xaml = File.ReadAllText(Path.Combine(folder, page + ".xaml"));
            Assert.Contains($"x:Class=\"Remotely.App.Views.Settings.{page}\"", xaml);
            Assert.Matches($@"partial class {page} : Page", File.ReadAllText(Path.Combine(folder, page + ".xaml.cs")));
        }
        Assert.Contains("SettingsPage", File.ReadAllLines(Path.Combine(Root(), "tools", "ci", "pages.txt")).Select(l => l.Trim()));
    }

    [Fact]
    public void The_settings_page_registers_itself_under_the_settings_tag()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "src", "Remotely.App", "Views", "Settings", "SettingsPage.xaml.cs"));
        Assert.Contains("[NavigationPage(NavigationCatalog.SettingsTag,", code);
        Assert.Equal("settings", Remotely.App.Shell.NavigationCatalog.SettingsTag);
        Assert.False(File.Exists(Path.Combine(Root(), "src", "Remotely.App", "SettingsPage.xaml")));
    }
}
