using Remotely.Core.Lan.Consent;

namespace Remotely.Core.Storage;

public enum SettingsSection { General, Sharing, Connection, Security, Appearance, Advanced, About, AddressBook }

public enum SettingKind { Toggle, Choice, Number, Text, Folder }

/// <summary>
/// The file-transfer values in <c>transfers.json</c>, same names and defaults as the app's <c>FileTransferSettings</c> (agent A8), which reads and writes that file.
/// This copy exists so the settings catalog can be tested without the WinUI project; a test compares the defaults with the source of the original.
/// </summary>
public sealed class FileTransferPrefs
{
    public const long MaxItemMegabytesDefault = 32 * 1024;

    public string ReceiveFolder { get; set; } = "";
    public long ReceiveQuotaMegabytes { get; set; }
    public long MaxItemMegabytes { get; set; } = MaxItemMegabytesDefault;
    public bool ExtractFolders { get; set; } = true;
}

/// <summary>The three files the settings pages edit: viewer.json, share-settings.json and transfers.json.</summary>
public sealed class SettingsSet
{
    public ViewerSettingsData Viewer { get; init; } = new();
    public ShareSettings Share { get; init; } = new();
    public FileTransferPrefs Transfers { get; init; } = new();
}

public sealed record SettingChoice(string Value, string LabelKey);

/// <summary>
/// One setting the person can change: where it lives, its default, its limits, and the resource keys of its label and help text
/// (<c>Settings_{Id}_Label</c> and <c>Settings_{Id}_Help</c>). The pages are built from this list, so a setting cannot appear without a default and a help string.
/// </summary>
public sealed class SettingDef
{
    public required string Id { get; init; }
    public required SettingsSection Section { get; init; }
    public required SettingKind Kind { get; init; }
    public required object Default { get; init; }
    public required Func<SettingsSet, object> Get { get; init; }
    public required Action<SettingsSet, object> Set { get; init; }
    public IReadOnlyList<SettingChoice> Choices { get; init; } = [];
    public long Min { get; init; }
    public long Max { get; init; } = long.MaxValue;
    /// <summary>False for values that belong to this PC (its name) and are left out of an exported settings file.</summary>
    public bool Portable { get; init; } = true;
    /// <summary>The value an administrator's policy forces, or null when no policy applies. Locked in the page and ignored on import.</summary>
    public Func<PolicyOverrides, object?>? ForcedBy { get; init; }

    public string LabelKey => $"Settings_{Id}_Label";
    public string HelpKey => $"Settings_{Id}_Help";

    public string Storage => this switch
    {
        _ when Id is "AskBeforeConnect" or "AllowRemembered" => "share-settings.json",
        _ when Id is "ReceiveFolder" or "ReceiveQuotaMegabytes" or "MaxItemMegabytes" or "ExtractFolders" => "transfers.json",
        _ => "viewer.json",
    };

    /// <summary>Checks a value of the right type against the limits. Returns the value to store (numbers are not silently clamped: out of range is refused).</summary>
    public bool TryNormalize(object? value, out object normalized)
    {
        normalized = Default;
        switch (Kind)
        {
            case SettingKind.Toggle:
                if (value is not bool b) return false;
                normalized = b;
                return true;
            case SettingKind.Number:
                if (value is not (int or long)) return false;
                var n = Convert.ToInt64(value);
                if (n < Min || n > Max) return false;
                normalized = Default is int ? (object)(int)n : n;
                return true;
            case SettingKind.Choice:
                if (value is not string c || !Choices.Any(x => x.Value == c)) return false;
                normalized = c;
                return true;
            default:
                if (value is not string s || s.Length > 260) return false;
                normalized = s.Trim();
                return true;
        }
    }
}

/// <summary>Every setting that has a screen, in the order the pages show them.</summary>
public static class SettingsCatalog
{
    private static SettingDef Toggle(string id, SettingsSection section, bool def, Func<SettingsSet, bool> get, Action<SettingsSet, bool> set, Func<PolicyOverrides, object?>? forcedBy = null) => new()
    {
        Id = id, Section = section, Kind = SettingKind.Toggle, Default = def,
        Get = s => get(s), Set = (s, v) => set(s, (bool)v), ForcedBy = forcedBy,
    };

    private static SettingDef Choice(string id, SettingsSection section, string def, Func<SettingsSet, string> get, Action<SettingsSet, string> set, params (string Value, string LabelKey)[] choices) => new()
    {
        Id = id, Section = section, Kind = SettingKind.Choice, Default = def,
        Get = s => get(s), Set = (s, v) => set(s, (string)v), Choices = [.. choices.Select(c => new SettingChoice(c.Value, c.LabelKey))],
    };

    public static IReadOnlyList<SettingDef> All { get; } =
    [
        // General
        new SettingDef
        {
            Id = "DeviceName", Section = SettingsSection.General, Kind = SettingKind.Text, Default = Environment.MachineName, Portable = false,
            Get = s => s.Viewer.DeviceName, Set = (s, v) => s.Viewer.DeviceName = string.IsNullOrWhiteSpace((string)v) ? Environment.MachineName : (string)v,
        },
        Toggle("StartWithWindows", SettingsSection.General, false, s => s.Viewer.StartWithWindows, (s, v) => s.Viewer.StartWithWindows = v),
        Toggle("CloseToTray", SettingsSection.General, false, s => s.Viewer.CloseToTray, (s, v) => s.Viewer.CloseToTray = v),
        Choice("Language", SettingsSection.General, "system", s => s.Viewer.Language, (s, v) => s.Viewer.Language = v,
            ("system", "Settings_Choice_LanguageSystem"), ("en-US", "Settings_Choice_LanguageEnglish"), ("sq-AL", "Settings_Choice_LanguageAlbanian")),

        // Sharing
        Toggle("AskBeforeConnect", SettingsSection.Sharing, true, s => s.Share.AskBeforeConnect, (s, v) => s.Share.AskBeforeConnect = v, p => p.ForceConsent ? true : null),
        Toggle("AllowRemembered", SettingsSection.Sharing, true, s => s.Share.AllowRemembered, (s, v) => s.Share.AllowRemembered = v),
        Toggle("ShareOnStart", SettingsSection.Sharing, false, s => s.Viewer.ShareOnStart, (s, v) => s.Viewer.ShareOnStart = v),

        // Connection
        Choice("KeyboardMode", SettingsSection.Connection, "auto", s => s.Viewer.KeyboardMode, (s, v) => s.Viewer.KeyboardMode = v,
            ("auto", "Settings_Choice_KeyboardAuto"), ("scancode", "Settings_Choice_KeyboardScanCode"), ("unicode", "Settings_Choice_KeyboardUnicode")),
        Toggle("SyncClipboard", SettingsSection.Connection, true, s => s.Viewer.SyncClipboard, (s, v) => s.Viewer.SyncClipboard = v),
        Toggle("FileTransferEnabled", SettingsSection.Connection, true, s => s.Viewer.FileTransferEnabled, (s, v) => s.Viewer.FileTransferEnabled = v, p => p.DisableFileTransfer ? false : null),
        new SettingDef
        {
            Id = "ReceiveFolder", Section = SettingsSection.Connection, Kind = SettingKind.Folder, Default = "",
            Get = s => s.Transfers.ReceiveFolder, Set = (s, v) => s.Transfers.ReceiveFolder = (string)v,
        },
        new SettingDef
        {
            Id = "MaxItemMegabytes", Section = SettingsSection.Connection, Kind = SettingKind.Number, Default = FileTransferPrefs.MaxItemMegabytesDefault, Min = 1, Max = 1024L * 1024,
            Get = s => s.Transfers.MaxItemMegabytes, Set = (s, v) => s.Transfers.MaxItemMegabytes = (long)v,
        },
        new SettingDef
        {
            Id = "ReceiveQuotaMegabytes", Section = SettingsSection.Connection, Kind = SettingKind.Number, Default = 0L, Min = 0, Max = 16L * 1024 * 1024,
            Get = s => s.Transfers.ReceiveQuotaMegabytes, Set = (s, v) => s.Transfers.ReceiveQuotaMegabytes = (long)v,
        },
        Toggle("ExtractFolders", SettingsSection.Connection, true, s => s.Transfers.ExtractFolders, (s, v) => s.Transfers.ExtractFolders = v),

        // Security
        new SettingDef
        {
            Id = "ClipboardConfirmMegabytes", Section = SettingsSection.Security, Kind = SettingKind.Number, Default = ViewerSettingsData.ClipboardConfirmMegabytesDefault,
            Min = ViewerSettingsData.ClipboardConfirmMegabytesMin, Max = ViewerSettingsData.ClipboardConfirmMegabytesMax,
            Get = s => s.Viewer.ClipboardConfirmMegabytes, Set = (s, v) => s.Viewer.ClipboardConfirmMegabytes = (int)v,
        },

        // Appearance
        Choice("Theme", SettingsSection.Appearance, "system", s => s.Viewer.Theme, (s, v) => s.Viewer.Theme = v,
            ("system", "Settings_Choice_ThemeSystem"), ("dark", "Settings_Choice_ThemeDark"), ("light", "Settings_Choice_ThemeLight")),
    ];

    public static SettingDef? Find(string id) => All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<SettingDef> In(SettingsSection section) => All.Where(d => d.Section == section);

    /// <summary>Sets every catalogued setting back to its default. Server, pairing, first-run state, window position and the address book are not settings in this list and stay.</summary>
    public static void ResetAll(SettingsSet set)
    {
        foreach (var d in All) d.Set(set, d.Default);
    }

    /// <summary>The value in effect: the policy's when one applies, else the person's.</summary>
    public static object Effective(SettingDef def, SettingsSet set, PolicyOverrides policy) => def.ForcedBy?.Invoke(policy) ?? def.Get(set);
}
