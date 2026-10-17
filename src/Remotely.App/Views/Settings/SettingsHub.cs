using Remotely.App.Services;
using Remotely.App.Session.Files;
using Remotely.Core.Storage;

namespace Remotely.App.Views.Settings;

public enum ApplyOutcome
{
    Saved,
    /// <summary>Applied, but writing the file failed (disk full, file locked); the choice is lost when Remotely closes.</summary>
    SaveFailed,
    /// <summary>Applied and saved; it takes effect the next time Remotely starts.</summary>
    NeedsRestart,
    /// <summary>Windows refused the change (start with Windows is the case today); the choice was put back.</summary>
    Refused,
}

/// <summary>
/// Joins the settings catalog to the places the app keeps its values: viewer.json (<see cref="App.Settings"/>), the share coordinator's settings, and transfers.json.
/// It also runs the side effects a change needs (theme, start with Windows, language).
/// </summary>
internal static class SettingsHub
{
    public static PolicyOverrides Policy() => PolicyOverrides.ReadMachine();

    /// <summary>The live objects the pages edit. Viewer and share settings are the app's own instances; transfers are read from disk.</summary>
    public static SettingsSet Load()
    {
        var transfers = FileTransferSettings.Load();
        return new SettingsSet
        {
            Viewer = App.Settings,
            Share = App.Share.Coordinator.Settings,
            Transfers = new FileTransferPrefs
            {
                ReceiveFolder = transfers.ReceiveFolder,
                ReceiveQuotaMegabytes = transfers.ReceiveQuotaMegabytes,
                MaxItemMegabytes = transfers.MaxItemMegabytes,
                ExtractFolders = transfers.ExtractFolders,
            },
        };
    }

    /// <summary>Applies one value that already passed <see cref="SettingDef.TryNormalize"/>, saves it where it lives and runs its side effect.</summary>
    public static ApplyOutcome Apply(SettingDef def, SettingsSet set, object value)
    {
        var previous = def.Get(set);
        def.Set(set, value);

        if (def.Id == "StartWithWindows" && !WindowsStartup.Set((bool)value))
        {
            def.Set(set, previous);
            return ApplyOutcome.Refused;
        }

        var saved = Persist(def, set);
        switch (def.Id)
        {
            case "Theme":
                if (App.Main is { } main) App.ApplyTheme(main);
                break;
            case "Language":
                ApplyLanguage(set.Viewer.Language);
                return saved ? ApplyOutcome.NeedsRestart : ApplyOutcome.SaveFailed;
        }
        return saved ? ApplyOutcome.Saved : ApplyOutcome.SaveFailed;
    }

    /// <summary>Writes the file that holds this setting. False when the disk refused.</summary>
    public static bool Persist(SettingDef def, SettingsSet set)
    {
        try
        {
            switch (def.Storage)
            {
                case "share-settings.json":
                    if (def.Id == "AskBeforeConnect") App.Share.Coordinator.SetAskBeforeConnect(set.Share.AskBeforeConnect);
                    else App.Share.Coordinator.SetAllowRemembered(set.Share.AllowRemembered);
                    break;
                case "transfers.json":
                    SaveTransfers(set.Transfers);
                    break;
                default:
                    set.Viewer.Save();
                    break;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>After an import or a reset: writes all three files and puts the side effects in line with the new values.</summary>
    public static bool PersistAll(SettingsSet set)
    {
        var ok = true;
        foreach (var storage in SettingsCatalog.All.GroupBy(d => d.Storage).Select(g => g.First())) ok &= Persist(storage, set);
        if (App.Main is { } main) App.ApplyTheme(main);
        if (!WindowsStartup.Set(set.Viewer.StartWithWindows)) set.Viewer.StartWithWindows = WindowsStartup.IsEnabled;
        ApplyLanguage(set.Viewer.Language);
        // The two share values are separate files' worth of state in one coordinator; Persist wrote the first, so write the second too.
        App.Share.Coordinator.SetAllowRemembered(set.Share.AllowRemembered);
        return ok;
    }

    private static void SaveTransfers(FileTransferPrefs prefs)
    {
        var file = FileTransferSettings.Load();
        file.ReceiveFolder = prefs.ReceiveFolder;
        file.ReceiveQuotaMegabytes = prefs.ReceiveQuotaMegabytes;
        file.MaxItemMegabytes = prefs.MaxItemMegabytes;
        file.ExtractFolders = prefs.ExtractFolders;
        file.Save();
    }

    /// <summary>Tells Windows which UI language to prefer for this app. UNVERIFIED on a real PC: the call is made in a try block so a failure leaves the choice saved and the language unchanged.</summary>
    public static void ApplyLanguage(string language)
    {
        try { Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language == "system" ? "" : language; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException) { App.LogCrash(ex); }
    }
}
